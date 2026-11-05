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

Fresh `init-store` applies both checked-in EF migrations and creates format 5 with case, history, creation-key, priority and EF migration-history tables. Skip this upgrade section for a fresh version-5 store. For retained version-1/2/3/4 data, build the current code first and **stop the API and every other process using that store**. `CASE_DB_PATH` must select the same absolute path as setup; neither inspection nor upgrade creates a missing file.

Inspect before deciding whether an upgrade is needed:

```sh
dotnet run --project src/EnterpriseCaseIntelligence.Api --configuration Release --no-build -- inspect-store --CaseStorage:DatabasePath "$CASE_DB_PATH"
```

Success prints `Case store version: N.` where N is 1, 2, 3, 4 or 5. Inspection validates exact supported schema, engine integrity and every case; versions 2–5 also validate status history, versions 3–5 every binding/provenance, and versions 4/5 their supported EF metadata chain and current model/snapshot agreement. Format 5 requires a recognized priority record for every case. It does not change committed application state, but SQLite may perform journal recovery. Investigate failure; never initialize over the existing path.

For **version 1, 2, 3 or 4**, explicitly upgrade while hosting remains stopped:

```sh
dotnet run --project src/EnterpriseCaseIntelligence.Api --configuration Release --no-build -- upgrade-store --CaseStorage:DatabasePath "$CASE_DB_PATH"
dotnet run --project src/EnterpriseCaseIntelligence.Api --configuration Release --no-build -- inspect-store --CaseStorage:DatabasePath "$CASE_DB_PATH"
```

Expect `Case store upgraded.` and then `Case store version: 5.`. All four source formats upgrade directly in one transaction, preserving every ID, text and status. Versions 2/3/4 keep every history value/time; versions 3/4 keep every key binding. Version 1 receives one honest `Baseline` per old case recorded at upgrade; earlier transitions/creation times remain unknown. Other versions receive no new status baseline. Previously unbound cases stay unbound. Every old case receives Normal priority once; this is a new default, not historical priority evidence, and no priority/status event is invented.

Source schema/data are fully validated before mutation. Existing application tables remain in place; missing tables use scaffolded EF operations. Legacy sources receive the validated initial EF baseline; format 4 keeps its metadata row/version. Generated priority-table creation, bounded tracked backfill, the second metadata row, target validation and format marker share the offline transaction. No parallel schema script or automatic startup migration is used.

This is a one-way in-place upgrade. Older executables refuse format 5; there is no downgrade, automatic backup or restore command. A repeated upgrade refuses format 5 without adding records. After successful inspection of 5, start the API with the normal README command and no command mode.

If upgrade fails, is interrupted or loses completion output, stop all users and run **only inspection first**. A valid 1/2/3/4 means the upgrade did not commit and may be retried deliberately; a valid 5 means it committed and must not be repeated. Investigate any invalid/unsupported outcome. Never delete a journal, reset a file or force initialization. These commands assume intact local storage, without a hardware/power-loss guarantee. The transaction includes EF metadata and creates no persistent migration lock. Unexpected metadata/lock tables are refused without automatic deletion. See [ADR 0007](../adr/0007-adopt-ef-core-code-first.md) and its [format-5 amendment](../adr/0016-expose-case-priority-without-changing-creation-replay.md).

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
