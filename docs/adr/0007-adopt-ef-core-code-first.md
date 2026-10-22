# 0007: Adopt EF Core Code First

## Status

**Accepted — implemented.** The owner selected EF Core Code First in PR #21. This implements the [adoption task](../tasks/ef-core-code-first-adoption.md) for the existing Phase 5 workflow and introduces application store format 4.

Amends the data-access/schema-lifecycle parts of ADRs [0004](0004-use-sqlite-for-the-durable-local-workflow.md), [0005](0005-add-status-history-with-an-explicit-store-upgrade.md) and [0006](0006-bind-creation-keys-in-the-local-case-store.md). SQLite, the HTTP/domain contract, single-process operating scope and retained-file recovery limits remain in effect. The older records preserve the exact versions 1–3 that shipped.

## Decision

### Model and ordinary data access

Use stable EF Core SQLite/Design 10.0.13 with the existing .NET 10 baseline. The checked-in local `dotnet-ef` manifest pins matching tooling. Api owns C# persistence entities, `CaseDbContext`, Fluent API configuration, scaffolded migration source and its model snapshot. Domain keeps its own validated case model and no EF/provider dependency.

Map IDs/keys/statuses/timestamps as strings and history sequence as INTEGER. Restore validated domain values after materialization; do not let EF conversions accept or repair malformed stored values. Preserve embedded NUL/Unicode text, ordinal payload comparison and canonical ID order. LINQ applies BINARY collation, filtering and `Take` before materialization; lists read at most limit+1 rows, history four, selected bindings two, and offline scans batches of 100.

Use one context per operation, attached to its owned connection and, where needed, the existing explicit SQLite transaction. Never share a context concurrently. Retain the host gate, five-second command timeout, private cache, disabled pooling, foreign keys, DELETE journaling and FULL synchronization. Tracked writes and `SaveChanges` replace hand-written CRUD statements. Creation still saves case, Created entry and optional binding under one commit; status/history remain paired. Replay/no-op reads no event clock and mutates nothing.

### Model-generated schema and STRICT tables

The initial migration and snapshot were scaffolded from the C# model. Fluent API owns column mappings, keys, uniqueness, relationships and check constraints. The EF SQLite provider does not expose a STRICT-table switch in this version; a small `SqliteMigrationsSqlGenerator` extension appends `STRICT` to generated application-table definitions. It leaves EF metadata tables to the provider. Columns, constraints and relationships still come from generated migration operations, without a parallel hand-written new-store schema.

Fresh `init-store` reserves a new path exclusively, starts one non-deferred transaction, attaches a migration context and invokes EF migrations. Application tables, `__EFMigrationsHistory`, the initial migration row and `user_version=4` commit together after validation. Existing files are refused, including empty ones. Normal hosting accepts only validated format 4 and never calls `Migrate` or `EnsureCreated`.

### Validated adoption of retained versions 1–3

`upgrade-store` requires every store user stopped. Within one non-deferred transaction:

1. Validate the exact frozen source version/schema, engine integrity, foreign keys and every case/history/binding present. Existing EF metadata or extra schema in a legacy source is refused rather than trusted as a partial baseline.
2. Retain existing application tables and all their values. Create only missing history/key tables using the scaffolded initial migration's `CreateTable` operations and EF's SQL generator. Do not run a full initial create migration against existing tables.
3. Version 1 receives honest Baseline entries through EF writes, with controlled recording time. Versions 2/3 keep every history field and read no migration-event clock. Version-3 key ownership remains unchanged; older unbound cases remain unbound.
4. Validate the complete modeled data, then use EF's history repository to create metadata and record the validated initial migration baseline. Set format 4, validate its exact schema/metadata and commit once. No application table is copied, dropped or rewritten during adoption.

Format 4 accepts each application table's exact scaffolded STRICT definition or exact frozen legacy definition. This deliberately permits the combinations emitted by direct adoption from versions 1/2/3. Each accepted definition preserves required constraints and column semantics; arbitrary structural equivalence or extra objects are not accepted. EF metadata must have the exact provider-generated table definition, one expected migration ID and a canonical stable EF product version within the initial-to-running supported 10.0 patch range. Model/snapshot drift is rejected. Future migrations require an explicit compatibility/version amendment; the current adoption runner accepts only the initial create-table operations.

A successful command prints `Case store upgraded.`; inspection reports `Case store version: 4.`. Repeating upgrade refuses format 4. The change is one-way; old executables refuse it. No downgrade, automatic backup, reset or repair is supplied.

### Transaction ownership and migration locks

The application owns the offline write transaction, rather than letting EF independently acquire persistent migration locks or commit metadata outside it. Only the initialization context suppresses EF's specific user-transaction migration warning. EF is not configured to retry uncertain transactions. Legacy adoption executes only transaction-safe generated operations and refuses transaction-suppressed operations.

This uses the existing operator requirement to stop all store users, plus SQLite's finite write reservation. It does not establish support for live migration or multiple hosts. These commands create no `__EFMigrationsLock` table. An unexpected lock table is an unsupported extra object and is refused without automatic deletion. Microsoft's [transaction warning](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-9.0/breaking-changes#exception-is-thrown-when-applying-migrations-in-an-explicit-transaction) and [SQLite migration limitations](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations) explain why transaction ownership and abandoned locks need explicit treatment.

After failure, interruption or lost completion output, stop store users and inspect first. A valid version 1/2/3 is an uncommitted source; valid 4 is committed and must not be upgraded again. Unsupported/invalid results require investigation. Do not delete journals or metadata to force a retry. SQLite may perform normal journal recovery; no machine/power/storage-loss guarantee is added.

### Remaining SQL exceptions

| Location | Concrete reason and validation |
| --- | --- |
| `CaseDbContext` check expressions | SQLite check expressions are configured through Fluent API and emitted by the migration generator. Real constraint tests exercise them. |
| STRICT generator suffix | Provider extension for the missing STRICT model option; initialization tests verify all three application tables and reject BLOB data in a TEXT column. |
| `SaveSingleChange`: `SELECT changes()` | EF's entity count does not detect a trigger-ignored insert when no generated value is returned. Immediately verify one actual outer-statement change after each single-entity save. This is a provider diagnostic, not hand-written CRUD; ABORT/IGNORE tests on all three writes prove rollback. [SQLite documents the affected-row semantics](https://www.sqlite.org/c3ref/changes.html). |
| `CaseDatabase` catalog/PRAGMA access | EF has no equivalent model API for the exact SQLite catalog, integrity/FK checks, journaling/synchronization or application `user_version`. No user values are interpolated into these diagnostic statements. |
| `CaseMigrations` execution | Executes EF-generated migration/history-repository SQL for a validated legacy baseline. No separately maintained application DDL or data-copy script is executed. |
| `LegacyCaseSchema` and frozen/fault fixtures | Frozen DDL is validation evidence for shipped formats; test SQL creates old files, corruption, locks and triggers independently of today's initializer. It is not the new-store schema source. |

## Verification and consequences

[EF adoption tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseEfAdoptionTests.cs) verify fresh migration metadata, snapshot agreement, STRICT enforcement, retained version-3 cases/histories/keys in UTF-8/UTF-16 with empty/all-status/205-case inputs, invalid metadata, safe CLI refusal and commit-lock rollback for all three source versions. Existing version-1/version-2 fixtures cover baselines, exact history values and multi-batch adoption. Process tests kill an upgrade during partial writes and reconcile lost completion from all supported source versions; no production fault/barrier flags are added.

The complete previous 391-test contract remains exercised with current-format assertions updated. CI also restores the pinned tool and runs `migrations has-pending-model-changes`. Readiness still means host liveness only. Authentication, server storage, cloud deployment, multi-process hosting and distributed exactly-once semantics remain future work.

EF owns the application model and schema evolution; a small explicit adoption/inspection boundary remains necessary for already-shipped files. Future schema changes must be scaffolded from the model and tested against retained format-4 variants. Revisit this decision before edits/deletion, provider changes, live migration, shared stores or transaction-suppressed/rebuild operations.
