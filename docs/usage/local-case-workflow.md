# Run the local synthetic case workflow

Start the API using the [README commands](../../README.md#getting-started). The examples assume `http://localhost:5080` and use synthetic text only.

**Storage is local and retained:** initialize a new file once, then start/restart the API with the same `CaseStorage:DatabasePath`. Acknowledged writes survive API process termination when the file and local storage remain intact. Only one API process may use a store at a time. There is no authentication or authorization; use synthetic data only.

Initialization is an explicit `init-store` command, not an HTTP route. It refuses any existing file, including an empty one. Normal startup refuses missing, incompatible, corrupt or unwritable storage and never creates a replacement. The directory must already exist and permit database/journal writes. Choose a local directory outside network shares or synchronization-managed folders; `CaseStorage__DatabasePath` is the equivalent environment setting.

## Create and retrieve

```sh
curl -i -X POST http://localhost:5080/cases \
  -H 'Content-Type: application/json' \
  -d '{"title":"Correct the sample guide","description":"Update the fictional example section."}'
```

Expect `201 Created`, a `Location: /cases/<generated-id>` header and exactly four JSON fields: `id`, `title`, `description`, `status`. Status starts at `New`. Surrounding title/description whitespace is trimmed.

Replace `CASE_ID` below with the actual ID returned by creation:

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

## Validation and limits

- Title: required, 1–120 .NET string characters after trimming. Description: required, 1–2,000.
- Requests use UTF-8 `application/json`; property names and status strings are case-sensitive.
- Unknown/duplicate properties, missing or non-string values, invalid Unicode and malformed JSON return `400 Bad Request`.
- Malformed or all-zero UUIDs return `400`; valid unknown IDs return `404`. Unsupported media types/charsets return `415`.
- Expected failures return `application/problem+json` with `type`, `title`, `status`, stable `code` and field errors where applicable. Unexpected failures return a generic `500` problem without exception details.
- State is retained in SQLite without expiration, version tokens or enforced disk quotas. Operations are serialized and use committed transactions; arrival order is not promised.
- A storage failure returns generic `500 internal_error`, not `404`, `409` or a success in memory. If the response is lost after commit, a known ID can be retrieved to reconcile state. Retrying an unacknowledged create can produce another case.

The existing `/` and `/health` endpoints retain their foundation behavior. See the [requirements and acceptance coverage](../requirements/phase-1-case-workflow.md) and [storage ADR](../adr/0004-use-sqlite-for-the-durable-local-workflow.md) for the exact contract and tradeoffs.

## Restart and recover the same case

Stop the API with Ctrl+C. In the same terminal, keep `CASE_DB_PATH` from the README setup and run **only normal startup**, without `init-store`:

```sh
dotnet run --project src/EnterpriseCaseIntelligence.Api --configuration Release --no-build -- --CaseStorage:DatabasePath "$CASE_DB_PATH" --urls http://localhost:5080
```

Retrieve the original `CASE_ID` again. Its ID, text and latest committed status are retained. The automated process-recovery test also verifies acknowledged writes after force-killing the API. This does not establish hardware/power-loss recovery, backups, encryption or multi-instance operation.

If startup fails, check the configured path, directory permissions and compatibility. Use `init-store` only for an intentionally new path. Do not delete SQLite recovery journals or reset an existing store to hide an error. Recovery after a runtime storage failure may require restarting against the same intact store.

## Deliberately discard demonstration data

Stop the API before discarding the synthetic store. Remove only the file/directory you deliberately chose for this demonstration, including any sidecars, or select a different new path and initialize it. This is destructive and is never performed automatically by application startup. Runtime `.db`, `.sqlite` and `.sqlite3` files and their recovery sidecars are ignored by Git; do not force-add them.

See [durability acceptance coverage](../requirements/phase-2-durable-case-workflow.md#current-implementation-coverage) for the automated evidence and its limits.
