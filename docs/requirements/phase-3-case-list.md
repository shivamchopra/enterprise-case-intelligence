# Phase 3 requirements: browse saved synthetic cases

**Status: Reviewed requirements — local list workflow implemented.** Requirements reviewed in PR #13; this document records the implemented contract and acceptance coverage.

**Phase 7 amendment:** [Title search](phase-7-case-title-search.md) adds the optional `title` parameter to the original query set below. The original no-search scope is historical to Phase 3; current queries combine literal case-sensitive title matching with status and ID continuation, preserving the response and bounds.

The [durable local workflow](phase-2-durable-case-workflow.md) originally required a known ID to reopen each case. Phase 3 adds a bounded, optionally status-filtered list so a fictional service-desk staff member can find saved work and resume the existing retrieve/status workflow. It is a reference-project assumption, not a validated enterprise requirement.

**Workload overview amendment:** [Overall counts](../tasks/case-workload-overview.md) now use a separate `GET /cases/summary` operation. The no-totals list contract remains unchanged: the list still has only `items` and `nextAfter`, and the overview is not a filtered/page count.

The API implements creation, listing, retrieval by ID and status changes. [ADR 0004](../adr/0004-use-sqlite-for-the-durable-local-workflow.md) remains the accepted storage decision.

## Scope and operating assumptions

- Add one read-only operation, `GET /cases`, against the same configured SQLite store.
- Reuse the four-field case representation and the three existing lifecycle states. The list does not assign, reserve, prioritize or change cases.
- Keep one API process per store, synthetic data only, and the existing initialization, restart and failure limits.
- Do not add text search, totals/counts, timestamps, sort choices, offset/page-number pagination, new case fields, audit, deletion, a frontend or authentication.
- Server-managed databases, cloud deployment, messaging and AI remain outside this slice. No package, schema, index, layer or service change is selected by these requirements.

## HTTP contract

`GET /cases` returns `200 OK` with `application/json`. It accepts only these case-sensitive query names, each at most once:

| Parameter | Rule |
| --- | --- |
| `status` | Optional. Exact value `New`, `InReview` or `Closed`; omission includes all statuses. `all` is not a status value. |
| `limit` | Optional; default `20`, maximum `100`. Accept canonical ASCII decimal integers `1`–`100` only, without signs, spaces, leading zeroes or decimal points. |
| `after` | Optional exclusive ID boundary. Use the existing route-ID validation rule: a non-empty hyphenated UUID. Normalize it to the server's canonical lowercase UUID representation before comparing. |

Validate decoded query names/values before storage access. Empty values, duplicate parameters (even identical values), unknown names and invalid values return `400 validation_failed`. Known parameter errors use `errors.status`, `errors.limit` or `errors.after`; unknown names may use `errors.query`. Do not echo submitted values or unknown names. Exact wording and error order are not contractual. An absent query and a bare trailing `?` both use defaults.

The response contains exactly `items` and `nextAfter`. Each item contains exactly `id`, `title`, `description` and `status`, using the [existing case contract](phase-1-case-workflow.md#http-contract). Do not add storage or host metadata.

Example response for an empty store or a filter with no matching cases:

```json
{
  "items": [],
  "nextAfter": null
}
```

Example request to browse new cases, with one case per page:

```text
GET /cases?status=New&limit=1
```

If the server returns a non-null `nextAfter`, send it as `after` on the next request while retaining the same status filter. Clients may change the valid page size. To change filters and browse from the beginning, omit `after`.

### Ordering and continuation

Order by canonical lowercase UUID string in ascending ordinal order. This is a stable browsing order, **not creation time, priority or FIFO order**. No new timestamp is needed. Apply the optional status filter and the exclusive `after` boundary before taking the page.

- Return at most `limit` items, with no repeated ID in a page.
- If more matching cases exist beyond the page in that operation's read view, `nextAfter` is the ID of the last returned item. Otherwise it is JSON `null`, including for a full page that happens to end the result set.
- The boundary need not identify an existing case or match the current filter. It is a comparison value, not a lookup; a valid boundary with no later matches returns an empty `200` page, never `404`.
- Continuation is a plain case ID, not an opaque/signed token, secret, permission or retained server session. The server does not bind it to a filter. Each request applies its own validated filter and boundary.
- With unchanged data/filter, following successive `nextAfter` values returns every matching case once in strictly increasing ID order and then terminates. No offset or total-count contract is provided.

Build each page and its continuation decision from one consistent read view. Materialize immutable values before writing the response so subsequent status changes cannot alter an earlier page's contents. Do not load the entire collection into application memory to paginate; fetch at most the requested case rows plus one lookahead row. This bounds materialization and response size, not database scan work, elapsed time or disk usage. The local dataset remains small by scope, without a measured performance guarantee.

### Changes between pages

A traversal is not a multi-request snapshot. Concurrent creates or status changes can change membership between requests. A newly matching case at or before an already-used boundary will not appear later in that traversal; a previously matching case may leave the filter before it is read. A continuation indicates that more matches existed when that page was read, not that the next request must return a non-empty page.

Start again without `after` to browse current state. Do not promise exactly-once discovery during concurrent changes, chronological order, stale-client detection or an application lock held between requests. Reading a list does not authorize or reserve a later status change; the existing status operation evaluates the then-current case state.

### Failures and compatibility

Use the existing problem format: `application/problem+json`, `about:blank`, HTTP reason title/status and stable `code`. Query validation uses `400 validation_failed` with field errors. Unavailable storage, provider failures or invalid persisted values encountered while constructing a page use generic `500 internal_error`. Do not silently skip damaged rows, return a partial successful page or substitute an empty list when storage fails. Problem bodies contain no submitted text, SQL, paths or exception details.

The list must not mutate cases, lifecycle state, schema version or initialization state. It must not create missing storage. Existing `POST /cases`, `GET /cases/{id}`, `PUT /cases/{id}/status`, `/` and `/health` retain their contracts. In particular, liveness remains independent of runtime storage readiness.

## Acceptance criteria

**P3-01–12 describe the implemented local list workflow.** The coverage table below links the automated evidence.

| ID | Given / when | Required result |
| --- | --- | --- |
| P3-01 | List an empty store, or select a status with no matches | `200`; exactly `items: []` and `nextAfter: null`; JSON media type. |
| P3-02 | Omit every parameter with more than 20 saved cases of mixed statuses | First 20 cases in canonical ID order; complete four-field items; correct continuation. |
| P3-03 | List each recognized status and omit the status filter, including valid percent-encoded equivalents | Exact membership after decoding; omission includes every status. Listing changes no case values. |
| P3-04 | Exercise limits 1, 20 and 100, plus result counts below, equal to and above the requested limit | Correct bounded page size and last-item continuation only when another match exists; no unnecessary continuation for an exactly full final page. |
| P3-05 | Traverse an unchanged dataset with several page sizes, including one-item pages | Every matching ID appears once, in ascending order; no gaps/duplicates; traversal terminates. Keep filters fixed for each traversal. |
| P3-06 | Supply a valid existing or nonexistent `after`, including uppercase hex, one from another filter or beyond all IDs | Include only matching IDs strictly greater than the normalized boundary; `200` even when empty. |
| P3-07 | Supply invalid/blank status, invalid limit syntax/range, invalid/all-zero UUID, unknown names, duplicate parameters or differently cased query names/status values | Safe `400 validation_failed` with the relevant field error, before storage access; no mutation. Include values that decode to invalid text/whitespace and repeated identical parameters. |
| P3-08 | Obtain a page, then create/update cases before the next request | Earlier response values stay unchanged; later page reflects a valid current read view and boundary. Coordinate changes deterministically; do not assert a traversal snapshot. |
| P3-09 | Restart against a retained store and repeat the same queries; separately query independent stores | Retained IDs/order/filter membership persist; stores remain isolated. No per-host paging session is required. |
| P3-10 | Make storage unavailable after startup, or place invalid persisted values in rows loaded for the page | Generic safe `500`, with no empty/partial success, repair, fallback or misleading `404`/`409`. Invalid query input still returns `400` without opening storage. |
| P3-11 | List synthetic text containing quotes, SQL-looking strings, Unicode and embedded NULs | Returned values exactly match normalized stored values; no SQL interpretation, truncation or new metadata. Validate every case value materialized for the page. |
| P3-12 | Run existing domain, HTTP, durability/process and setup checks alongside list tests | Existing behavior and schema compatibility remain intact; documentation demonstrates browse → retrieve → change status, pagination and restart. |

## Verification and implementation sequence

1. **Reviewed in PR #13:** the scenario, query contract, defaults, ordering and consistency limits form this slice's baseline.
2. **Implemented within existing boundaries:** check the smallest implementation against the existing Api/Domain boundary and ADR 0004. Prefer a focused read operation in the concrete SQLite store, parameterized queries and the existing immutable case snapshots. A new ADR is needed only if a meaningful architecture/storage decision changes; this proposal does not justify an additional layer or generic query framework.
3. **Covered by automated tests:** test against actual disposable SQLite files and real HTTP requests. Use deliberately chosen fixture IDs whose canonical string order differs from insertion order, mixed statuses and enough cases to cross page boundaries. Test default/max limits, the exclusive boundary, exact final-page behavior, failures and restart isolation without relying on random ID order or sleeps.
4. **Existing gates retained:** keep domain tests independent of HTTP/storage and retain format verification, warnings-as-errors, Release build and the full existing test suite. Review that only page-sized results are materialized; response counts alone cannot prove this implementation property.
5. **Updated public evidence:** the local walkthrough demonstrates browse → retrieve → change status and pagination/restart behavior. Keep cloud, identity, server-database and AI capabilities clearly deferred.

The local browsing workflow is implemented without changing the existing case routes, storage schema or ADR 0004. New deployment, identity, search or integration capabilities still require separate requirements.

## Current implementation coverage

| Criteria | Evidence |
| --- | --- |
| P3-01–06 | [Pagination tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseListPaginationTests.cs): empty/default pages, exact status membership, decoded inputs, limits 1/20/100, final-page boundaries, complete traversals, uppercase/nonexistent boundaries and changed page sizes. |
| P3-07 | [Validation tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseListValidationTests.cs): strict values/names, duplicates, mixed casing and safe field errors while the database file is unavailable, proving validation precedes storage access. |
| P3-08–09 | [Consistency tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseListConsistencyTests.cs): immutable earlier snapshots, deterministic membership changes between pages, restart continuity and independent files. |
| P3-10 | [Failure tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseListFailureTests.cs): missing storage, invalid stored fields/IDs, no repair/partial success and a damaged row moving into the lookahead window. |
| P3-11–12 | Consistency tests round-trip quoted/SQL-looking text, Unicode and embedded NULs, then retrieve and progress a listed case. Existing domain, HTTP, SQLite/process-recovery checks remain in the full suite. |

[SqliteCaseStore.List](../../src/EnterpriseCaseIntelligence.Api/Cases/SqliteCaseStore.cs) applies filtering, canonical ordering and the parameterized `limit + 1` in one SELECT. Only those rows are restored/materialized before the response is constructed. The failure fixture places corruption beyond lookahead and then inside it; this supplements source review of the SQL bound, without claiming bounded scan cost. [Case endpoints](../../src/EnterpriseCaseIntelligence.Api/Cases/CaseEndpoints.cs) validate queries and project the exact two-field response.

The [walkthrough](../usage/local-case-workflow.md) gives runnable browse/pagination examples. No new layer, provider abstraction, index, schema migration or domain capability was introduced.
