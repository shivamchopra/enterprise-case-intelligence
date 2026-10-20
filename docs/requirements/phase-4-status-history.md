# Phase 4 proposal: durable case status history

**Status: Proposed requirements — not implemented.** Phase 3 remains the current runnable scope. This document proposes the next slice for review; it does not select a new schema or authorize automatic upgrades.

## Workflow assessment and proposed outcome

The [current workflow](../usage/local-case-workflow.md) supports create → browse → retrieve → change status → restart and resume. The [case store](../../src/EnterpriseCaseIntelligence.Api/Cases/SqliteCaseStore.cs) retains only the latest status. After reopening a case, a fictional service-desk staff member cannot inspect its earlier status changes or distinguish recorded transitions from an inherited starting state.

Propose a read-only status history for each synthetic case, retained with its current state. This adds evidence about the existing lifecycle without expanding its states or requiring a new deployment. These are reference-project assumptions, not validated enterprise requirements.

The current lifecycle allows at most two actual transitions. A starting entry plus those transitions gives a maximum of three entries per case. A bounded history therefore needs no pagination, search or generic event framework. Revisit this limit if reopening or additional states are proposed later.

## Scope and limits

- Keep `New → InReview → Closed`, including successful same-status no-ops and the complete existing transition matrix.
- Preserve the existing four-field case representation, case routes, browsing contract, `/` and `/health`. Add only the proposed history read route below.
- Persist history atomically with case creation and actual status changes. History is application-owned; clients cannot submit, edit or delete entries.
- Keep one API process per local SQLite store, synthetic data only, and the existing process-recovery limits.
- Record state changes, not request attempts. Failed requests, reads and successful no-ops create no entries.
- Do not add actor identities, comments, reasons, case text copies, assignment, new case fields, authentication, a frontend, messaging, cloud deployment or AI.
- This is workflow history, without tamper evidence, access control or proof of who performed an action. It is not a compliance audit trail or an event-sourced replacement for the case model. Local file access can alter both current state and history.

## History entry contract

Each entry has exactly these fields:

| Field | Proposed rule |
| --- | --- |
| `sequence` | Server-owned integer, starting at 1 for each case and increasing contiguously by 1 for each added entry. It determines display order within that case. |
| `kind` | Exact string `Created`, `Baseline` or `StatusChanged`. |
| `fromStatus` | JSON `null` for the first entry; otherwise the exact preceding status. |
| `toStatus` | Exact string `New`, `InReview` or `Closed`. |
| `recordedAtUtc` | Server-recorded UTC timestamp, serialized as `yyyy-MM-ddTHH:mm:ss.fffffffZ`, for example `2026-10-19T12:00:00.0000000Z`. |

Timestamps describe when the application recorded an entry. They are not client-supplied, transaction commit timestamps or a guarantee of accurate wall-clock time. Equal or decreasing timestamps are allowed when the clock repeats or moves backward; order by `sequence`, never by time. No ordering across cases is promised.

### New cases

A newly created case starts with one `Created` entry: sequence 1, `fromStatus: null`, `toStatus: "New"`. Creation of the case and this entry must commit together before returning `201`. All subsequent entries are `StatusChanged` entries matching actual allowed transitions. There is no later `Created` or `Baseline` entry.

For example, a new case progressed to `InReview` would have this proposed history:

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

The currently supported store is version 1 and contains no history. Its earlier transitions and creation times cannot be recovered. Preserve each existing case's ID, title, description and current status through an explicit, offline store upgrade; never fabricate those earlier events.

At upgrade, each existing case receives exactly one `Baseline` entry: sequence 1, `fromStatus: null`, `toStatus` equal to its retained current status, and the time the upgrade records that baseline. A baseline for `Closed` does not imply that the application observed its earlier `New` or `InReview` transitions. Future real transitions append normally; earlier history remains unknown.

The upgrade must be an explicit command against an existing supported version-1 store while HTTP hosting is stopped. It must validate the source schema and all existing cases, preserve the current data, and commit the new history, schema metadata and baselines atomically. A known pre-commit failure leaves the old logical store intact. An interrupted upgrade may require normal SQLite recovery; after recovery it must be wholly old or wholly upgraded, with no partial baseline set. An error or lost completion message does not prove rollback.

Normal startup must not upgrade, reset, repair or manufacture history. New initialization must create the selected history-capable format and keep refusing every existing path. The explicit upgrade must refuse missing, empty, corrupt, unsupported or already-upgraded stores, with a concise nonzero diagnostic and no replacement file. After an uncertain outcome, inspect the supported store version before attempting another upgrade; never force a repeat or delete recovery journals. Tests use disposable copies, never user data.

[ADR 0004](../adr/0004-use-sqlite-for-the-durable-local-workflow.md) currently requires exact version-1 schema validation and refusal of schema changes. A new compatibility/storage ADR is required after requirements review and before implementation. It must choose the history schema, new version, upgrade command and transaction/recovery checks, and explain the amendment to ADR 0004. No ORM, service, provider abstraction or separate persistence project is selected by this proposal.

## Proposed HTTP read contract

`GET /cases/{id}/history` returns `200 OK` with `application/json` and exactly one top-level field, `items`. Return the complete validated history in ascending sequence order, with one to three entries. There is no continuation, total count, filtering or client-selected ordering. Any query parameter is invalid; an absent query or bare trailing `?` is accepted.

Use the existing route-ID rule: a non-empty hyphenated UUID, accepting uppercase hex and normalizing before lookup. Validate the ID and reject query parameters before accessing storage. Invalid IDs return `400 validation_failed` with `errors.id`; query parameters return that code with `errors.query`. Never echo submitted IDs, parameter names or values in problems. A valid missing case returns `404 case_not_found`, not an empty successful history.

Read the current case and its complete history from one consistent storage view, then return immutable values. Validate the entire chain: recognized kinds/statuses, valid timestamps, exactly one valid starting entry, contiguous sequences, matching adjacent statuses, allowed non-no-op transitions, at most three entries and a final status equal to the current case status. A `Created` start must target `New`; a `Baseline` start may target any retained valid status. Do not require timestamp monotonicity. Bound entry materialization; do not silently truncate an overlong history into a successful response.

A missing history for an existing case, an invalid chain or unavailable storage returns generic `500 internal_error`, never an empty/partial `200`, fabricated baseline, `404` or domain conflict. Use the existing `application/problem+json` shape, `about:blank`, corresponding HTTP title/status and safe stable codes. Omit exception details, SQL, paths and case text.

The response describes one read view. A later request may observe a new transition. Reading history does not reserve a case, authorize a change or provide stale-client protection.

## Write, failure and concurrency requirements

Creation and each actual transition must persist current state and its new history entry in the same transaction. Preserve commit-before-success and the existing host gate. Check history consistency before changing an existing case or acknowledging its current status. A damaged existing history must fail safely rather than being extended or repaired.

Repeating the current status returns the existing successful case representation without appending an entry or rewriting a timestamp. Once the case has advanced further, a retry of an earlier target still follows the existing transition matrix; no blanket retry success is promised. Concurrent requests for the same next state result in one actual transition and one additional entry; subsequent same-status successes add none. Request arrival order is not promised.

For a known pre-commit failure, neither half of a create/status operation may persist alone. After an uncertain commit or lost response, the retained state and history must agree even if the client cannot tell whether the operation committed. Do not automatically replay writes. Reading a known ID and its history can reconcile a status outcome; retrying an unacknowledged create can still create a second independent case. Creation idempotency keys remain deferred.

Existing retrieval and list operations keep their case representation and validation contracts. They do not become history endpoints or make a new promise of checking every history chain on every case read. Root metadata and host-liveness behavior remain unchanged.

## Acceptance criteria

These criteria are proposed verification obligations, not claims about current code or test coverage.

| ID | Given / when | Required result |
| --- | --- | --- |
| P4-01 | Create a valid synthetic case in a fresh history-capable store | Existing `201`, `Location` and four-field body; history contains exactly one valid `Created` entry committed with the case. |
| P4-02 | Progress `New` → `InReview` → `Closed`, reading after each step | Exactly one added `StatusChanged` entry per real change; contiguous sequences, correct chain/final status, unchanged ID/text; complete history has three entries. |
| P4-03 | Repeat current-status requests in all states; exercise rejected transitions, invalid input, unsupported media types and missing cases | Existing status/problem behavior; no added entry or timestamp rewrite; rejected writes change neither state nor history. |
| P4-04 | Read new, progressed and baseline cases, including uppercased route IDs; supply invalid IDs, unknown query parameters and valid missing IDs | Exact proposed envelope/entry fields and sequence order; `400` before storage access for invalid input, `404` for a missing case; no partial/empty success for an existing case. |
| P4-05 | Read a history, then change status; separately create/read independent cases and stores | Earlier values stay immutable; a later read sees the committed transition; sequences and data remain case/store-local. |
| P4-06 | Coordinate concurrent same-next-status requests and a sequence of competing legal/illegal targets | Only actual permitted transitions persist; no duplicate entries, sequence gaps or broken chains. Same-status successes append nothing. |
| P4-07 | Fail creation or status change after one side has written but before commit, using actual SQLite fixtures | Safe `500`; reopen the store and prove rollback of both case and history changes. Test both case-write and history-write failures without production test routes. |
| P4-08 | Suppress a successful response after commit; separately force-kill the API after acknowledged writes and restart | Current state and complete history agree after reopening; acknowledged writes survive; reconciling/repeating current status adds no duplicate history. No power/storage-loss claim. |
| P4-09 | Explicitly upgrade a valid version-1 store containing cases in all three states | IDs/text/statuses preserved; one honest `Baseline` per case; later transitions append correctly; no invented creation time or earlier events. A fresh empty version-1 store can also be upgraded. |
| P4-10 | Refuse unsupported upgrade inputs; fail or interrupt a valid upgrade; repeat against an upgraded store; try normal startup on an old store | No replacement, automatic upgrade or partial baseline set. After recovery, the store is wholly old or wholly upgraded. Old-format normal startup fails safely; already-upgraded explicit upgrade refuses without modification. |
| P4-11 | Corrupt a history or make runtime storage unavailable; try extending or acknowledging a damaged chain | Safe `500`, without truncation, partial success, repair or invented entries. Cover missing/extra entries, gaps, invalid kinds/statuses/times, chain mismatch and final-state disagreement. Invalid HTTP input still returns `400` without storage I/O. |
| P4-12 | Supply a controlled clock with equal and decreasing times; run the full existing suite and updated setup/walkthrough | Timestamps follow the controlled UTC source; sequence order remains correct. Existing case/browsing/lifecycle/root/health contracts and all quality gates pass locally and in Linux CI. |

## Review and implementation sequence

1. Review this proposed scope, entry contract, baseline meaning and acceptance criteria. Amend assumptions before implementation if the desired next capability differs.
2. Propose and review the compatibility/storage ADR required by the schema change. Choose exact schema/version checks and the explicit offline upgrade path; retain existing local storage and safety limits.
3. Implement the smallest history operation, atomic writes and upgrade within the existing boundaries. Keep Domain independent of HTTP, SQLite and wall-clock access. Use a controllable time source without a new test framework or generic event infrastructure.
4. Verify with disposable real SQLite stores, isolated HTTP hosts and API child processes. Coordinate concurrency and failure controls deterministically; do not use arbitrary sleeps or mocked storage as the only atomicity/recovery evidence. Keep format verification, warnings-as-errors, Release build and tests.
5. Update the local walkthrough and public implemented-scope evidence only after the capability exists. Keep proposed requirements separate from current behavior until then.

This proposal adds no runtime code, schema, migration, package or infrastructure. Approval of these requirements alone does not establish that history is implemented or that the storage design has been selected.
