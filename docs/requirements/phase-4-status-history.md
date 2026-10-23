# Phase 4 requirements: durable case status history

**Status: Reviewed requirements — implemented for the local workflow.** Reviewed in PR #15; [ADR 0005](../adr/0005-add-status-history-with-an-explicit-store-upgrade.md) was reviewed in PR #16 and is accepted and implemented. Normal startup never upgrades stores automatically.

[EF Core adoption / ADR 0007](../adr/0007-adopt-ef-core-code-first.md) now provides format 4 and direct adoption of retained versions 1–3 while preserving this workflow contract. Historical format references below describe their original phase.

## Workflow assessment and outcome

Before Phase 4, the workflow supported create → browse → retrieve → change status → restart and resume, but the store retained only the latest status. After reopening a case, a fictional service-desk staff member could not inspect its earlier status changes or distinguish recorded transitions from an inherited starting state. The [current walkthrough](../usage/local-case-workflow.md) now includes status history and explicit store upgrades.

A read-only status history for each synthetic case is retained with its current state. This adds evidence about the existing lifecycle without expanding its states or requiring a new deployment. These are reference-project assumptions, not validated enterprise requirements.

The current lifecycle allows at most two actual transitions. A starting entry plus those transitions gives a maximum of three entries per case. A bounded history therefore needs no pagination, search or generic event framework. Revisit this limit if reopening or additional states are proposed later.

## Scope and limits

- Keep `New → InReview → Closed`, including successful same-status no-ops and the complete existing transition matrix.
- Preserve the existing four-field case representation, case routes, browsing contract, `/` and `/health`. Add only the history read route below.
- Persist history atomically with case creation and actual status changes. History is application-owned; clients cannot submit, edit or delete entries.
- Keep one API process per local SQLite store, synthetic data only, and the existing process-recovery limits.
- Record state changes, not request attempts. Failed requests, reads and successful no-ops create no entries.
- Do not add actor identities, comments, reasons, case text copies, assignment, new case fields, authentication, a frontend, messaging, cloud deployment or AI.
- This is workflow history, without tamper evidence, access control or proof of who performed an action. It is not a compliance audit trail or an event-sourced replacement for the case model. Local file access can alter both current state and history.

## History entry contract

Each entry has exactly these fields:

| Field | Rule |
| --- | --- |
| `sequence` | Server-owned integer, starting at 1 for each case and increasing contiguously by 1 for each added entry. It determines display order within that case. |
| `kind` | Exact string `Created`, `Baseline` or `StatusChanged`. |
| `fromStatus` | JSON `null` for the first entry; otherwise the exact preceding status. |
| `toStatus` | Exact string `New`, `InReview` or `Closed`. |
| `recordedAtUtc` | Server-recorded UTC timestamp, serialized as `yyyy-MM-ddTHH:mm:ss.fffffffZ`, for example `2026-10-19T12:00:00.0000000Z`. |

Timestamps describe when the application recorded an entry. They are not client-supplied, transaction commit timestamps or a guarantee of accurate wall-clock time. Equal or decreasing timestamps are allowed when the clock repeats or moves backward; order by `sequence`, never by time. No ordering across cases is promised.

### New cases

A newly created case starts with one `Created` entry: sequence 1, `fromStatus: null`, `toStatus: "New"`. Creation of the case and this entry must commit together before returning `201`. All subsequent entries are `StatusChanged` entries matching actual allowed transitions. There is no later `Created` or `Baseline` entry.

For example, a new case progressed to `InReview` has this history:

```json
{
  "items": [
    {
      "sequence": 1,
      "kind": "Created",
      "fromStatus": null,
      "toStatus": "New",
      "recordedAtUtc": "2026-10-19T12:00:00.0000000Z"
    },
    {
      "sequence": 2,
      "kind": "StatusChanged",
      "fromStatus": "New",
      "toStatus": "InReview",
      "recordedAtUtc": "2026-10-19T12:01:00.0000000Z"
    }
  ]
}
```

### Existing cases and compatibility

The Phase 3 store is version 1 and contains no history. Its earlier transitions and creation times cannot be recovered. Phase 4 initialized/hosted version 2 and explicitly upgraded version 1. [ADR 0006](../adr/0006-bind-creation-keys-in-the-local-case-store.md) now extends this to version 3 with direct upgrades from versions 1/2, preserving these history rules. Preserve each existing case's ID, title, description and current status through that offline upgrade; never fabricate those earlier events.

At upgrade, each existing case receives exactly one `Baseline` entry: sequence 1, `fromStatus: null`, `toStatus` equal to its retained current status, and the time the upgrade records that baseline. A baseline for `Closed` does not imply that the application observed its earlier `New` or `InReview` transitions. Future real transitions append normally; earlier history remains unknown.

The upgrade must be an explicit command against an existing supported version-1 store while HTTP hosting is stopped. It must validate the source schema and all existing cases, preserve the current data, and commit the new history, schema metadata and baselines atomically. A known pre-commit failure leaves the old logical store intact. An interrupted upgrade may require normal SQLite recovery; after recovery it must be wholly old or wholly upgraded, with no partial baseline set. An error or lost completion message does not prove rollback.

Normal startup must not upgrade, reset, repair or manufacture history. New initialization must create the selected history-capable format and keep refusing every existing path. The explicit upgrade must refuse missing, empty, corrupt, unsupported or already-upgraded stores, with a concise nonzero diagnostic and no replacement file. After an uncertain outcome, inspect the supported store version before attempting another upgrade; never force a repeat or delete recovery journals. Tests use disposable copies, never user data.

[ADR 0005](../adr/0005-add-status-history-with-an-explicit-store-upgrade.md) amends [ADR 0004](../adr/0004-use-sqlite-for-the-durable-local-workflow.md)'s original exact version-1 checks. It selects the version-2 schema, explicit `upgrade-store`/`inspect-store` commands and transaction/recovery checks. No ORM, service, provider abstraction or separate production persistence project was added.

## HTTP read contract

`GET /cases/{id}/history` returns `200 OK` with `application/json` and exactly one top-level field, `items`. Return the complete validated history in ascending sequence order, with one to three entries. There is no continuation, total count, filtering or client-selected ordering. Any query parameter is invalid; an absent query or bare trailing `?` is accepted.

Use the existing route-ID rule: a non-empty hyphenated UUID, accepting uppercase hex and normalizing before lookup. Validate the ID and reject query parameters before accessing storage. Invalid IDs return `400 validation_failed` with `errors.id`; query parameters return that code with `errors.query`. Never echo submitted IDs, parameter names or values in problems. A valid missing case returns `404 case_not_found`, not an empty successful history.

Read the current case and its complete history from one consistent storage view, then return immutable values. Validate the entire chain: recognized kinds/statuses, valid timestamps, exactly one valid starting entry, contiguous sequences, matching adjacent statuses, allowed non-no-op transitions, at most three entries and a final status equal to the current case status. A `Created` start must target `New`; a `Baseline` start may target any retained valid status. Do not require timestamp monotonicity. Bound entry materialization; do not silently truncate an overlong history into a successful response.

A missing history for an existing case, an invalid chain or unavailable storage returns generic `500 internal_error`, never an empty/partial `200`, fabricated baseline, `404` or domain conflict. Use the existing `application/problem+json` shape, `about:blank`, corresponding HTTP title/status and safe stable codes. Omit exception details, SQL, paths and case text.

The response describes one read view. A later request may observe a new transition. Reading history does not reserve a case, authorize a change or provide stale-client protection.

## Write, failure and concurrency requirements

Creation and each actual transition must persist current state and its new history entry in the same transaction. Preserve commit-before-success and the existing host gate. Check history consistency before changing an existing case or acknowledging its current status. A damaged existing history must fail safely rather than being extended or repaired.

Repeating the current status returns the existing successful case representation without appending an entry or rewriting a timestamp. Once the case has advanced further, a retry of an earlier target still follows the existing transition matrix; no blanket retry success is promised. Concurrent requests for the same next state result in one actual transition and one additional entry; subsequent same-status successes add none. Request arrival order is not promised.

For a known pre-commit failure, neither half of a create/status operation may persist alone. After an uncertain commit or lost response, the retained state and history must agree even if the client cannot tell whether the operation committed. Do not automatically replay writes. Reading a known ID and its history can reconcile a status outcome; retrying an unacknowledged create can still create a second independent case. Creation keys were deferred in Phase 4; [Phase 5](phase-5-retry-safe-creation.md) now supports reconciling creation with a retained key/request.

Existing retrieval and list operations keep their case representation and validation contracts. They do not become history endpoints or make a new promise of checking every history chain on every case read. Root metadata and host-liveness behavior remain unchanged.

## Acceptance criteria

These criteria describe the implemented local workflow; the coverage table below links the automated evidence.

| ID | Given / when | Required result |
| --- | --- | --- |
| P4-01 | Create a valid synthetic case in a fresh history-capable store | Existing `201`, `Location` and four-field body; history contains exactly one valid `Created` entry committed with the case. |
| P4-02 | Progress `New` → `InReview` → `Closed`, reading after each step | Exactly one added `StatusChanged` entry per real change; contiguous sequences, correct chain/final status, unchanged ID/text; complete history has three entries. |
| P4-03 | Repeat current-status requests in all states; exercise rejected transitions, invalid input, unsupported media types and missing cases | Existing status/problem behavior; no added entry or timestamp rewrite; rejected writes change neither state nor history. |
| P4-04 | Read new, progressed and baseline cases, including uppercased route IDs; supply invalid IDs, unknown query parameters and valid missing IDs | Exact envelope/entry fields and sequence order; `400` before storage access for invalid input, `404` for a missing case; no partial/empty success for an existing case. |
| P4-05 | Read a history, then change status; separately create/read independent cases and stores | Earlier values stay immutable; a later read sees the committed transition; sequences and data remain case/store-local. |
| P4-06 | Coordinate concurrent same-next-status requests and a sequence of competing legal/illegal targets | Only actual permitted transitions persist; no duplicate entries, sequence gaps or broken chains. Same-status successes append nothing. |
| P4-07 | Fail creation or status change after one side has written but before commit, using actual SQLite fixtures | Safe `500`; reopen the store and prove rollback of both case and history changes. Test both case-write and history-write failures without production test routes. |
| P4-08 | Suppress a successful response after commit; separately force-kill the API after acknowledged writes and restart | Current state and complete history agree after reopening; acknowledged writes survive; reconciling/repeating current status adds no duplicate history. No power/storage-loss claim. |
| P4-09 | Explicitly upgrade a valid version-1 store containing cases in all three states | IDs/text/statuses preserved; one honest `Baseline` per case; later transitions append correctly; no invented creation time or earlier events. A fresh empty version-1 store can also be upgraded. |
| P4-10 | Refuse unsupported upgrade inputs; fail or interrupt a valid upgrade; repeat against an upgraded store; try normal startup on an old store | No replacement, automatic upgrade or partial baseline set. After recovery, the store is wholly old or wholly upgraded. Old-format normal startup fails safely; already-upgraded explicit upgrade refuses without modification. |
| P4-11 | Corrupt a history or make runtime storage unavailable; try extending or acknowledging a damaged chain | Safe `500`, without truncation, partial success, repair or invented entries. Cover missing/extra entries, gaps, invalid kinds/statuses/times, chain mismatch and final-state disagreement. Invalid HTTP input still returns `400` without storage I/O. |
| P4-12 | Supply a controlled clock with equal and decreasing times; run the full existing suite and updated setup/walkthrough | Timestamps follow the controlled UTC source; sequence order remains correct. Existing case/browsing/lifecycle/root/health contracts and all quality gates pass locally and in Linux CI. |

## Review and implementation sequence

1. **Reviewed in PR #15:** scope, entry contract, baseline meaning and acceptance criteria. Amend these assumptions explicitly if the desired capability changes.
2. **Reviewed in PR #16 and implemented:** [ADR 0005](../adr/0005-add-status-history-with-an-explicit-store-upgrade.md) selects exact schema/version checks and the explicit offline upgrade path, retaining local storage and safety limits.
3. **Implemented:** history, atomic writes and upgrade within existing production boundaries. Domain remains independent of HTTP, SQLite and wall-clock access. Framework `TimeProvider` supplies controllable time without a new package or generic event infrastructure.
4. **Verified:** disposable real SQLite stores, isolated HTTP hosts and API child processes. Concurrency, exceptions, locks and process interruption are coordinated without arbitrary sleeps. Format verification, warnings-as-errors, Release build and tests remain required.
5. **Updated:** the walkthrough, architecture and README describe the implemented capability and its limits.

## Current implementation coverage

| Criteria | Evidence |
| --- | --- |
| P4-01–05 | [History API tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseHistoryApiTests.cs): exact fields, full lifecycle, uppercased IDs, validation before I/O, immutable prior views, restart and case/file isolation. [Status tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseStatusEndpointTests.cs) verify the nine-cell matrix and history preservation for invalid/no-op requests. |
| P4-06 | Status tests coordinate same-next-state requests and assert one entry; [durability tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseDurabilityTests.cs) exercise competing targets and verify final state/history agreement. |
| P4-07, P4-11 | [History failure tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseHistoryFailureTests.cs): real-engine failures at case/history writes, unexpected inserted-row counts and malformed/missing/overlong chains; safe failures without partial success or repair. Existing storage tests retain trigger, lock and case-value failures. |
| P4-08 | [Response-loss tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseResponseLossTests.cs) inspect committed history after suppressed responses; [process recovery](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseProcessRecoveryTests.cs) force-kills the API after acknowledged writes and compares history after restart/no-op retries. |
| P4-09–10 | [Upgrade tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseStoreUpgradeTests.cs): frozen version-1 inputs in all statuses, empty/205-case stores, preserved text/IDs, honest baselines, later transitions, repeated-upgrade refusal, invalid inputs, source validation, controlled recording failures and a real commit lock failure after the version write. |
| P4-10 | [Upgrade process tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseUpgradeProcessTests.cs): kill a child at a named-pipe barrier after DDL/partial baselines, inspect recovered version 1 and retry deliberately; separately suppress completion after a real commit and inspect the current format 4 without duplicating baselines. Controls live in a [test-only executable](../../tests/EnterpriseCaseIntelligence.UpgradeFixture/UpgradeFixtureProgram.cs). |
| P4-12 | History tests inject equal/backward UTC readings through `TimeProvider`, including the real HTTP host; [walkthrough](../usage/local-case-workflow.md) covers setup, history, upgrade, inspection and recovery. Existing domain/case/list/root/health tests remain in the full suite. |

The production implementation remains in Api; no Domain, package or production-project change was needed. Tests add one isolated executable for deterministic upgrade interruption, without production fault switches. The evidence covers retained intact storage and process termination, without claiming power-loss recovery, actor identification or tamper evidence.
