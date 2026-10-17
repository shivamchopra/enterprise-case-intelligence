# Run the local synthetic case workflow

Start the API using the [README commands](../../README.md#getting-started). The examples assume `http://localhost:5080` and use synthetic text only.

**Storage is temporary:** each application host starts empty. All cases disappear when that host stops or restarts. Separate instances do not share cases. There is no authentication or authorization; use this local demonstration only with synthetic data.

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
- State is held only in host memory, without expiration, durable recovery, version tokens or an enforced memory cap. Concurrent operations are serialized inside that host; arrival order is not promised.

The existing `/` and `/health` endpoints retain their foundation behavior. See the [requirements and acceptance coverage](../requirements/phase-1-case-workflow.md) and [storage ADR](../adr/0003-use-process-memory-for-the-phase-1-local-workflow.md) for the exact contract and tradeoffs.
