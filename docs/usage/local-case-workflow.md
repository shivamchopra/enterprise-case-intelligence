# Run the local synthetic case workflow

Start the API using the [README commands](../../README.md#getting-started). The examples select explicit `Security:Mode=LocalDemo`, assume `http://localhost:5080` and use synthetic text only.

**Storage is local and retained:** initialize a new file once, then start/restart the API with the same `CaseStorage:DatabasePath`. Acknowledged writes survive API process termination when the file and local storage remain intact. Only one API process may use a store at a time. LocalDemo has no authentication or authorization; use synthetic data only. [Oidc configuration and behavior](authenticated-case-workspace.md) are separate.

Initialization is an explicit `init-store` command, not an HTTP route. It refuses any existing file, including an empty one. Normal startup refuses missing, incompatible, corrupt or unwritable storage and never creates a replacement. The directory must already exist and permit database/journal writes. Choose a local directory outside network shares or synchronization-managed folders; `CaseStorage__DatabasePath` is the equivalent environment setting.

## Browser workspace

With the API running on loopback, open [http://localhost:5080/workspace](http://localhost:5080/workspace). Open the server URL rather than the source HTML file. A modern browser with JavaScript is required; there is no frontend server, account, Node runtime or external asset download. The API and curl workflow below remain available.

The **Workload overview** shows New, In review, Closed and Total across all saved cases, independent of list title/status/priority filters and paging. It loads after access is checked and after confirmed writes. Choose **Refresh totals** to see external changes; it does not poll. Counts and list/details/history are separate reads, so they need not describe the same instant. Unavailable totals are hidden with an explicit retry message, never replaced with zero.

1. Choose **New case**. Enter title `Correct the sample guide` and description `Update the fictional example section.`, then choose **Create case**. The workspace reads the created ID for current state and shows recorded history.
2. While the case is open, choose **High** under **Change priority** and use **Save priority**. Then choose **Start review** and **Close case**. Success is shown after the server response; current details/history reload separately. Closed cases have no advance action.
3. Enter `sample guide` under **Find cases** and choose **Search** (or Enter), then filter by **Closed**. Search matches literal title text with case sensitivity; `%`/`_` are ordinary characters. **Next page** retains title/status/priority. Search, Clear, changing status/priority filters or **Refresh list** starts at the beginning. Editing without submitting does not change the active search. **Clear search** removes title filtering and preserves status/priority. Pages are ordered by ID, not creation time or priority. No total/count or traversal snapshot is promised.
4. Copy the displayed case ID into **Open case by ID** and choose **Open case** (or Enter). Use the full hyphenated ID; uppercase letters and surrounding spaces are accepted. Details/history open even if the case is outside the current list, without changing title/status/priority filters or the current page. An ID grants no access by itself.
5. Select **Priority: High** to find that work, or **All priorities** to omit the priority filter. Current priority loads only for the selected case. Reader sees its value; For an open case, Caseworker can choose a different Low/Normal/High value under **Change priority** and choose **Save priority**. Creation starts at Normal; a failed read never invents a value. Closed cases show priority without a change action. Priority changes have no actor/time/history entries; **Recorded history** remains status history.
6. Stop/restart the API against the same intact file and revisit the workspace. Open the saved ID to see its retained Closed state/history. Cases adopted from older stores can start with **State first recorded at upgrade**; that is not their original creation time.

The browser sends a quoted UUID creation key and freezes the submitted payload while an outcome is uncertain. If creation cannot be confirmed, **Retry same draft** resends the same key/payload. A replay can represent the original New state; the workspace retrieves current state afterward. To change an uncertain draft, choose **Use a different draft**, acknowledge that the first request may already have created a case, then choose **Start new draft**. No automatic replacement key or background retry is used.

Draft text/key state lives only in the open page's memory, including when the draft dialog is closed. Closing or reloading the page loses it; the UI warns while creating/uncertain, but browser unload prompts are best effort. This is not cross-reload recovery. Do not put real information into the demonstration. Creation keys/text are not saved in browser storage, page URLs or telemetry.

Lookup rejects malformed IDs before making case requests and distinguishes a missing case from an unavailable read. URLs and creation retry keys are not case IDs. The workspace address and browser storage stay unchanged; the normal case-ID API paths can still appear in HTTP diagnostics. Session loss/sign-out clears the lookup field.

If a list, detail, priority or history read fails, use its explicit **Refresh** control. A failure is not an empty result. If a write was confirmed and its follow-up read fails, the confirmation remains visible separately. A rejected/uncertain status or priority action requires **Refresh case** to reload current status/priority before another action. **Refresh priority** can recover its read independently, but cannot enable editing while current case status is unavailable. A confirmed priority write refreshes selected views, the first filtered page and independent workload totals. Pending status/priority writes prevent duplicate or competing actions; changing selection stays available. No priority write retries automatically: a blind replay could replace another worker’s later choice. Browser requests time out after 15 seconds; this does not prove that a server-side write was canceled. Details/priority/history load independently and can reflect different observations.

This explicit LocalDemo mode has no sign-in or staff permission checks and is not safe for network hosting. Oidc mode adds sign-in, Reader/Caseworker permissions and CSRF but still requires separate hosted acceptance. Keep the supported demonstration on loopback with synthetic data. `/health` still reports host liveness only; storage readiness remains deferred.

## Inspect overall workload through HTTP

```sh
curl -sS http://localhost:5080/cases/summary
```

The response is exactly `new`, `inReview`, `closed` and `total` integer counts, with zeroes for an empty store. No query parameters are accepted. Reader/Caseworker access is required in Oidc mode; this example uses the explicit loopback LocalDemo host. Counts aggregate current recognized statuses; they do not inspect every case/history/key or prove store integrity. Use the documented offline inspector for that purpose.

## Find cases by title

Search the same case list through HTTP:

```sh
curl --get http://localhost:5080/cases \
  --data-urlencode 'title=sample guide' \
  --data-urlencode 'status=Closed' \
  --data-urlencode 'limit=1'
```

`title` is optional and supplied once; omit it to include every title. A present value is trimmed, then must contain 1–120 UTF-16 code units of valid text. Empty/whitespace-only, overlong or repeated values return `400 validation_failed` with `errors.title` before storage access. Match is a case-sensitive literal substring of title only, with significant internal spacing/Unicode forms and no wildcard, description or relevance search. For example, `guide` does not match `Guide`.

The response remains exactly `items`/`nextAfter` with the usual four-field cases. If `nextAfter` is non-null, resend the same title/status with that value as `after`. To change search/filter or refresh current membership, omit `after`. Search filters before the bounded page/lookahead, without promising a bounded database scan or traversal snapshot. It changes no stored values or format. Search text appears in the API query string and may appear in HTTP diagnostics; use synthetic text. It is not added to the workspace page address or saved in browser storage.

## Read, change and filter priority through HTTP

The browser controls above use these same priority resources. Copy a synthetic case ID returned by creation into `CASE_ID`:

```sh
CASE_ID="<case-id-from-creation>"
curl -sS "http://localhost:5080/cases/$CASE_ID/priority"
curl -i -X PUT "http://localhost:5080/cases/$CASE_ID/priority" \
  -H 'Content-Type: application/json' \
  -d '{"priority":"High"}'
curl --get http://localhost:5080/cases \
  --data-urlencode 'priority=High' \
  --data-urlencode 'title=sample guide' \
  --data-urlencode 'status=New'
```

Read/write success returns exactly `{"caseId":"<id>","priority":"High"}` (with the current value). New cases start at Normal. Values are exactly Low, Normal or High; numbers, aliases, whitespace, different casing, extra/duplicate fields and query parameters on the priority resource are rejected. In Oidc mode, Reader/Caseworker may read and filter; only Caseworker may write, with the session CSRF token even for a no-op. These curl examples use explicit loopback LocalDemo.

An actual change on Closed returns `409 case_closed`; its current value is a validated `200` no-op. Priority does not change status, text, history or creation-key ownership. Existing creation/list/detail/status/replay responses stay four-field; supplying priority on creation is rejected. Original keyed replay never resets current priority. Status history contains no priority changes, actor or priority timestamp.

Omit `priority` for all priorities; otherwise supply it exactly once. Combine title/status/priority before the bounded page and keep all filters with `after`. Pages still sort by canonical ID, not priority. Summary counts remain status-only and independent of filters. A filtered list validates encountered rows/lookahead, not excluded rows; use the offline inspector for whole-store validation.

After a lost/uncertain priority response, read current case and priority before choosing another explicit write. There is no priority retry key or automatic replay: a blind retry could overwrite another worker's later choice. Read failures do not prove a write failed. Missing cases return the normal `404`; unavailable/damaged retained state returns safe `500`, without inventing Normal or repairing records.

## Inspect and upgrade an older store

Fresh `init-store` applies the checked-in EF migrations and creates format 6 with an empty closure stream. For retained formats 1–5, build the current code and **stop the API and every other process using that store**. Set `CASE_DB_PATH` to the same absolute path used for setup.

Inspect the retained file before deciding whether to upgrade.

```sh
dotnet run --project src/EnterpriseCaseIntelligence.Api --configuration Release --no-build -- inspect-store --CaseStorage:DatabasePath "$CASE_DB_PATH"
```

Success prints `Case store version: N.` for supported formats 1–6. Inspection checks the exact schema, SQLite integrity and all retained application state for the detected format. SQLite may recover an interrupted journal during inspection. Investigate any inspection failure before proceeding.

For **formats 1–5**, run the explicit upgrade and inspect again while hosting remains stopped.

```sh
dotnet run --project src/EnterpriseCaseIntelligence.Api --configuration Release --no-build -- upgrade-store --CaseStorage:DatabasePath "$CASE_DB_PATH"
dotnet run --project src/EnterpriseCaseIntelligence.Api --configuration Release --no-build -- inspect-store --CaseStorage:DatabasePath "$CASE_DB_PATH"
```

Expect `Case store upgraded.` and then `Case store version: 6.`. Each source upgrades directly in one transaction. Format 1 receives a `Baseline` history row per case. Formats 2–5 keep exact histories and formats 3–5 retain creation-key bindings. Formats 1–4 receive the Normal priority default. Format 5 keeps its existing priorities. The closure stream begins empty, including when the store already contains Closed cases. A later closure on a retained open case appends a notice.

The upgrade is one-way. After successful inspection of format 6, start the API with the normal README command. After a failure, interruption or lost completion message, stop every store user and **inspect first**. Valid format 1–5 means you can deliberately retry the upgrade. Valid format 6 confirms commit. Investigate other outcomes while preserving the file and its journal. [EF adoption](../adr/0007-adopt-ef-core-code-first.md) · [Priority migration](../adr/0016-expose-case-priority-without-changing-creation-replay.md) · [Closure migration](../adr/0017-use-a-transactional-closure-feed.md)

## Read the local closure feed

Run the API in LocalDemo against a fresh disposable store and request the feed. The initial page has an empty `items` array, a stream ID and `nextAfter` of `"0"`.

```sh
curl --fail-with-body http://localhost:5080/integrations/case-closures
```

Create a synthetic case using the commands below, advance it to InReview and close it. Read the feed with a one-item page and retain its `streamId` and `nextAfter` values.

```sh
curl --fail-with-body 'http://localhost:5080/integrations/case-closures?limit=1'
```

Compare the notice's history sequence and timestamp with the case history. Repeat the Closed request and confirm the page still shows one notice. Set `STREAM_ID` and `AFTER` to the returned values, restart the API with the same file and continue after that notice.

```sh
curl --fail-with-body "http://localhost:5080/integrations/case-closures?streamId=${STREAM_ID}&after=${AFTER}"
```

An empty continuation keeps the same cursor. After a different case closes, the same continuation returns the new notice. Preserve the stream ID with the cursor when saving a checkpoint. A replacement file returns `409 handoff_source_changed` for the old stream, while an unknown positive position on the same stream returns `409 handoff_checkpoint_unknown`. For a lost close response, read the retained case/history or repeat the Closed request, then check the feed. [Phase 10 pull contract](../requirements/phase-10-case-closure-handoff.md#pull-contract) · [Run the records intake](local-records-intake.md)

## Create a case

```sh
curl -i -X POST http://localhost:5080/cases \
  -H 'Content-Type: application/json' \
  -d '{"title":"Correct the sample guide","description":"Update the fictional example section."}'
```

Expect `201 Created`, a `Location: /cases/<generated-id>` header and exactly four JSON fields: `id`, `title`, `description`, `status`. Status starts at `New`. Surrounding title/description whitespace is trimmed.

## Retry a creation with a retained key

Choose a fresh UUID for each intended new case and retain it with the request until the outcome is resolved. This synthetic example uses a fixed key so the command can be repeated:

```sh
curl -i -X POST http://localhost:5080/cases \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: "b6ab3cd4-87cf-4bdd-ae55-103ea9978018"' \
  -d '{"title":"Correct the sample guide","description":"Update the fictional example section."}'
```

The double quotes around the UUID are required header characters. Repeat this command after a lost response or restart to get `201` with the same `Location` and four original creation values, including status `New`. Following that location shows the current status, which may now be `InReview` or `Closed`. Replay never rewinds status, appends history or rewrites timestamps. Try the same command again after progressing the keyed case to see the difference.

Equivalent JSON escapes/property order, surrounding trimmed text and uppercase UUID hex replay the same result. Changed normalized title/description returns `422 Unprocessable Content`, code `idempotency_key_reused`, without original values or a `Location`. Text comparison is ordinal; case, internal spaces and distinct Unicode sequences matter. Bad key/body input returns `400` before accessing storage; unsupported media types retain `415`, including for known keys.

Omitting the header always creates independently. Different keys with identical text also create independently. After response loss, keep the same key/request: choosing a new key can create a duplicate. Bindings remain for the intact store’s lifetime, without expiry, release or cleanup. One store has one namespace; keys provide no identity or authorization. A selected damaged binding/target/history/priority returns safe `500` rather than replay, mismatch or replacement. Lookup validates only the selected target; offline inspection validates the whole store. Arbitrary deletion/rekeying outside the application is not detectable as a previous use.

## Browse, retrieve and resume saved work

After creating cases, or reopening an existing store, find saved work without remembering IDs:

```sh
curl -i 'http://localhost:5080/cases?status=New&limit=1'
```

Expect `200 OK` with exactly `items` and `nextAfter`. Each item has the same four fields as a retrieved case. If more matches existed when this page was read, `nextAfter` is the last returned ID; otherwise it is `null`. Empty stores/filters return `{"items":[],"nextAfter":null}`.

For another page, replace `AFTER_ID` with the actual non-null `nextAfter` value, keeping the same filter:

```sh
curl -i 'http://localhost:5080/cases?status=New&limit=1&after=AFTER_ID'
```

Omit `status` to include all three statuses. The default limit is 20 and the maximum is 100. Cases are ordered by canonical ID, not creation time or priority. `after` is an exclusive comparison boundary and need not identify an existing case; a valid boundary with no later matches returns an empty `200` page. Start again without `after` to browse from the beginning.

Each page reflects one read view; later requests can observe changed membership. A status change or newly created case can affect later pages, and a previously returned continuation may lead to an empty page. No multi-request snapshot or reservation is provided.

Choose an ID from `items` (or the original create response) and replace `CASE_ID` below:

```sh
curl -i http://localhost:5080/cases/CASE_ID
```

Expect `200 OK` and the same case.

## Progress the case

```sh
curl -i -X PUT http://localhost:5080/cases/CASE_ID/status \
  -H 'Content-Type: application/json' \
  -d '{"status":"InReview"}'

curl -i -X PUT http://localhost:5080/cases/CASE_ID/status \
  -H 'Content-Type: application/json' \
  -d '{"status":"Closed"}'
```

Each successful request returns `200 OK` and the complete case. Immediately repeating its current status also succeeds without changing the case. `Closed` is terminal; skipping review or moving backward returns `409 Conflict` and leaves the case unchanged.

## Inspect recorded status history

```sh
curl -i http://localhost:5080/cases/CASE_ID/history
```

Expect `200 OK` with exactly `items`, containing one to three entries ordered by case-local `sequence`. Each entry has exactly `sequence`, `kind`, `fromStatus`, `toStatus` and `recordedAtUtc`.

A fresh case starts with `Created`, sequence 1, `fromStatus: null`, `toStatus: "New"`. Following the two changes above adds `StatusChanged` entries for `New → InReview` and `InReview → Closed`. Repeating the current status adds no entry and preserves recorded times. Creation/status updates and their history commit together; failures cannot leave one committed without the other.

Upgraded cases instead start with `Baseline` at their retained status; they can have fewer entries even when closed. Earlier history is unknown. Timestamps use UTC with seven fractional digits, such as `2026-10-20T12:00:00.0000000Z`. They record application time, not transaction commit time; the clock may repeat or move backward, so display by sequence rather than timestamp.

The route accepts a non-empty hyphenated UUID and no query parameters. Invalid IDs/queries return safe `400` problems before storage access; valid unknown cases return `404`. Missing/invalid history for an existing case or unavailable storage returns generic `500`, without a partial list, invented baseline or repair. History reads do not reserve a case or provide stale-client protection.

This is workflow history, without actor identity, access control or tamper evidence. It is not a compliance audit trail. The three-entry bound follows the current lifecycle and would require review before adding states or reopening.

## Validation and limits

- Title: required, 1–120 .NET string characters after trimming. Description: required, 1–2,000.
- Writes use UTF-8 `application/json`; property names and status strings are case-sensitive.
- Listing accepts only `status`, `limit` and `after`, each once. Empty/unknown/duplicate parameters, incorrectly cased names/status values and limits with padding, signs or values outside 1–100 return `400` with `validation_failed`.
- Unknown/duplicate properties, missing or non-string values, invalid Unicode and malformed JSON return `400 Bad Request`.
- Malformed or all-zero UUIDs return `400`; valid unknown IDs on single-case routes return `404`. A valid list boundary is not a lookup and never produces `404`. Unsupported write media types/charsets return `415`.
- Expected failures return `application/problem+json` with `type`, `title`, `status`, stable `code` and field errors where applicable. Unexpected failures return a generic `500` problem without exception details.
- State is retained in SQLite without expiration, version tokens or enforced disk quotas. Operations are serialized and use committed transactions; arrival order is not promised.
- A storage failure returns generic `500 internal_error`, not `404`, `409`, `422` or a success in memory. If the response is lost after commit, a known ID can be retrieved to reconcile state. Retrying an unacknowledged keyless create can produce another case; retain and reuse an optional creation key to reconcile without the generated ID.

The existing `/` and `/health` endpoints retain their foundation behavior. See the [requirements and acceptance coverage](../requirements/phase-1-case-workflow.md) and [storage ADR](../adr/0004-use-sqlite-for-the-durable-local-workflow.md) for the exact contract and tradeoffs.

## Restart and recover the same case

Stop the API with Ctrl+C. In the same terminal, keep `CASE_DB_PATH` from the README setup and run **only normal startup**, without `init-store`:

```sh
dotnet run --project src/EnterpriseCaseIntelligence.Api --configuration Release --no-build -- --CaseStorage:DatabasePath "$CASE_DB_PATH" --Security:Mode LocalDemo --urls http://localhost:5080
```

List saved cases again, retrieve the original `CASE_ID` or read its `/history`. Plain ID continuations can also be reused against the same retained store after restart. Its ID, text, latest committed status and recorded history are retained. The automated process-recovery test also verifies acknowledged paired writes after force-killing the API. This does not establish hardware/power-loss recovery, backups, encryption or multi-instance operation.

If startup fails, check the configured path, directory permissions and compatibility. Use `init-store` only for an intentionally new path. Do not delete SQLite recovery journals or reset an existing store to hide an error. Recovery after a runtime storage failure may require restarting against the same intact store.

## Deliberately discard demonstration data

Stop the API before discarding the synthetic store. Remove only the file/directory you deliberately chose for this demonstration, including any sidecars, or select a different new path and initialize it. This is destructive and is never performed automatically by application startup. Runtime `.db`, `.sqlite` and `.sqlite3` files and their recovery sidecars are ignored by Git; do not force-add them.

See [durability acceptance coverage](../requirements/phase-2-durable-case-workflow.md#current-implementation-coverage) for the automated evidence and its limits.

See [case browsing acceptance coverage](../requirements/phase-3-case-list.md#current-implementation-coverage) for ordering, page/failure boundaries and changes between requests.

See [history and upgrade acceptance coverage](../requirements/phase-4-status-history.md#current-implementation-coverage) and [ADR 0005](../adr/0005-add-status-history-with-an-explicit-store-upgrade.md) for the schema, transaction and compatibility decisions.

See [retry-safe creation coverage](../requirements/phase-5-retry-safe-creation.md#current-implementation-coverage) and [ADR 0006](../adr/0006-bind-creation-keys-in-the-local-case-store.md) for atomic binding, replay and direct-upgrade decisions.
