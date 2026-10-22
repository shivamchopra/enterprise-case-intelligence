# Adopt EF Core Code First

**Status: Implemented.** The owner selected EF Core Code First in PR #21. [ADR 0007](../adr/0007-adopt-ef-core-code-first.md) documents the conversion and format 4, including validated adoption of retained versions 1–3. The current runtime uses EF models, LINQ/tracked writes and scaffolded migrations.

## Outcome

Make the C# EF Core model and its migration history the source of truth for application tables and schema changes. Use EF APIs for ordinary queries and writes. Retain the current SQLite provider scope, synthetic workflow and application boundaries while preserving cases, histories and creation-key bindings already stored by previous versions.

This task was prioritized ahead of another feature slice. Follow [AGENTS.md](../../AGENTS.md) and [contribution guidance](../../CONTRIBUTING.md). The owner's EF-first direction replaces direct SQL as the default for new persistence code; historical SQL is evidence of the shipped format, not a pattern to repeat.

## Implementation work

1. Document the EF compatibility amendment to ADRs 0004–0006. Select compatible stable EF Core packages/tooling for the .NET 10 baseline and define the context lifetime, transaction behavior, model mappings, migration ownership and explicit command execution. Keep Domain free of EF dependencies.
2. Map cases, status-history entries and creation-key bindings through C# entities and Fluent API. Preserve canonical UUID text, status names, exact normalized text semantics, UTC history values, foreign keys, uniqueness and required checks. Evaluate STRICT tables and other provider-specific features explicitly; retain supported guarantees or document/test an intentional compatibility amendment.
3. Replace ordinary hand-written SELECT/INSERT/UPDATE paths with EF LINQ, projections and tracked writes. Keep bounded keyset pages/history/key lookups and exact ordinal request comparisons, including embedded NULs and distinct Unicode sequences. Avoid loading whole tables or sharing a context across concurrent operations.
4. Scaffold and check in EF migrations and the model snapshot. Use generated migration operations for model changes; explain unavoidable provider-specific SQL rather than duplicating the model in separately maintained schema scripts. Route initialization and upgrades through the explicit offline workflow. Do not use `EnsureCreated` as a parallel schema lifecycle, migrate automatically during normal startup or reset existing files.
5. Design and test safe adoption of retained version-1, version-2 and version-3 stores. Decide the next application format/version, baseline strategy, treatment of EF migration-history/lock tables and exact schema inspection before applying changes. Do not simply run an initial create migration against populated tables or mark a migration applied without validating the actual source. Preserve version-1 honest baselines, every version-2/3 history field and all version-3 key ownership/original-result behavior. Old unbound cases must remain unbound.
6. Preserve transaction and failure behavior across the conversion. Creation ownership, case and history still share one commit; matching retries return original `New`, changed normalized fields return `422`, and corruption/provider failures remain safe `500`. No-op/replay does not read the event clock. Keep inspection and failure/interruption recovery explicit, with bounded waits and no silent retries of an uncertain transaction.
7. Adapt the real SQLite integration/process tests, retain frozen legacy-format fixtures and extend compatibility coverage. Update the README, walkthrough, architecture, amended ADRs and implemented-scope evidence when EF adoption actually ships.

## Acceptance criteria

| ID | Required evidence |
| --- | --- |
| EF-01 | C# entities, context and Fluent API own the application model; reproducible EF migrations and snapshot are committed. Initialization/schema changes use migrations. No parallel hand-maintained DDL replaces model-supported operations. |
| EF-02 | Ordinary queries/writes use EF where supported. Lists/history/key replay retain bounds, canonical-ID ordering and ordinal payload semantics. Domain stays independent; contexts are not shared concurrently. |
| EF-03 | Fresh initialization and retained versions 1/2/3 are covered by disposable real-provider tests. Existing cases, exact history values and retained bindings survive adoption; repeated/invalid upgrades fail safely without replacement. |
| EF-04 | Existing HTTP/domain behavior, no-overwrite initialization, explicit offline upgrade, startup refusal, safe diagnostics and atomic case/history/key commits are preserved. Failure, lost-response, concurrency and process-interruption tests verify recovery with EF migration metadata accounted for. |
| EF-05 | Each remaining hand-written SQL use has a concrete EF/provider limitation or historical/test-fixture reason and relevant validation. EF-generated SQL itself is expected and needs no exception. |
| EF-06 | Restore, format verification, zero-warning Release build, all existing 391 tests (adapted where necessary) and new EF compatibility tests pass locally and in Linux CI. Documentation accurately distinguishes the EF implementation from earlier direct-SQL formats. |

## Current implementation coverage

| Criteria | Evidence |
| --- | --- |
| EF-01 | [Context/model](../../src/EnterpriseCaseIntelligence.Api/Cases/CaseDbContext.cs), scaffolded [migration and snapshot](../../src/EnterpriseCaseIntelligence.Api/Cases/Migrations), pinned local EF tooling and CI model-change verification. |
| EF-02 | [Concrete store](../../src/EnterpriseCaseIntelligence.Api/Cases/SqliteCaseStore.cs), EF history/key queries and existing list/lifecycle/retry tests preserve bounded reads, restoration and ordinal comparisons. Context lifetime follows each operation. |
| EF-03 | [EF adoption tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseEfAdoptionTests.cs) cover frozen version 3 with retained keys, UTF-8/UTF-16 and multiple batches. Existing upgrade suites cover frozen versions 1/2, exact histories and honest baselines. |
| EF-04 | All existing write-boundary, concurrency, unknown-ID response-loss, process-recovery and controlled upgrade interruption tests run against EF. Adoption tests fail commit after metadata writes for each old source and verify no metadata/lock artifact remains; lost-completion tests cover all sources. |
| EF-05 | ADR 0007 lists each remaining provider/compatibility/test SQL exception, including actual SQLite affected-row verification after a single-entity EF save and the small STRICT generator extension. |
| EF-06 | All 415 tests (42 domain and 373 API/storage/process) include the previous 391 cases with current-format expectations and new EF metadata/compatibility checks. The required gates now include pinned tool restore and model/snapshot agreement. The README, walkthrough and architecture describe format 4. |

EF metadata is validated as part of format 4. Explicit caller-owned offline transactions keep metadata/application changes atomic, with no persistent migration lock or automatic uncertain-transaction retry. Exact frozen application definitions are accepted for validated legacy adoption; only missing tables are generated. Provider diagnostics and history-repository-generated SQL remain narrowly documented exceptions rather than a parallel schema implementation.

## References

- [Microsoft: EF Core migrations](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/) — model changes generate migration source and EF records applied migrations.
- [Microsoft: SQLite provider limitations](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations) — provider rebuild limitations and migration-lock recovery considerations.
- [Current architecture](../architecture/overview.md), [ADR 0004](../adr/0004-use-sqlite-for-the-durable-local-workflow.md), [ADR 0005](../adr/0005-add-status-history-with-an-explicit-store-upgrade.md) and [ADR 0006](../adr/0006-bind-creation-keys-in-the-local-case-store.md) — compatibility contracts the conversion must address.
