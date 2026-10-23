# 0006: Bind creation keys in the local case store

## Status

**Accepted — implemented for Phase 5.** Requirements were reviewed in PR #18 and this storage design in PR #19. At Phase 5, the executable initialized/hosted version 3 and explicitly upgraded versions 1 and 2. See [implementation coverage](../requirements/phase-5-retry-safe-creation.md#current-implementation-coverage).

[ADR 0007](0007-adopt-ef-core-code-first.md) now implements EF Core Code First and format 4 with validated adoption of versions 1–3. This record preserves the historical format and direct-access decision; use the current walkthrough for executable commands.

This is a focused amendment to [ADR 0005](0005-add-status-history-with-an-explicit-store-upgrade.md): add durable creation-key bindings, original-result replay and explicit upgrades from versions 1 and 2 to version 3. Keep the SQLite choice and operating/recovery limits from [ADR 0004](0004-use-sqlite-for-the-durable-local-workflow.md).

## Context

A create can commit before the client receives its generated case ID. Phase 5 requires matching retries to return that creation result without another case/history entry. Requests without a key must remain independent creates.

The existing model makes IDs and normalized case text immutable; only status changes. Thus the durable case already retains the original ID/title/description, and a valid `Created` history start establishes its creation provenance. This permits a small binding table instead of copied request bodies, response JSON, payload hashes or a generic idempotency service.

## Decision

### Schema and original-result representation

Keep the exact current definitions of `cases` and `case_status_history`. Add this table, using the same definition for fresh initialization and upgrades, and select application-owned `PRAGMA user_version=3`:

```sql
CREATE TABLE case_creation_keys (
    request_key TEXT NOT NULL PRIMARY KEY,
    case_id TEXT NOT NULL UNIQUE,
    FOREIGN KEY (case_id) REFERENCES cases(id),
    CHECK (
        length(request_key) = 36
        AND instr(request_key, char(0)) = 0
        AND substr(request_key, 9, 1) = '-'
        AND substr(request_key, 14, 1) = '-'
        AND substr(request_key, 19, 1) = '-'
        AND substr(request_key, 24, 1) = '-'
        AND length(replace(request_key, '-', '')) = 32
        AND replace(request_key, '-', '') NOT GLOB '*[^0-9a-f]*'
        AND request_key <> '00000000-0000-0000-0000-000000000000'
    )
) STRICT
```

Store the unquoted canonical lowercase UUID. The primary key permits one binding per key; `UNIQUE(case_id)` permits at most one key per case, while unkeyed cases have no row. The foreign key supplies target existence with the existing per-connection enforcement. Add no cascade, explicit secondary index, trigger, timestamp, expiration or pending/result-status field. SQLite documents [constraints](https://www.sqlite.org/lang_createtable.html), [STRICT tables](https://www.sqlite.org/stricttables.html) and [GLOB expressions](https://www.sqlite.org/lang_expr.html#the_like_glob_regexp_match_and_extract_operators). An explicit [NUL search](https://www.sqlite.org/lang_corefunc.html#instr) excludes hidden suffixes without depending on database text encoding; application UUID validation is still required.

The durable original result is the bound case's immutable ID/title/description projected with status `New`. Reconstruct the same relative `Location: /cases/{id}` and existing four-field JSON/media type; do not serialize the case's current status as the replay result. There is no stored transport envelope or replay metadata. This depends on immutable text and the existing creation representation: revisit before adding edits, deletion or response-shape changes.

### Parsing, equality and outcomes

Keep this extension inside the existing create endpoint and concrete store. Do not introduce middleware that caches arbitrary HTTP responses, a new production project, provider abstraction or package. Domain keeps its current model and references no header or storage concepts.

After the existing media-type/JSON checks, validate normalized text through the existing domain rules and parse the optional header before opening storage. Require one field value; remove only HTTP outer space/tab whitespace, then require exactly 38 characters: double quote, 36-character non-empty `D` UUID, double quote. Use exact UUID parsing without trimming the quoted contents. Normalize accepted uppercase hex to lowercase. This rejects lists, parameters, escapes, duplicates, unquoted and empty values without a general structured-fields parser. A missing header stays keyless. Preserve the reviewed error precedence and `errors.idempotencyKey` without echoing values.

For a selected binding, validate canonical stored key/case IDs, restore the complete case and validate its bounded history chain in the same transaction. Require its first entry to be `Created`, never `Baseline`. Use the restored case's normalized title/description as the original request and compare the newly normalized values with `.NET` ordinal equality. Do not use SQL text length, collation, case conversion or a payload checksum to decide request equality; embedded NULs and Unicode must retain the existing string semantics.

Return an explicit store outcome for a valid payload mismatch, mapped to `422 idempotency_key_reused` with title `Unprocessable Content`. Map success to the ordinary `201`/Location/creation body. No special success outcome needs to expose whether it was replayed. Do not translate an arbitrary constraint violation or broad provider exception into a payload mismatch; those remain generic `500 internal_error`.

### One transaction for ownership and creation

Preserve the existing host gate, synchronous parameterized access and one connection per operation. After input validation, begin a non-deferred write transaction before testing key ownership. The provider's [transaction guidance](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions) explains why a read-first deferred transaction should not be upgraded later for this write path.

1. **No key:** retain the existing case + `Created` entry writes and commit; do not add a binding or deduplicate by text.
2. **Unbound key:** insert the new case, insert its `Created` entry and insert the binding in that transaction. Verify each inserted-row count is exactly one. Commit once, then publish the new creation snapshot.
3. **Bound key, valid target/provenance and equal fields:** commit without writes and return a new immutable creation snapshot with `New`. Do not call the clock, change the actual status or touch history/binding values.
4. **Bound key, valid target/provenance and different fields:** return the explicit mismatch outcome without writes. No error response, reservation or replacement binding is committed.
5. **Invalid binding/target/history or provider failure:** fail safely, dispose/roll back and retain the generic storage error. Never fall through to new creation when a selected binding fails validation.

Matching concurrent requests serialize into one creation and one replay. Differing requests sharing a key permit one definition to bind it and the other to get `422`; request arrival order is not promised. Do not use `INSERT OR REPLACE`, upsert/rebind, a persisted pending reservation or a second commit. A failed/lost response does not prove rollback; the client's same key/request reconciles the retained result. Keyless retries retain their old duplicate risk.

### Bounded lookup and corruption boundary

Look up a canonical key using parameterized equality with BINARY comparison. Materialize at most two binding rows (one expected plus a duplicate sentinel), one case and at most four history rows using the existing chain reader. Dispose readers before further commands/commit, and serialize HTTP after leaving the gate. A second selected binding, invalid stored UUID, absent target, invalid case, broken history or baseline provenance is a storage failure before either replay or mismatch is returned.

Request-time validation covers the selected binding/target, not every key in the database. Offline inspection scans all bindings. A row whose identity has been externally changed may no longer match a lookup; a deleted binding is indistinguishable from a never-used key. Schema constraints and startup integrity checks reject malformed stored keys in supported files, but this design does not detect arbitrary consistent file edits or supply tamper evidence. The retry guarantee assumes intact application-owned retained state. This clarifies the bounds of P5-11 rather than promising a full store scan on each request.

Do not expire or evict bindings. Clock changes and host replacement never release a key. Other case operations retain Phase 4 behavior and do not rewrite bindings or add keys to public case/history representations. There is no cleanup route or TTL configuration. Local disk capacity and the absence of identity/authorization remain existing limitations.

### Exact formats and command compatibility

Keep existing-file ReadWrite access, private cache, disabled pooling, five-second provider timeout, verified foreign keys, DELETE journaling and FULL synchronization. After implementation, recognize formats by exact version and non-internal schema definitions; reject extra/missing/modified objects. Do not trust `user_version` alone or use `CREATE TABLE IF NOT EXISTS` to bypass a mismatch.

| Mode | Behavior |
| --- | --- |
| `init-store` | Reserve a new path exclusively; create all three tables and version 3 atomically. Refuse every existing file. |
| Normal hosting | Accept only exact version 3, with existing integrity/FK checks and rolled-back write probe; refuse versions 1/2 with safe explicit-upgrade guidance. |
| `upgrade-store` | Accept an exact valid version-1 or version-2 source while all hosting is stopped; upgrade directly to version 3 in one transaction. Refuse version 3 and unsupported inputs without replacement. |
| `inspect-store` | Validate versions 1, 2 or 3 in one deferred read view; report only the existing fixed version message for the validated format. No committed application-state change. |

Inspection must validate all cases, history where present and every binding in version 3. Reuse batches of at most 100 cases and four entries per history, then scan bindings in canonical-key batches of at most 100, closing batch readers before target queries. Require valid `Created` provenance for every bound target. No binding is required for a keyless case. Whole-store inspection is an offline scan without a latency guarantee; SQLite may perform normal journal recovery.

### Direct upgrades from both older versions

Require every process using the store to stop before upgrade/inspection. A SQLite write reservation does not prove no host exists; retain that operator precondition and add no cross-process coordinator.

Within one non-deferred write transaction:

1. Validate the source version, exact source schema, engine integrity, foreign keys and every case. For version 2, also validate every existing history chain before DDL. Missing/corrupt/incompatible sources are refused without initialization, repair or replacement.
2. **Version 1 only:** create the unchanged history table and add one honest `Baseline` per case using the existing bounded, controlled-clock procedure. Earlier transitions remain unknown. **Version 2:** leave all history rows and their recorded times untouched; add no baselines and do not read the clock for migration events.
3. Create the empty `case_creation_keys` table. All older cases remain unbound, including version-2 cases whose history starts with `Created`. Never infer or retrofit a key from an ID, payload or history.
4. Set `user_version=3` in that transaction, validate the exact target format/data and empty binding set, then commit once. Preserve original case values and every version-2 history value. Do not commit an intermediate version-2 store when upgrading version 1, commit per batch or set metadata outside the transaction.
5. Report `Case store upgraded.` only after commit. A known failure rolls back the whole change. After interruption, commit error or lost completion output, stop processes and inspect first: a valid source version may be retried deliberately; valid version 3 must not be upgraded again; an invalid result requires investigation without deleting journals or resetting storage.

Retain the one-way compatibility rule: old executables refuse version 3, and no downgrade or automatic backup/copy/restore is added. Upgrade/inspection introduce no listener, cloud resource or SQLite CLI prerequisite. Diagnostics must not disclose keys, IDs, text, SQL, paths or stack traces. These version-3 commands are available in the [current walkthrough](../usage/local-case-workflow.md).

## Alternatives considered

| Option | Judgment for this slice |
| --- | --- |
| Two-column binding + immutable case + `Created` provenance | Selected: stores only ownership, reuses durable request values and supports original `New` replay. Depends on immutable text and unchanged creation representation. |
| Copy request fields and a response snapshot into the binding | Retains separate originals if cases later become editable, but duplicates current immutable data and adds cross-copy validation without a reviewed editing requirement. Revisit with that requirement. |
| Payload hash as the only equality evidence | Adds canonical encoding/hash decisions and collision handling where exact normalized strings are already available. No hash is selected. |
| In-memory dictionary or generic HTTP response cache | Cannot supply durable atomic ownership with the case/history commit or restart recovery. |
| Unique case text | Breaks independent keyless/different-key creation and mistakes similar content for the same intent. |
| External key store, broker, distributed lock or independently committed reservation | Adds another consistency/recovery boundary without a reviewed multi-process need; cannot replace this single local commit. |
| Automatic upgrade at startup | Violates explicit format-change requirements and obscures the one-way compatibility change. |

## Required implementation evidence

The implementation evidence covers every [P5-01–12 criterion](../requirements/phase-5-retry-safe-creation.md#acceptance-criteria), including:

- Strict header/body/media validation before I/O, including known keys while storage is unavailable. Corrected invalid requests must be able to use an unbound key.
- Exact ordinal normalized comparisons for quotes, NULs, supplementary Unicode and distinct Unicode sequences; equivalent JSON formatting and surrounding trimmed text must replay correctly.
- Original `201`/Location/four-field replay at all later lifecycle states, unchanged current case/history and no replay clock read. Keyless and different-key duplicates remain independent.
- Deterministically coordinated same/different request races, store isolation and unique binding/target enforcement. Constraint errors must not become false `422` outcomes.
- Actual SQLite failures at each case/history/binding write boundary, including unexpected inserted-row counts, followed by independent reopen and a same-key retry. No half-committed binding or orphaned create.
- Suppress a successful keyed-create response, restart and retry without relying on the client knowing its original ID. Separately force-kill the API after acknowledged keyed writes, then retry against the retained store. Equal/backward/advanced clock controls must not expire bindings.
- Selected-row corruption, missing targets, broken provenance and malformed histories return safe `500`, even for a mismatching request. Inspection must also reject invalid unrelated bindings. Do not claim lookup detects arbitrary deletion/rekeying that removes its match.
- Frozen version-1 and version-2 fixtures, including empty/all-status/multi-batch cases and supported UTF-8/UTF-16 encodings; byte-for-byte equality of individual version-2 history field values after upgrade, without requiring identical whole-file bytes. All old cases start without keys, and new keyed/keyless creation works afterward.
- Upgrade failure after DDL/baselines and after the version write but before commit for both source formats; process interruption during a controlled upgrade and lost completion after commit. Inspection must find a wholly supported source or version 3, without an intermediate version-2 commit for a version-1 upgrade. Keep barriers/fault controls in test fixtures, never production flags.

Preserve restore, format verification, zero-warning Release build and the full existing domain/API/storage/process suite locally and in Linux CI. Update the walkthrough with quoted-key examples, original-result versus current-state behavior, explicit direct upgrades and inspection recovery after implementation. The README and walkthrough describe the implemented Phase 5 scope.

**Design-time feasibility check:** a disposable probe using the existing provider and bundled SQLite 3.53.3 exercised this exact DDL. Version-1 and version-2 inputs in both UTF-8 and UTF-16 rolled back to their source format after a controlled failure following the version write, then committed directly to version 3. Original case values and all fields of multi-entry version-2 histories were retained; version-1 cases received only baselines. Three-way creation/history/binding rollback and original-`New` projection after a later status change passed. Fourteen invalid/duplicate/orphan binding attempts were rejected, including a hidden NUL suffix. The then-current Phase 4 executable refused version 3 as expected. That probe checked SQL feasibility. The current implementation coverage linked above supplies HTTP, command, race and recovery evidence.

## Consequences and revisit triggers

This decision adds one small table, a bounded creation lookup/outcome and one newest-format upgrade path within Api. It adds no production project, package, ORM, cache service or deployment capability. One binding per keyed case consumes durable disk without expiry or quotas. Inspection/upgrade remain synchronous offline scans.

Revisit before allowing text edits/deletion, changing the creation response, expiring/releasing keys, introducing user/tenant namespaces, sharing stores between processes or using external side effects. The design is a local retry contract, not exactly-once distributed delivery, tamper evidence, authorization, power-loss recovery or production readiness.
