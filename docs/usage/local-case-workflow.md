# Run the local synthetic case workflow

Start the API using the [README commands](../../README.md#getting-started). The examples assume `http://localhost:5080` and use synthetic text only.

**Storage is local and retained:** initialize a new file once, then start/restart the API with the same `CaseStorage:DatabasePath`. Acknowledged writes survive API process termination when the file and local storage remain intact. Only one API process may use a store at a time. There is no authentication or authorization; use synthetic data only.

Initialization is an explicit `init-store` command, not an HTTP route. It refuses any existing file, including an empty one. Normal startup refuses missing, incompatible, corrupt or unwritable storage and never creates a replacement. The directory must already exist and permit database/journal writes. Choose a local directory outside network shares or synchronization-managed folders; `CaseStorage__DatabasePath` is the equivalent environment setting.

## Inspect and upgrade a version-1 store

Fresh `init-store` now creates version 2 with case and history tables. Skip this upgrade section for a fresh version-2 store. For retained Phase 3 data, build the current code first and **stop the API and every other process using that store**. `CASE_DB_PATH` must be the same absolute path selected during setup; neither inspection nor upgrade creates a missing file.

Inspect before deciding whether an upgrade is needed:

```sh
dotnet run --project src/EnterpriseCaseIntelligence.Api --configuration Release --no-build -- inspect-store --CaseStorage:DatabasePath "$CASE_DB_PATH"
```

Success prints only `Case store version: 1.` or `Case store version: 2.` as the command result. The command validates schema, engine integrity and case values; version 2 also validates all history chains. It does not change committed application state, but SQLite may perform normal journal recovery. An inspection failure needs investigation, not initialization over the existing path.

For **version 1 only**, explicitly upgrade while hosting remains stopped:

```sh
dotnet run --project src/EnterpriseCaseIntelligence.Api --configuration Release --no-build -- upgrade-store --CaseStorage:DatabasePath "$CASE_DB_PATH"
dotnet run --project src/EnterpriseCaseIntelligence.Api --configuration Release --no-build -- inspect-store --CaseStorage:DatabasePath "$CASE_DB_PATH"
```

Expect `Case store upgraded.` and then `Case store version: 2.`. The upgrade preserves every case's ID, text and current status and records one `Baseline` entry per old case. A baseline's time is the time it was recorded during upgrade; earlier transitions and creation times remain unknown. No historical `Created` or transition entries are invented.

This changes the store format in place and is one-way. Older executables refuse version 2; there is no downgrade, automatic backup or restore command. A second upgrade refuses version 2 without adding baselines. Normal startup never upgrades automatically and refuses old-format stores. After successful inspection of version 2, start the API using the normal final README command, without a command mode.

If upgrade fails, is interrupted or loses its completion output, the outcome is uncertain. With all processes stopped, run **only inspection first**. A valid version 1 means the upgrade did not commit and may be retried deliberately; a valid version 2 means it committed and must not be repeated. Any unsupported/invalid result requires investigation. Never delete a journal, reset the file or force initialization to hide an error. These commands assume intact local storage, without a hardware/power-loss guarantee.

## Create a case

```sh
curl -i -X POST http://localhost:5080/cases \
  -H 'Content-Type: application/json' \
  -d '{"title":"Correct the sample guide","description":"Update the fictional example section."}'
```

Expect `201 Created`, a `Location: /cases/<generated-id>` header and exactly four JSON fields: `id`, `title`, `description`, `status`. Status starts at `New`. Surrounding title/description whitespace is trimmed.

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
- A storage failure returns generic `500 internal_error`, not `404`, `409` or a success in memory. If the response is lost after commit, a known ID can be retrieved to reconcile state. Retrying an unacknowledged create can produce another case.

The existing `/` and `/health` endpoints retain their foundation behavior. See the [requirements and acceptance coverage](../requirements/phase-1-case-workflow.md) and [storage ADR](../adr/0004-use-sqlite-for-the-durable-local-workflow.md) for the exact contract and tradeoffs.

## Restart and recover the same case

Stop the API with Ctrl+C. In the same terminal, keep `CASE_DB_PATH` from the README setup and run **only normal startup**, without `init-store`:

```sh
dotnet run --project src/EnterpriseCaseIntelligence.Api --configuration Release --no-build -- --CaseStorage:DatabasePath "$CASE_DB_PATH" --urls http://localhost:5080
```

List saved cases again, retrieve the original `CASE_ID` or read its `/history`. Plain ID continuations can also be reused against the same retained store after restart. Its ID, text, latest committed status and recorded history are retained. The automated process-recovery test also verifies acknowledged paired writes after force-killing the API. This does not establish hardware/power-loss recovery, backups, encryption or multi-instance operation.

If startup fails, check the configured path, directory permissions and compatibility. Use `init-store` only for an intentionally new path. Do not delete SQLite recovery journals or reset an existing store to hide an error. Recovery after a runtime storage failure may require restarting against the same intact store.

## Deliberately discard demonstration data

Stop the API before discarding the synthetic store. Remove only the file/directory you deliberately chose for this demonstration, including any sidecars, or select a different new path and initialize it. This is destructive and is never performed automatically by application startup. Runtime `.db`, `.sqlite` and `.sqlite3` files and their recovery sidecars are ignored by Git; do not force-add them.

See [durability acceptance coverage](../requirements/phase-2-durable-case-workflow.md#current-implementation-coverage) for the automated evidence and its limits.

See [case browsing acceptance coverage](../requirements/phase-3-case-list.md#current-implementation-coverage) for ordering, page/failure boundaries and changes between requests.

See [history and upgrade acceptance coverage](../requirements/phase-4-status-history.md#current-implementation-coverage) and [ADR 0005](../adr/0005-add-status-history-with-an-explicit-store-upgrade.md) for the schema, transaction and compatibility decisions.
