# 0005: Add status history with an explicit store upgrade

## Status

**Accepted — implemented for the local Phase 4 workflow.** Reviewed in PR #16 following the [Phase 4 requirements](../requirements/phase-4-status-history.md), reviewed in PR #15. At Phase 4, the executable initialized/hosted version 2 and explicitly upgraded version 1.

[ADR 0007](0007-adopt-ef-core-code-first.md) now implements EF Core Code First and format 4 with validated adoption of versions 1–3. This record preserves the historical format and direct-access decision; use the current walkthrough for executable commands.

This amends [ADR 0004](0004-use-sqlite-for-the-durable-local-workflow.md): introduce history and an explicit version-1 → version-2 upgrade. Retain SQLite, the current application boundaries, connection ownership, synchronization, journaling and recovery limits. It does not supersede the whole storage decision.

[ADR 0006](0006-bind-creation-keys-in-the-local-case-store.md) is the implemented Phase 5 amendment for creation-key bindings and direct version-1/version-2 → version-3 upgrades. This record retains the historical version-2 decision; current commands are documented in the walkthrough.

## Context

At decision time, the `cases` table stored only the latest status. Phase 4 requires durable history, atomic case/history writes, an honest baseline for older cases and a bounded history read. Earlier transitions cannot be reconstructed from version-1 data.

The lifecycle remains `New → InReview → Closed`, so each case needs at most three entries. A single API process still owns one local store. These requirements do not establish a need for a database server, ORM, event broker, event sourcing or a separate persistence service.

## Decision

### Schema and connection settings

Retain the version-1 `cases` table and its exact existing definition. Add one `STRICT` table and set application-owned `PRAGMA user_version` to `2` only in the transaction that creates the complete new format. Use the same history-table definition for fresh initialization and upgrade:

```sql
CREATE TABLE case_status_history (
    case_id TEXT NOT NULL,
    sequence INTEGER NOT NULL CHECK (sequence BETWEEN 1 AND 3),
    kind TEXT NOT NULL CHECK (kind IN ('Created', 'Baseline', 'StatusChanged')),
    from_status TEXT CHECK (from_status IS NULL OR from_status IN ('New', 'InReview', 'Closed')),
    to_status TEXT NOT NULL CHECK (to_status IN ('New', 'InReview', 'Closed')),
    recorded_at_utc TEXT NOT NULL,
    PRIMARY KEY (case_id, sequence),
    FOREIGN KEY (case_id) REFERENCES cases(id),
    CHECK (
        (sequence = 1 AND kind = 'Created' AND from_status IS NULL AND to_status = 'New')
        OR (sequence = 1 AND kind = 'Baseline' AND from_status IS NULL)
        OR (sequence BETWEEN 2 AND 3 AND kind = 'StatusChanged' AND from_status IS NOT NULL
            AND ((from_status = 'New' AND to_status = 'InReview')
                OR (from_status = 'InReview' AND to_status = 'Closed')))
    )
) STRICT
```

The composite primary key supplies case-local uniqueness and the history lookup order. Add no extra index, cascade, trigger, global event ID or timestamp-based ordering. Database checks constrain individual entries; application validation must still check the whole chain, timestamps and agreement with the current case. See SQLite's [table/constraint semantics](https://www.sqlite.org/lang_createtable.html).

Enable `PRAGMA foreign_keys=ON` on every connection before any transaction, and verify it reads back as `1`. Do not rely on a provider/native default or attempt to enable it inside a transaction. SQLite documents this [per-connection requirement](https://www.sqlite.org/foreignkeys.html#fk_enable). Retain ReadWrite mode without file creation, private cache, disabled pooling, DELETE journaling, FULL synchronization and the finite provider timeout.

Recognize supported formats through both version and exact expected non-internal schema objects/definitions, not the version number alone. Version 1 has only the existing case table; version 2 has exactly both tables, with their constraints. Continue ignoring SQLite-owned internal objects. Normal startup accepts version 2 only, with integrity checking, an empty `foreign_key_check` result and the existing rolled-back write probe. It must not create missing entries or rewrite stored values. These [PRAGMAs](https://www.sqlite.org/pragma.html) provide format metadata and integrity checks; they do not prove an application history chain is valid.

### Ownership, time and validation

Keep history persistence and operation ordering in the existing Api project. Use small immutable entry/result types and a shared chain validator used by history reads and status writes. Domain keeps its lifecycle/restoration rules without HTTP, provider or clock dependencies. No generic event-store or migration framework is introduced.

Register the framework's `TimeProvider.System` in the composition root and inject `TimeProvider` where entries are recorded. Call `GetUtcNow()` only when recording a real entry; tests can supply a small controlled subclass. No new clock package is needed on .NET 10; see the [framework TimeProvider documentation](https://learn.microsoft.com/en-us/dotnet/standard/datetime/timeprovider-overview). Serialize and store UTC timestamps using the exact invariant seven-fractional-digit format from the requirements; parse and validate that format when restoring entries. Never use a SQLite timestamp default with different precision or interpret a baseline timestamp as creation time.

Validate case IDs/statuses and all five entry fields, contiguous sequences starting at 1, one valid `Created`/`Baseline` start, permitted non-no-op transitions and final-status agreement. `Created` starts at `New`; `Baseline` may start at any retained valid state. A baseline never makes earlier transitions known. Equal or decreasing times are valid; sequence is the only history order. Do not use time to enforce transitions or infer missing entries.

### Atomic runtime operations

Retain one synchronous host gate covering each complete operation and one connection per operation. Use parameterized SQL for all values and publish snapshots only after commit. The existing domain matrix remains authoritative.

- **Create:** construct/validate the case, start a non-deferred write transaction, insert the case, record one `Created` entry with sequence 1, verify affected-row counts, then commit. Failure of either write must not leave the other committed.
- **Change status:** start a non-deferred write transaction before reading the case and history. Validate the existing chain before a domain transition or same-status acknowledgement. A missing case remains `404`; damaged history is a storage failure. Catch a rejected transition only around the domain call, preserving the existing `409` mapping. For a real change, update status and insert the next `StatusChanged` entry in that transaction, verify row counts and commit. For a same-status request, commit without rewriting history or asking the clock for a new timestamp.
- **Read history:** use a deferred read transaction for case lookup and history retrieval, with no later write upgrade. Read entries with `WHERE case_id=$id ORDER BY sequence LIMIT 4`; three is the valid maximum and a fourth row is a corruption sentinel. Restore and validate the entire bounded result before returning immutable values. A missing case returns absence; an existing case with missing/invalid history fails. Dispose readers before commit and serialize HTTP outside the gate.

An explicit read transaction supplies one view across the case and history queries; SQLite documents [read-transaction visibility](https://www.sqlite.org/lang_transaction.html). Non-deferred status transactions avoid a read-first write upgrade; the provider's [transaction guidance](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions) describes the distinction. Keep access synchronous and do not hold a C# lock across `await`.

Do not truncate an overlong chain or return a partial successful history. Existing case retrieval/browsing continue restoring their four-field case values; they need not load history on every read. They add no new global consistency claim.

### Explicit command modes

Extend the existing Api executable, using the same `CaseStorage:DatabasePath` configuration and no HTTP hosting in command modes:

| Implemented mode | Input and outcome |
| --- | --- |
| `init-store` | Exclusively reserve a new path; create both tables and version 2 in one transaction. Continue refusing every existing file. |
| `upgrade-store` | Open an existing exact version-1 store while hosting is stopped; validate and atomically upgrade it. Refuse version 2, unknown formats and invalid sources. |
| `inspect-store` | Inspect an existing supported version-1 or version-2 store while hosting is stopped; report only `Case store version: 1.` or `Case store version: 2.` on success. Change no committed application state. |
| Normal hosting | Accept and validate version 2 only. On version 1, fail with a concise diagnostic directing the operator to stop hosting and use the explicit upgrade. |

`inspect-store` resolves an uncertain upgrade result without guessing from a failed command or attempting a second mutation. Use one deferred read transaction to validate exact schema, engine integrity, all case values and, for version 2, foreign keys and every history chain before reporting success. Stream validation one case at a time, retaining at most four entries per case and closing batch readers before further queries on the connection. This offline scan has no promised duration. Open through the normal existing-file connection settings; SQLite may perform its own journal recovery. Inspection is not a promise of zero filesystem writes, but it does not initialize, upgrade, repair or change committed cases/history/version.

Missing, empty, corrupt, unsupported or inaccessible files produce a concise diagnostic and nonzero exit in inspection/upgrade modes. Do not print paths, SQL, records or stack traces. Do not start a listener, auto-create a replacement or delete journals. These command modes are demonstrated in the [current walkthrough](../usage/local-case-workflow.md#inspect-and-upgrade-an-older-store).

### Version-1 upgrade transaction

Require the operator to stop the API and other processes using the file before upgrade or inspection. This is an operating precondition; a SQLite write lock alone does not prove no other host exists. Do not add an automatic startup migration, online upgrade or cross-process orchestration mechanism.

1. Open the existing file without create permission, establish connection settings, and begin one non-deferred write transaction. Check version 1, its exact original schema and engine integrity within this operation; reject any extra non-internal object. Do not call the future version-2 startup validator as a substitute for source validation.
2. Stream and restore every existing case using the current canonical ID, normalized text and exact status rules. Validate all cases before creating the history table. An invalid source is refused rather than normalized or repaired; a valid empty source is allowed. Do not load every case into application memory.
3. Create the history table from the selected definition. In a second bounded pass over cases, insert sequence 1 `Baseline` entries using each retained status and the controlled UTC source. Use keyset batches and close readers before writes on the same connection. Preserve all case columns without updating/reinserting cases. The transaction holds the source stable across both passes.
4. Check that each case has exactly one correct baseline, no orphan exists and the expected target schema is present. Set `user_version=2` in this same transaction, then commit once. Do not set the version outside the transaction or commit per batch. DDL, entries and format metadata must reach a single commit boundary.
5. Report `Case store upgraded.` only after commit. On a known failure, dispose/roll back the transaction and return a safe nonzero result. An interrupted operation or commit error may require normal engine recovery; never infer rollback just from the exit code. Open with `inspect-store` after all processes have stopped to determine the recovered supported state. A partial/invalid format is an error requiring investigation, not an invitation to reset or repeat forcibly.

Retain SQLite rollback journaling and the existing intact-file/process-recovery assumptions. No downgrade is supplied. After commit, the Phase 3 executable refuses version 2; running old code is not a rollback strategy. Do not implement automatic backup/copy/restore or claim machine power/storage-loss recovery. The walkthrough explains the one-way compatibility change before giving upgrade commands.

### Error and retry semantics

Keep the existing generic `500 internal_error` mapping for provider, invalid-row/chain and internal storage failures. HTTP input validation still happens before I/O. No application retry may replay a transaction after an uncertain outcome. After response loss, current state and history remain mutually consistent; a known ID can be read to reconcile the result. Retrying an unacknowledged create can still create a second independent case.

Same-next-status requests serialize into one actual transition and one history entry; a later request for the now-current status adds nothing. No request-arrival ordering, stale-client protection, actor identification or tamper detection is added.

## Alternatives considered

| Option | Judgment for these requirements |
| --- | --- |
| Separate SQLite history table and explicit upgrade | Accepted: preserves the current case model, bounds history reads and makes atomic writes/compatibility inspectable. Adds schema and upgrade ownership. |
| JSON history inside the case row | Avoids a second table but adds whole-document rewrites and moves entry keys/shape checks into application parsing. No requirement justifies that format. |
| Derive earlier transitions from current status | Cannot recover recorded times or prove observed transitions; violates the honest-baseline requirement. |
| Automatically upgrade during normal startup | Hides a one-way format change inside hosting and violates explicit-upgrade requirements. |
| Require users to discard all version-1 cases | Simpler, but fails the requirement to preserve existing cases. |
| Generic migration framework, ORM, event sourcing or server database | Adds new conventions or infrastructure beyond one history table and one supported upgrade path. Defer until concrete needs justify it. |

## Required implementation evidence

All [P4-01–12 criteria](../requirements/phase-4-status-history.md#acceptance-criteria) remain required. In addition to the existing suite, verify:

- Fresh version-2 initialization and exact schema/version refusal; foreign-key enforcement on independently opened connections; no listener in command modes.
- Full HTTP history shape, pre-I/O validation, chain corruption failures and bounded fourth-row detection. Test equal/backward clock readings with a controlled `TimeProvider`.
- Real-engine creation/status failures at both write boundaries, concurrency and lost responses; restart and force-kill recovery of acknowledged paired writes.
- Exact version-1 fixtures containing all statuses, empty stores and invalid rows; successful upgrade preserves every case value, creates only honest baselines, and refuses a second upgrade.
- Controlled upgrade failure after DDL, after some baselines and after setting version but before commit. Reopen independently and verify the entire old logical format/data remains. A local exception test is not evidence of process interruption.
- An upgrade child process stopped at a deterministic pre-commit barrier, then force-killed, followed by inspection/reopen. Also inspect after a completed commit whose success output is suppressed. Expect wholly version 1 or wholly version 2, never a partial baseline set. Keep barriers and output suppression in test fixtures, with no production test command or environment switch.
- Safe diagnostics for unsupported/corrupt/missing inputs and preserved existing files. Test process cleanup and use disposable directories on macOS and Linux; never upgrade user data as validation.

Keep restore, formatting verification, warnings-as-errors, Release build and the full test suite. Update affected tests that hard-code version 1 only when implementing the new format; retain explicit version-1 compatibility/refusal fixtures. Update setup, upgrade/reconciliation examples and implemented-scope evidence after the feature exists.

**Design-time feasibility check:** the proposed DDL was exercised in a disposable database using the current Microsoft.Data.Sqlite dependency and bundled SQLite 3.53.3. A controlled pre-commit exception rolled back table creation, a baseline insert and the version update together; a successful transaction retained original case values and committed baselines for all three statuses. Six malformed/duplicate/orphan entry attempts were rejected by the selected constraints. This scratch check is not a production upgrade implementation, an HTTP test or process-interruption evidence; the implementation checks above remain required.

## Consequences and revisit triggers

The implementation adds a history table, a controlled clock dependency in Api, atomic paired writes, offline validation and one explicit one-way upgrade. The current three-entry lifecycle bound keeps reads small; schema checks do not replace chain validation. Synchronous whole-store upgrade/inspection scans can take time and have no throughput guarantee.

Revisit before adding lifecycle states/reopening, independent writers, production records, retention, compliance audit requirements or shared/cloud hosting. This decision supplies neither identities nor an immutable audit ledger. It adds no production project, package, broker, cloud resource or deployment capability.

## Implementation evidence

[CaseHistory](../../src/EnterpriseCaseIntelligence.Api/Cases/CaseHistory.cs) defines the selected schema, immutable entries, UTC recording and shared bounded chain validation. [SqliteCaseStore](../../src/EnterpriseCaseIntelligence.Api/Cases/SqliteCaseStore.cs) performs atomic paired writes and history reads; [CaseDatabase](../../src/EnterpriseCaseIntelligence.Api/Cases/CaseDatabase.cs) initializes version 2, validates exact formats, upgrades version 1 in bounded batches and inspects both versions.

[Requirements coverage](../requirements/phase-4-status-history.md#current-implementation-coverage) maps every P4 criterion to API, real-engine and process tests. The [test-only UpgradeFixture](../../tests/EnterpriseCaseIntelligence.UpgradeFixture/UpgradeFixtureProgram.cs) uses a controlled clock and named-pipe barrier during an actual upgrade; no barrier/fault switch enters production code. Another fixture mode discards completion after commit. [Upgrade tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseStoreUpgradeTests.cs) also hold a real reader lock to fail commit after the version write, then verify whole-store rollback. These checks do not establish hardware/power-loss recovery or every possible native failure timing.
