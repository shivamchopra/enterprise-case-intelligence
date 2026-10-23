# Phase 6 requirements: local browser case workspace

**Status: Proposed requirements — not implemented.** The [project assessment](../roadmap.md) prioritizes a complete local case journey. The runnable baseline remains the Phase 5 API with EF Core Code First; there is no browser UI today.

## Scenario and outcome

A fictional service-desk staff member should be able to find saved synthetic work, create a case, inspect its details and recorded history, and advance it from New to InReview to Closed without constructing HTTP requests. “Staff member” describes the demonstration scenario; no authenticated identity, role or ownership is implied.

Serve the workspace at `/workspace` from the existing application host. Preserve the root JSON endpoint, `/health` and all five case operations. Use those HTTP operations for data and mutations; do not add a second workflow implementation or let presentation code access the database directly. Keep the supported demonstration on loopback, with synthetic data and the existing explicit store setup. No cloud account or separate frontend server should be required to run the built application.

## Workspace behavior

| Journey | Required behavior |
| --- | --- |
| Browse | Load one default-sized page using `GET /cases`; show title and status, an explicit empty state and a Next control only when `nextAfter` is present. Never fetch every page automatically. |
| Filter / refresh | Offer All, New, InReview and Closed. Changing the filter or refreshing starts from the first page, clearing the previous continuation. Next retains the filter. Explain that ID ordering is not time/priority and Refresh finds current membership. No totals or page counts. |
| Create | Require title/description with the existing limits; submit JSON to `POST /cases`. Show validation near the fields and retain the entered draft on failure. On confirmed creation, open the returned case by ID and read its current state. |
| Review | Open a listed/created case; show its ID, title, description, current status and the separately loaded history ordered by sequence. Label baseline entries honestly as state first recorded at upgrade, not original creation. An unavailable history must not appear as “no history.” |
| Progress | Offer Start review for New and Close case for InReview; Closed has no advance action. Send only the corresponding existing status value. Show a successful state change only after an HTTP success, then reload current state/history and refresh the first list page for the selected filter. |
| Recover | Distinguish loading, empty, failure and successful states. Provide explicit retry/refresh controls. A server failure must not turn into an empty list, invented success or silent new case. |

The server remains authoritative for text normalization, lifecycle rules and failures. The UI must handle a `409` caused by a newer server state even when its displayed action was legal. Explain the conflict and allow a refresh; do not claim optimistic concurrency or a reserved case. Details and history are separate read observations and need not form a cross-request snapshot. If a mutation succeeded but the following refresh fails, report the confirmed write and the failed refresh separately.

### Creation retries

Generate one valid quoted UUID `Idempotency-Key` for a draft on its first submit. Freeze that submitted payload and retain its key in page memory while the result is uncertain. Disable duplicate submissions in flight. An explicit retry resends the same key and payload; a network failure or uncertain server outcome must not generate a replacement key. After a `201`, retrieve the returned ID for current status because replay represents the original New result.

If the user wants a different payload after an uncertain outcome, explain that the earlier request may already have created a case. Require an explicit start-new-draft action before issuing a new key. Display a safe explanation for `422 idempotency_key_reused`; never automatically replace the key. A full page reload/closure loses in-memory draft/retry state; disclose that limitation while a result is uncertain. Do not promise durable browser recovery or store case text/keys in localStorage, URLs or analytics for this slice.

### Usability and safe rendering

Use labeled native controls, keyboard navigation, visible focus, meaningful headings and accessible loading/error announcements. Preserve description line breaks and display all case text as text, including HTML-looking synthetic input. Do not inject case/error values into executable markup or expose raw server bodies, paths or stack traces. Avoid color-only status communication. The main workflow must remain usable on narrow screens and at browser zoom.

Load application assets from the same host; no third-party script, font, analytics or runtime CDN is needed. The implementation decision must cover asset publication, browser content restrictions and the test runner. Serve only intended UI assets; no directory browsing or access to store/configuration files. Keep current API contracts and access boundaries. A browser page does not add authentication, protect the existing unauthenticated API or authorize network deployment.

### Requests and stale views

Keep reads bounded to the current page and selected case/history. Do not add polling, bulk preload or cached success as a substitute for a failed request. Rapid filter/selection changes must not let older responses overwrite the latest selection; disable or discard obsolete work. Keep distinct pending/error state for list, details/history and mutations. If the current view is retained after a read failure, mark it as previously loaded and allow an explicit refresh.

## Scope limits

No new case fields, search, editing, comments, attachment, assignment, identity, bulk action, deletion, status transition or API operation is selected. No schema migration, data seeding/reset, store upgrade or new storage-format version is needed. EF Core Code First remains the approach if later features require model changes; this UI should reuse the shipped store unchanged. No AI, messaging, cloud deployment, readiness probe or additional production project is included.

## Acceptance criteria

| ID | Given / when | Required evidence |
| --- | --- | --- |
| CW-01 | Initialize a disposable store and start the built application with documented commands | `/workspace` and its local assets load; root, liveness and case contracts still pass. No separate frontend process or external account is needed. |
| CW-02 | Browse an empty store, mixed statuses and enough cases for multiple pages | Empty state, default bound, exact filters, correct Next visibility/continuation and first-page reset on filter/Refresh; no automatic full traversal or false chronological/count claims. |
| CW-03 | Create with valid text and submit invalid/boundary input | Created case is retrievable; server-normalized values appear; field errors are accessible and draft text survives errors; no premature success. |
| CW-04 | Lose the first creation response after a real commit, then explicitly retry in the same page | Same key/payload yields the same case; only one case/history/key binding exists. Current state comes from retrieval, not the replay status. A new/changed draft after uncertainty requires explicit action and warning. |
| CW-05 | Open cases with Created or upgraded Baseline history and progress both legal transitions | Details/history labels are truthful, correct actions appear, Closed has none, and confirmed changes reload the selected case/history and restart the filtered list. Restart retains the result. |
| CW-06 | Change the case externally before a displayed status action; fail reads or refreshes independently | Safe conflict/failure feedback; no invented success/empty history; confirmed writes remain distinguished from failed refreshes; explicit recovery works. |
| CW-07 | Change filter/selection rapidly with deliberately delayed responses | Latest selection wins; obsolete data never replaces it; duplicate mutations are disabled while pending. |
| CW-08 | Use keyboard only, narrow viewport and zoom; render HTML-looking/Unicode synthetic text and error markers | Controls/feedback remain accessible, text never executes, no external asset calls or raw internal diagnostics. No submitted text or creation key in page URLs or persistent browser storage; ordinary case-ID API routes remain supported. |
| CW-09 | Publish and run the application from a disposable output directory | All intended workspace assets work from published output; store/configuration files are not exposed. Document local-only limits and draft/retry lifetime. |
| CW-10 | Run repository gates plus the chosen real-browser workflow checks | Existing domain/API/SQLite/process tests and EF model/snapshot check pass. Browser evidence covers the journey, pagination, retry after lost response, stale responses, safe rendering and failure recovery. README/walkthrough describe only shipped behavior. |

## Delivery and evidence

Document the smallest same-host UI approach and reproducible browser-test setup in an ADR before runtime implementation. Choose tools for this workflow rather than scaffolding speculative layers. Use disposable stores and deterministic test coordination; keep fault controls outside production routes. Exercise real browser interactions against the real API for the core journey; HTTP/markup assertions alone do not establish that controls work.

When the workspace ships, update this document with evidence links and the README/local walkthrough with the browser entry point and a short synthetic scenario. Until then, keep implemented-scope tables and the profile on the existing API capability. This proposal changes priority and acceptance expectations only.
