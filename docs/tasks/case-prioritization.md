# Deliver case prioritization after contract review

**Status: requirements and ADR proposed — implementation not started.** The [Phase 9 proposal](../requirements/phase-9-case-prioritization.md) defines Low/Normal/High choices, Closed behavior, filtering and separate priority resources. [ADR 0016](../adr/0016-expose-case-priority-without-changing-creation-replay.md) preserves the current four-field case/replay contract. Current runtime/schema/fixtures remain unchanged at format 4.

## Why the next change needs a lifecycle amendment

The current [migration adapter](../../src/EnterpriseCaseIntelligence.Api/Cases/CaseMigrations.cs) uses `assembly.Migrations.Single()`, derives expected schema from the initial migration and accepts exactly one metadata row. [The database lifecycle](../../src/EnterpriseCaseIntelligence.Api/Cases/CaseDatabase.cs) recognizes formats 1–4, upgrades only 1/2/3 and validates format 4 against its current model plus frozen legacy shapes. These are intentional current limits, not an implementation of general future migrations.

A new migration cannot simply be appended while leaving those assumptions intact. Priority also becomes required retained state even though the original HTTP representations stay unchanged. Restore, deep inspection, list/filter materialization, status changes and creation-key validation must detect invalid priority rather than silently defaulting during ordinary operations.

## Ordered implementation work

1. **Review the public behavior.** Agree the proposed values/default, Closed no-op/change distinction, separate resources, unchanged creation input/replay, selected-case UI and filter-only queue behavior. This PR delivers a reviewable contract; it does not implement the feature or claim hosting acceptance.
2. **Capture source evidence and prove the physical model.** Freeze disposable format-4 fixtures from the current released initializer and from supported legacy adoption paths before changing the model. Preserve all existing format-1/2/3 fixtures and historical migrations. Record only synthetic contents. Define candidate priority state in C#/Fluent API and scaffold the migration/snapshot with the pinned tool. Inspect generated operations and test actual SQLite transactional/STRICT/FK behavior; select a case column or justify a required related record from that evidence. Do not maintain both models or a parallel SQL upgrade path.
3. **Extend the explicit lifecycle.** Replace the single-initial-migration assumptions with an explicitly supported migration chain and bounded metadata validation. Distinguish every supported old schema/metadata baseline from the target model, including fresh/adopted format 4; do not use the latest snapshot to validate an older source accidentally. Initialize only new paths; upgrade supported retained sources atomically to the selected format 5, assigning Normal without changing old values/history/key ownership. Startup never upgrades. Verify target invariants and metadata before commit; refuse unknown/unsupported or transaction-suppressed paths until a safe design is established.
4. **Implement the business operations.** Add Domain priority rules, appropriate per-operation contexts and bounded EF queries/tracked writes. Atomic creation includes required priority state; priority updates preserve status/history/key values and acknowledge only after commit. Preserve exact original creation replay after reprioritization and Closed. Add real-provider failure/no-op/race, restart/isolation and validation-before-I/O coverage.
5. **Connect the existing workspace.** Add the priority filter and independent selected-case read/edit/retry states. Keep Reader/Caseworker/CSRF boundaries, current generation checks, no polling and no per-listed-case reads. Preserve uncertain creation context; a priority write with an uncertain response requires reading current state before an explicit further action. Verify concurrent Close, denial/expiry/sign-out, delayed responses and published keyboard/mobile journeys.
6. **Complete acceptance before claiming delivery.** Map CP-01–12 to passing tests, update the proposed ADR with the physical/model/lifecycle decision and evidence, and update README/architecture/runbooks to format 5 only when it exists. Keep hosted, restore/backup, SLA and audit claims separate. Run all repository gates, including real provider and retained creation-key/process recovery tests.

After an actual model change, the implementation uses the existing contribution workflow, for example:

```sh
dotnet tool restore
dotnet ef migrations add AddCasePriority --project src/EnterpriseCaseIntelligence.Api --output-dir Cases/Migrations
```

The [EF migration workflow](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/managing#add-a-migration) scaffolds from an actual model change. These commands are future implementation instructions. No migration was scaffolded for this proposal. Inspect generated operations/snapshot; never edit a historical migration to make an old file appear current. Ordinary data work uses bounded EF LINQ and tracked `SaveChanges`; provider-generated migration/history SQL follows the existing documented boundary. Any remaining hand-written SQL needs a demonstrated EF/provider limitation and supporting real-provider test under [AGENTS.md](../../AGENTS.md).

## Failure and compatibility checklist for the implementation

- Fresh initialization, both accepted format-4 layout families and supported metadata patch versions, plus direct supported format-1/2/3 adoption; preserve UTF-8/UTF-16 text and datasets spanning multiple batches.
- Invalid source schema/metadata/priority, malformed history/bindings, extra objects, missing required state and unsupported future versions: refuse without destructive normalization or reset.
- Failure/interruption during generated schema work, default assignment and metadata/format changes: source remains a valid recoverable source or target is fully committed; no partially accepted target. Suppress completion after a real commit and reconcile by the offline inspector.
- Preserve existing IDs, normalized immutable text, current status, every history value/time/sequence, creation-key namespace/target and old unbound records. Default priority assignment is not historical priority evidence and must not manufacture a status entry.
- Abort/ignored writes and commit failures across all creation stages; same-key replay after priority/status changes; deterministic priority-write versus Close ordering and no-op behavior.
- No automatic startup migration, shared `DbContext`, overlapping writers, missing-file replacement, downgrade promise, scale-out, new server provider, cloud mutation or cost authorization follows from this work.

Current [known-ID lookup](open-case-by-id.md) and [workload overview](case-workload-overview.md) remain implemented independently of this proposal. Their existing tests are baseline regression evidence, not priority acceptance tests.

Proposal verification: all 733 existing tests passed (42 Domain, 643 non-browser API/storage/process and 48 published browser cases), with restore/tool restore, format verification, zero-warning Release builds, offline probe-envelope check and unchanged EF snapshot agreement. All 181 checked local documentation links resolved. These are baseline regression/documentation checks; CP-01–12 remain planned implementation evidence.
