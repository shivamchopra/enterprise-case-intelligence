# Case workload overview

**Status: implemented and verified locally.** This local product slice gives Readers and Caseworkers an overview of saved synthetic work while free cloud hosting remains unresolved. It follows the [workspace](../requirements/phase-6-case-workspace.md) and [known-ID lookup](open-case-by-id.md); no case ownership, prioritization or new lifecycle rule is implied.

## Contract

Add authorized `GET /cases/summary`, with no query parameters. A bare trailing `?` is accepted. Any decoded query name, including list parameters, returns the existing safe `400 validation_failed` with `errors.query` before store access. Authentication and Reader/Caseworker authorization precede validation and I/O. Case responses remain no-store; the exact literal route is separate from `GET /cases/{id}`.

A successful JSON response has exactly four nonnegative 64-bit integer fields: `new`, `inReview`, `closed` and `total`. Include zero for every missing status; an empty store has four zeroes. Total is the checked sum of the three counts. Count persisted case rows by their current recognized status, across the entire configured shared workspace, independent of title/status filters, continuation and history-entry count. Creation-key replay and same-status requests do not increment counts. This is a current workload count, not an actor audit, age/SLA metric or priority ranking.

Use one EF Core translated `GroupBy`/`LongCount` query and one read view. Materialize at most four status/count groups, never whole case records or every list page. There are only three valid statuses, so any unrecognized status among those groups fails the operation; a fourth group cannot be valid. Unavailable storage/provider or invalid loaded status uses generic `500 internal_error`, never zero/partial success, repair or replacement storage. The operation does not inspect all case text/IDs, history/key consistency or storage integrity; the explicit offline inspector retains that responsibility. Aggregation can scan the table; the four-group bound is a materialization bound, not a latency/scan guarantee.

## Browser behavior

Show **Workload overview** with New, In review, Closed and Total under the protected workspace. Label it **All saved cases**, independent of list filters/paging and a separate read from case/list/history views. Load once after access bootstrap and on **Refresh totals**, and refresh after a confirmed creation or status response, including an explicit successful retry. No polling, list traversal or automatic write/replay is added. Filtering, paging and ID lookup do not reload totals or change the selected case/draft.

Clear/hide previous totals during a refresh and show loading or a fixed recoverable error. An unavailable result must never look like zero workload. Confirmed write feedback remains separate from a failed totals refresh. Accept only nonnegative JavaScript-safe integer counts whose checked-safe total agrees; malformed/inconsistent/unsafe values are unavailable, not rounded successes. A delayed earlier success/failure must not replace a newer refresh. On session loss, expiry, access refresh or sign-out, clear counts and discard earlier-session responses; no authorized read means no overview request.

Use labeled native Refresh control, a semantic description list, accessible loading/error feedback and responsive counts. No case text, ID, identity, secret or raw failure body belongs in the aggregate response. No persistent browser storage, external asset or telemetry is introduced.

## Acceptance plan

| ID | Evidence required |
| --- | --- |
| WO-01 | Empty and mixed-status stores return the exact four-field integer response; totals cover more than a list page and reflect successful writes/no-ops/replay correctly. |
| WO-02 | Queries fail safely before unavailable storage is opened; authorization wins first. Missing storage does not initialize a replacement; invalid statuses fail without partial success or repair. |
| WO-03 | Actual SQLite execution uses one translated grouped count query with a four-group limit and no case-text materialization; immutable earlier results, retained restart and independent stores work. |
| WO-04 | Browser bootstrap/manual refresh/confirmed mutations load correct totals independent of search/status/page/lookup, without polling or writes; empty, loading, error and recovery states are distinct. |
| WO-05 | Delayed successes/failures, malformed counts and session loss cannot restore stale/invalid data; Reader HTTPS/proxy access, sign-out and absolute expiry clear the protected overview. |
| WO-06 | Published browser journey, narrow/zoomed layout, full repository gates and documentation reflect the implemented scope. |

No EF model, migration, schema version, Domain dependency or additional production project is needed. The aggregate uses the existing per-operation context and local process serialization. [EF Core aggregate translation](https://learn.microsoft.com/en-us/ef/core/querying/complex-query-operators#groupby) informs the query; real SQLite tests establish the supported provider behavior. Cloud quotas, billing, deployment and server-provider adoption remain separate gates.

## Implementation evidence

- [Provider/API tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseSummaryTests.cs) cover empty/mixed datasets beyond one page, replay/no-op semantics, immutable earlier results, validation before I/O, failure without repair, retained restart and independent stores. A scoped EF diagnostic observer captures the real SQLite command and checks grouped counting, the four-group limit and absence of case text/ID columns.
- [Authorization tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Security/CaseSecurityTests.cs) include the malformed summary route in anonymous/unmapped denial precedence and Reader access. [Authenticated browser tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Security/AuthenticatedBrowserTests.cs) verify Reader/Caseworker totals across direct/proxied HTTPS and both certificate sources, plus sign-out and absolute-expiry clearing.
- [Overview browser tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Workspace/CaseOverviewBrowserTests.cs) cover bootstrap, manual/current totals, independent list filters/paging/lookup, confirmed writes, stale success/failure ordering, bad counts, loading/unavailable/recovery and late responses after access loss. Existing browser journeys continue to cover frozen creation retries and known-ID lookup.

Validation: all 733 tests passed (42 Domain, 643 non-browser API/storage/process and 48 real-browser cases). Browser tests ran against a fresh published API and provider/process tests used the independently published probe. Restore/tool restore, format verification, zero-warning Release builds, offline probe-envelope check and EF snapshot agreement passed. Desktop and narrow/200%-text screenshots were inspected; public documentation and the README screenshot are updated.
