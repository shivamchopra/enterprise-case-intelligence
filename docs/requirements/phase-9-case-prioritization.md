# Phase 9: prioritize saved synthetic cases

**Status: reviewed and implemented for the local workflow.** Domain/store/API rules, explicit format-5 upgrades and browser priority filtering/read/edit/reconciliation are implemented. [Delivery evidence](../tasks/case-prioritization.md) maps CP-01–12 to passing tests. [Hosted acceptance](phase-8-secure-hosting.md) remains pending under the [$0 cost boundary](../tasks/zero-cost-review-hosting.md); this work does not authorize cloud changes.

## Scenario and outcome

A fictional service-desk Caseworker needs to identify high-priority work and adjust its priority as the situation changes. A Reader needs to find that work and inspect the selected case's current priority. These are reference-project assumptions, not validated enterprise requirements. Priority expresses an explicit staff choice; it does not claim an SLA, urgency prediction, ownership or automatic ranking.

The values are exactly **Low**, **Normal** and **High**. Every newly created case starts at Normal. Existing cases receive Normal during an explicit offline upgrade; that is a newly assigned default, not evidence of their historical priority. Keep title/description immutable and the existing `New → InReview → Closed` lifecycle unchanged.

## Business rules

| Current status | Requested priority | Result |
| --- | --- | --- |
| New or InReview | Any recognized value | Set the requested priority; success only after commit. |
| Closed | Same as current priority | Successful no-op, after the same authorization and retained-state validation as any write. |
| Closed | Different recognized value | `409 case_closed`; change nothing. |
| Any | Missing/unrecognized priority | `400 validation_failed`, before store access. |

Priority changes do not advance status, reopen cases or append status-history entries. Existing history remains **status history**; it is not an audit of priority changes. This first slice stores current priority without actor/time/change history. The UI and documentation must make that boundary clear. Revisit the contract before adding audited triage, ownership, SLA rules or priority sorting.

One workspace-wide priority is shared by all authorized staff. There is no reservation or optimistic concurrency token. Each accepted write evaluates the then-current case/status in the existing serialized transaction boundary. A later authorized write can replace an earlier priority. If Close commits first, a subsequent different-priority write conflicts; if a priority write commits first, Close preserves it. Do not imply either operation reserved the case between requests.

## HTTP contract

Preserve the exact existing four-field case representation (`id`, `title`, `description`, `status`) on creation, retrieval, listing and retained-key replay. Expose priority as a separate resource, as decided in [ADR 0016](../adr/0016-expose-case-priority-without-changing-creation-replay.md).

| Operation | Proposed contract |
| --- | --- |
| `GET /cases/{id}/priority` | Reader or Caseworker read. `200` with exactly `caseId` and `priority`; canonical lowercase UUID and one exact priority name. |
| `PUT /cases/{id}/priority` | Caseworker write with existing CSRF validation. JSON body exactly `{ "priority": "High" }` (or Low/Normal); `200` with the same two-field representation, including no-ops. |
| `GET /cases?priority=High` | Add one optional, case-sensitive query name `priority`, supplied at most once. Combine with title/status/exclusive-ID boundary before ordering and taking the page. |

Priority route IDs use the existing non-empty hyphenated UUID validation. The two priority routes accept no query parameters. Values are exact Low/Normal/High after normal JSON/query decoding: reject numeric enum values, aliases, case changes and surrounding whitespace. Duplicate, absent, extra or non-string JSON fields follow the existing strict validation/media/body limits (`errors.priority` for the priority field and `errors.body` for unsupported fields). Invalid route/query input uses the existing `errors.id`/`errors.query` conventions. Reject invalid input before I/O without echoing submitted names/values.

Apply the existing precedence: authentication → authorization → required CSRF validation on writes → input/media/body validation → store access. Anonymous/unmapped sessions cannot use a priority read or filter to learn that a case exists. Readers cannot change priorities, including no-ops. Caseworker no-ops require CSRF. A valid absent case returns existing `404 case_not_found`; unavailable/incompatible/corrupt state encountered during the operation returns generic `500 internal_error`. All responses remain no-store with the existing problem shape and no credentials, paths, SQL or raw exceptions.

The priority filter preserves default 20/max 100, `limit + 1` lookahead and the exact `items`/`nextAfter` response. Unknown, duplicated, blank or invalid priority filters use `400 validation_failed` with `errors.priority` before I/O. Retain canonical-ID ordering; this is **not** priority order or FIFO. Changing any filter starts again without `after`; later membership can change between requests. The [overall workload summary](../tasks/case-workload-overview.md) remains independent of all list filters, with the same four status/total counts. No priority-count or filtered-total endpoint is proposed.

## Creation and retained replay

`POST /cases` continues to accept only title and description. Supplying priority there remains an unsupported field. Create the new case's Normal priority in the same transaction as its case, Created history and optional creation-key binding; roll back the whole creation on any failed write or commit. Never leave a case without its required priority state.

A known creation key still compares only normalized immutable title/description and returns the **original four-field New result**, even after priority/status changes. It must not reset priority, return a newer state as the original result, expire/reassign the key or create another case. Validate retained case/history/binding state as today, extended for required priority state; invalid priority must not be repaired during replay. Older unbound cases remain unbound.

The client retrieves current priority separately after creation/replay. A creation key is not a priority-write key; priority writes do not consume or create bindings. After an uncertain priority response, read current case/priority before an explicit further write. A blind retry could replace another worker's later choice, so neither API nor UI promises exactly-once priority updates or automatic write replay.

## Browser behavior

Add an All/Low/Normal/High priority filter beside existing list filters. Explain that filtering finds a chosen priority while ordering remains by ID. Next, list refresh and post-mutation refresh retain submitted title/status/priority; changing a filter starts from the first page. Keep lookup by known ID independent of list membership. The four-field list does not acquire per-row priority badges or automatic per-row fetches in this slice.

Load selected-case priority through its own bounded read, with independent loading/error/retry state and the same selection/session generation checks. Show current priority to Readers and Caseworkers; show an explicit priority selector and Save action only to Caseworkers. Disable duplicate writes while saving. A Closed case exposes no different-priority action. Editing requires successfully loaded current case status and priority for the latest selection. A failed priority or case read must not invent Normal/status or leave stale priority editable; details/history/priority remain independently recoverable.

A successful priority write is confirmed separately from follow-up reads. Reload selected priority/current status and the first filtered list page; refresh the independent workload overview consistently with other confirmed writes, without implying an aggregate snapshot. Handle a concurrent Close with safe conflict/reconciliation feedback. A priority failure or uncertainty requires current-state retrieval before another action, never an automatic write or draft replacement. List/priority refreshes must preserve a frozen uncertain creation key/payload.

On denial, expiry, access refresh or sign-out, clear current priority/feedback and invalidate prior responses with other protected views. Retain no case priority/ID/text or creation key in persistent browser storage or the workspace address. Normal API paths/query values remain observable to HTTP infrastructure; use synthetic data only. Use labels, keyboard controls, visible focus, accessible feedback and narrow/zoomed layouts.

## Persistence and compatibility

Keep Domain independent of EF/provider types. Model priority in C# and configure stored constraints through Fluent API. Scaffold EF Core migrations and check in their snapshot; do not maintain a parallel schema/data-upgrade SQL script. [The delivery task](../tasks/case-prioritization.md) records the current runner's single-migration assumption and the required real-provider proof before selecting the physical schema change.

**Format 5** implements this capability through a scaffolded required one-to-one priority table; the generated column/check candidate required a transaction-suppressed SQLite rebuild and was not selected. See ADR 0016 for the provider evidence. Explicit initialization creates the new format with no overwrite. Startup refuses older/unknown/corrupt formats without initialization, migration, repair or reset. Offline `upgrade-store` must continue to accept all supported formats 1/2/3/4 through validated, atomic procedures, preserve all old IDs/text/status/history/timestamps/keys and assign Normal once. No invented priority history or new status baseline for a format-2/3/4 case. Preserve format-1's existing honest status-baseline rule.

Cover both freshly initialized and historically adopted format-4 layouts and supported EF metadata versions. Do not compare old source schemas against the latest model or remove old compatibility to make the new migration pass. A migration that cannot preserve transaction/STRICT/foreign-key guarantees needs a documented, tested design revision; it is not permission to reset a store, rewrite history or fall back to hand-written CRUD/DDL.

## Acceptance criteria

| ID | Acceptance requirement (evidence linked in the delivery task) |
| --- | --- |
| CP-01 | Domain covers all priorities/statuses, invalid enum values, Closed no-op versus change, immutable text and independent status transitions. |
| CP-02 | New priority reads/writes have exact responses, strict ID/query/body/media validation and safe missing/unavailable/corrupt outcomes. Invalid inputs precede I/O. |
| CP-03 | Anonymous/unmapped denial, Reader read/write distinction, CSRF on changes/no-ops, expiry/revision/sign-out and both ingress/certificate-source modes remain covered. |
| CP-04 | Actual SQLite LINQ applies priority/title/status/boundary before ordering/Take; page/lookahead bounds, late-page changes, immutable earlier results and no unbounded priority materialization or per-row query are proven. |
| CP-05 | Priority writes commit before success; failed/ignored writes or commits leave priority/status/history/keys intact. Priority-versus-Close ordering has deterministic evidence. |
| CP-06 | Creation failure at every case/history/priority/key boundary rolls back; replay after reprioritization and Closed returns the same original result without resetting current state. |
| CP-07 | EF-generated initialization/migration/snapshot and metadata checks pass against real SQLite; both format-4 variants and all supported older inputs upgrade without loss. |
| CP-08 | Interrupt upgrades before/after relevant operations and suppress completion after commit; inspect valid source versus format 5 before retry. Reject malformed schema/metadata/priority without repair. |
| CP-09 | Restart, independent-store and retained-key tests preserve priority together with all existing values; old executables safely refuse the new format. No downgrade promise. |
| CP-10 | Browser filter/selection/refresh, Reader display, Caseworker save, concurrent Close, uncertain writes, late responses and frozen creation retry all use real retained state. |
| CP-11 | Accessibility, narrow/zoomed layout, no executable/raw error rendering and protected-state clearing pass against published output. |
| CP-12 | Existing case/history/creation/summary representations and lifecycle contracts remain, full gates pass and README/runbooks distinguish implementation from remaining hosting/operations work. |

No new cloud resource, identity grant, service/broker, server database, AI capability, background job, ownership, deletion or actor audit is selected. The current runtime includes the priority Domain/store/API/filter/model, format-5 support and selected-case browser workflow described above. This is current triage state, without a priority audit, staff ownership or hosted acceptance.
