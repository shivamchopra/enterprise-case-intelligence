# Deliver case prioritization

**Status: backend implemented; workspace priority controls remain pending.** The [Phase 9 contract](../requirements/phase-9-case-prioritization.md) defines Low/Normal/High choices, Closed behavior, filtering and separate priority resources. [ADR 0016](../adr/0016-expose-case-priority-without-changing-creation-replay.md) records the physical-model proof and format-5 compatibility amendment. Existing four-field case and original creation-replay representations remain unchanged.

## Delivered backend

- Domain priority rules, Normal creation default, validated Closed same-value no-op versus differing-value conflict; status progression remains independent.
- Authorized `GET /cases/{id}/priority`, CSRF-protected Caseworker `PUT`, exact two-field representations and strict input/query/media/error behavior. Optional exact `priority` list filter combines with title/status/boundary before ordering/Take. Summary remains the existing status aggregate.
- `StoredPriority` and Fluent API primary/foreign key, restrictive deletion, value check and STRICT table, scaffolded `AddCasePriority` migration/designer/snapshot. A column/check candidate was scaffolded and removed after its actual SQLite rebuild emitted transaction-suppressed foreign-key commands. The selected migration is a single transactional CreateTable and preserves existing table catalogs.
- Explicit two-migration chain with bounded metadata and per-migration EF patch checks, historical initial target-model schema validation, format-5 no-overwrite initialization and direct offline 1/2/3/4 → 5 upgrades. Validate sources before mutation; create missing legacy tables/baselines only when required; bounded tracked Normal backfill; validate target before committing metadata/marker together. No startup migration, reset or automatic retry.
- Atomic case/priority/history/key creation, tracked priority changes and commit-before-success. Restore/selected reads/list lookahead/status changes/replay/deep inspection reject encountered missing/invalid priority. Left joins preserve missing-dependent detection. No ordinary operation supplies a repair default. Priority is current state without actor/time/history evidence.

## Independent source and provider evidence

Before changing the model, disposable released format-4 files were created from main `97c67ec` through the released initializer and each supported version-1/2/3 adoption path. Their exact table catalogs are frozen under [Fixtures/FormatFour](../../tests/EnterpriseCaseIntelligence.Api.Tests/Fixtures/FormatFour), independently of the new model/migration adapter. [VersionFourCaseStore](../../tests/EnterpriseCaseIntelligence.Api.Tests/VersionFourCaseStore.cs) combines those frozen catalogs with deterministic synthetic source values/history/bindings. Existing frozen version-1/2/3 DDL and historical initial migration/designer remain unchanged.

[Lifecycle tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CasePriorityLifecycleTests.cs) exercise all four format-4 layout families, UTF-8/UTF-16, empty/205-case files and supported initial EF 10.0.13/10.0.14 metadata. They preserve exact old table definitions, every case/text/status, history sequence/time and binding; confirm no adoption clock use, Normal assignment, original replay and refusal of repeated upgrade. Existing direct 1/2/3 tests now target 5.

[Storage tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CasePriorityStorageTests.cs) cover missing/invalid priority, no repair/default on reads/replay/no-ops, ignored/aborted writes, failed commit, restart/isolation, single joined bounded provider query and both actual competing priority/Close transaction orderings. Existing creation-stage failure tests now include priority and verify rollback of all required records. [Response-loss tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseResponseLossTests.cs) suppress successful priority output after commit, then reconcile a later worker's choice by read.

The test-only [upgrade process](../../tests/EnterpriseCaseIntelligence.UpgradeFixture/UpgradeFixtureProgram.cs) uses EF diagnostic events and named pipes to stop after generated priority-table creation, an actual backfill command and second metadata insertion. Killing it recovers the whole valid format-4 source; retry preserves original creation replay. Commit-lock and lost-completion tests remain separate. No production failure/barrier hooks are added. No power-loss, scale-out or downgrade guarantee is claimed.

## Acceptance mapping

| Criteria | Backend evidence / remaining boundary |
| --- | --- |
| CP-01 | [Domain matrix](../../tests/EnterpriseCaseIntelligence.Domain.Tests/Cases/CasePriorityTests.cs): all 27 status/current/next combinations, defaults, independent status progression and invalid enums. |
| CP-02 | [API tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CasePriorityApiTests.cs): exact resources, strict body/ID/query/media validation before missing storage, safe missing/unavailable outcomes. |
| CP-03 | [Priority security tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Security/CasePrioritySecurityTests.cs) cover roles, CSRF including no-op, expiry, revision and cookie deletion in both ingress/certificate-source modes; shared anonymous/unmapped/Reader/CSRF route matrices include priority. Browser priority controls/clearing remain pending. |
| CP-04 | Single actual joined provider command with filters/order/limit+lookahead, combined traversal, later membership changes, earlier immutable pages and no per-row queries. |
| CP-05/06 | Store failure/commit/race tests, four-stage atomic creation tests and original four-field replay after reprioritization and Closed. |
| CP-07/08 | Generated operations/snapshot/STRICT/FK, independent old catalogs, direct older adoption, metadata/source refusal, real process interruption and commit/lost-output recovery. |
| CP-09 | Retained priority after restart, independent stores, retained key replay; the previously published PR #46 executable was also run locally against a disposable format-5 file and safely refused inspect, upgrade and startup without changing its bytes. No supported downgrade. |
| CP-10/11 | **Pending:** selected-case priority read/edit/filter UI, uncertainty/generation handling, access clearing and published accessibility/mobile/browser evidence. |
| CP-12 | Backend representations and existing published browser regressions remain gates; full feature acceptance awaits CP-10/11. README/architecture/runbooks identify the API capability and pending UI. |

## Next slice: connect the workspace

Implement the reviewed All/Low/Normal/High filter, independent selected-case priority read/retry and explicit Caseworker Save. Preserve Reader display, successfully loaded latest case/status/priority before editing, no differing action on Closed, auth/CSRF/session invalidation and no per-listed-case requests. Keep submitted list filters/ID ordering and independent overall status totals. A confirmed write and failed follow-up read must remain distinct. Uncertain priority/status responses require fresh current state before another explicit action; never replay automatically or replace a frozen uncertain creation draft/key. Verify concurrent Close, delayed responses, denial/expiry/sign-out, keyboard and narrow/zoomed published journeys before marking complete.

No new cloud resource, provider registration, billing/credit use, server database, ownership, priority sorting, SLA or actor audit is authorized or implemented by this work. The [$0 hosting boundary](zero-cost-review-hosting.md) remains in force.

Local validation: 874 passing tests across Domain (73), non-browser API/storage/process/security (753) and existing published browser journeys (48). Restore/tool restore, format verification, zero-warning Release builds, offline probe-envelope validation and EF snapshot agreement passed. The browser cases are regression evidence for the existing workspace, not acceptance of the pending priority UI. Changed documentation links and an exact known-private-value scan passed.
