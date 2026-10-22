# Phase 5 proposal: retry-safe case creation

**Status: Proposed requirements — not implemented.** Phase 4 remains the runnable scope. The current API does not deduplicate creates using a request key; repeating a create can produce another case.

## Workflow assessment and proposed outcome

The [current workflow](../usage/local-case-workflow.md) preserves case state and history after restart. Its [response-loss tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseResponseLossTests.cs) also show that a create can commit before the client receives its response. Without the generated ID, the client cannot reliably distinguish that case from another case with identical text. Retrying creates another independent case.

Propose an optional client-generated key for one logical `POST /cases` operation. A matching retry would return the original creation result without another case or history entry, including after response loss or API restart. These are local reference-project assumptions, not validated enterprise requirements.

This addresses a demonstrated failure boundary in the existing store. A new cloud deployment, broker or service would not by itself identify two requests as the same intended creation.

## Scope and compatibility

- Keep the existing request body, text normalization/limits, server-generated case ID, four-field response and `New → InReview → Closed` lifecycle.
- Make the key optional. Requests without one retain today's distinct-create behavior, including when their normalized text is identical.
- Apply the new contract only to `POST /cases`. Do not add a new route, body field, initial status, case field or client-selected case ID.
- Keep one API process per intact local SQLite store. Retain generic storage failures, commit-before-success and explicit offline format changes.
- Do not add authentication, authorization, a generic response cache, TTL cleanup, key management endpoints, background workers, messaging, distributed locks, a server database, cloud hosting or AI.
- The key is request metadata, not an actor identity, secret, permission, case ID or deduplication rule for every request with similar text. There is one key namespace for case creation in each configured store, without tenant/user scoping in this local demonstration.

## Proposed request contract

Accept an optional `Idempotency-Key` HTTP request header containing **one quoted, non-empty hyphenated UUID**, for example:

```http
POST /cases HTTP/1.1
Content-Type: application/json
Idempotency-Key: "b6ab3cd4-87cf-4bdd-ae55-103ea9978018"

{"title":"Correct the sample guide","description":"Update the fictional example section."}
```

The quotes are part of the header value. Header names follow normal case-insensitive HTTP handling. Normalize uppercase UUID hex to the same lowercase key; do not trim inside the quotes. Reject empty/all-zero IDs, unquoted values, invalid UUID syntax, escapes, lists/comma-joined values, repeated field values and field parameters. Ordinary transport whitespace outside the field value does not change the key. A missing header is different from an empty header.

This is a project-specific restricted syntax and optional-key policy. The [IETF header draft](https://www.ietf.org/archive/id/draft-ietf-httpapi-idempotency-key-header-07.html) is a design reference, not a claim of full conformance; its [Datatracker record](https://datatracker.ietf.org/doc/draft-ietf-httpapi-idempotency-key-header/) identifies it as an expired Internet-Draft rather than an RFC. The contract here defines the supported behavior, including serialization of concurrent local requests instead of a separate in-progress response.

Clients must choose a fresh key for each intended new case and retain it with the intended request until its outcome is resolved. Retrying the same logical creation uses the same key and equivalent normalized fields. Choosing a new key after losing a response can create a duplicate. This proposal does not change the requirement to correct malformed input before retrying.

### Input validation and request equality

Preserve UTF-8 `application/json` handling and strict case-sensitive body properties. Validate media type, JSON, required string fields, duplicates/unknown properties, normalized lengths and key syntax before any storage access, even when a supplied key was previously used. Unsupported media types retain `415`; invalid body/field/key input uses the existing `400 validation_failed` problem contract. Key errors use `errors.idempotencyKey`. Error order for multiple invalid inputs is not contractual beyond the existing media-type precedence.

Compare the valid normalized `title` and `description` using exact ordinal string equality. Surrounding whitespace removed by the existing normalization is insignificant. JSON property order, equivalent JSON escapes and transport formatting are insignificant. Case changes, internal whitespace and different Unicode sequences remain significant; do not add case folding or Unicode normalization.

An invalid request must not claim a key. A later valid request may use that still-unbound key, including with corrected text.

## Binding and replay behavior

| Valid request | Proposed outcome |
| --- | --- |
| No key | Existing independent create: `201`, new generated ID, one `Created` history entry and no key binding. |
| Key has no committed binding | Atomically create the case, its `Created` entry and its durable binding to the normalized request; return the ordinary `201` result after commit. |
| Key has a valid committed binding to equal normalized fields | Replay `201`, the same `Location` and the original four-field creation representation, without another case, binding or history entry. |
| Key has a valid committed binding to different normalized fields | `422 Unprocessable Content`, code `idempotency_key_reused`; leave the existing case, history and binding unchanged. |

The key does not select the generated case ID; the server retains ID generation. Different keys create independent cases even for identical text. Omitting a previously used key does not attach a new request to an earlier case. The same key in an independent store is unrelated.

### Original result versus current case state

Replay the original creation values: the original ID, normalized title/description and **status `New`**, even if that case has since reached `InReview` or `Closed`. Replaying must not roll back the actual status, rewrite history/timestamps or read the clock to record a new event. Follow the unchanged `Location` to retrieve current state or history.

The guarantee covers status, `Location`, media type and the four JSON values, not byte-identical JSON formatting or transport-generated headers such as `Date`. Do not add replay metadata to case bodies or return the key in a response. No replay-indicator header is required.

Keep enough durable information to validate request equality and reproduce the original result. Current case text is immutable; if editing is proposed later, revisit how original request/result values remain available. A storage ADR must select the representation instead of assuming an in-memory cache is sufficient.

### Errors and damaged storage

The new `422` response uses the existing `application/problem+json` shape: `type: "about:blank"`, `title: "Unprocessable Content"`, `status: 422` and `code: "idempotency_key_reused"`. This status follows [RFC 9110's definition](https://www.rfc-editor.org/rfc/rfc9110.html#section-15.5.21). Do not include a `Location`, generated ID, submitted key/text, original payload, SQL, paths or exception details in failure responses.

Before treating a committed binding as a replay or a payload mismatch, validate its stored values and associated case/history. A missing target case, invalid binding, invalid original result or broken history must return generic `500 internal_error`. Do not treat it as an unused key, return a misleading `422` or create a replacement. A bound case must have valid creation provenance; never retrofit a binding to a legacy baseline case based on matching text.

Do not persist validation, conflict or transient error responses as the key's result. A known pre-commit failure leaves no binding or partial case/history. An uncertain commit or lost response does not prove rollback; retry the same key and equivalent request to reconcile. Provider failures remain generic `500`; there is no successful fallback to memory or automatic replay of a whole uncertain transaction.

## Atomicity, concurrency and lifetime

For a new binding, key ownership, case creation and its `Created` history entry must share one transaction and commit boundary. Repeated lookup, equality checking and the decision to create/replay must be serialized with that transaction in the existing host/store boundary. Persist no independently committed pending reservation and add no background completion mechanism.

With available storage, simultaneous equal requests using one key yield one new case, one binding and one `Created` entry; both return the same creation result after the first commit. Simultaneous different requests using one key allow at most one request definition to bind it; the other returns `422`. Arrival order is not promised. Provider/transport failures can still interrupt responses; they must not break the single-binding invariant.

Bindings survive normal restart, force-killed API recovery and response loss with the same intact retained store. They do not expire or get evicted based on elapsed time, process lifetime or clock changes. No TTL, release/rebind, cleanup command or quota is added. Retained bindings consume disk alongside cases; this is not an unlimited-capacity promise.

Request-time lookup/replay must be bounded to the matching binding and its case/history, without loading the complete collection or key set into memory. This bounds materialization, without promising a database scan cost or measured response time.

Deleting/replacing/losing a store, unsupported concurrent processes, different stores and retries with new/omitted keys are outside the deduplication guarantee. This is a durable local retry contract, not a general exactly-once delivery claim or a sensitive-data permission boundary.

## Existing stores and decision gate

Phase 4 initializes/hosts version 2 and explicitly upgrades version 1. The new design must preserve those existing cases and, for version 2, every existing history value. Older cases start without bindings; never infer keys from IDs, titles or history. Upgrading version 1 retains the reviewed honest-baseline rule, without inventing earlier transitions.

A follow-on compatibility/storage ADR is required before implementation. It must select durable binding storage, the next format/version, exact validation, bounded lookup and explicit offline upgrade/inspection behavior for both supported older formats. Preserve the current no-overwrite initialization, no automatic startup migration, single-transaction upgrade/recovery and safe-refusal rules. Repeating an already-completed upgrade must not create bindings or duplicate baselines.

Normal startup must never silently add bindings or reset a store. New inspection must validate committed bindings as well as cases/history. Existing public walkthrough commands remain Phase 4 commands until the design is reviewed and implemented; this proposal selects no new schema, package, layer or migration code.

## Acceptance criteria

These are proposed obligations, without new runtime behavior or test coverage claimed today.

| ID | Given / when | Required result |
| --- | --- | --- |
| P5-01 | Create identical valid text twice without a key | Existing distinct generated IDs, `201` bodies/Locations and one `Created` entry per case; no accidental deduplication. |
| P5-02 | Create with a new valid quoted key | One case, binding and `Created` entry committed together; ordinary four-field `201` body and `Location`; server still generates the ID and the key is absent from response metadata. |
| P5-03 | Replay equal normalized fields with reordered/escaped JSON, trimmed surrounding text, uppercase key hex or differently cased header name | Same `201`, ID, normalized creation body and `Location`; no added records or timestamp changes. |
| P5-04 | Reuse a bound key with changed title/description, internal whitespace, case or a distinct Unicode sequence | Safe `422 idempotency_key_reused`; no mutation, key rebind or disclosure of the original values. |
| P5-05 | Supply invalid/repeated/list/unquoted/empty keys or invalid body/media type, including a known key while storage is unavailable | Existing `400`/`415` contracts before I/O; no key claim or replay bypass. A later valid request can use an unbound key after earlier validation failure. |
| P5-06 | Advance a keyed case to each later state, then replay creation | Original creation response still says `New`; retrieve/history show actual current state. No status rewind, history append, timestamp rewrite or new clock read. |
| P5-07 | Coordinate equal/different requests sharing one key; separately use different keys or independent stores | One committed binding per store/key; matching successes share the original result, differing requests yield one winner and `422`. Distinct keys/stores remain independent. |
| P5-08 | Fail at the case, history or binding write boundary before commit using actual SQLite controls | Generic safe failure; independent reopen proves no partial binding/case/history. Retrying the same key after known rollback creates one complete result. |
| P5-09 | Lose a successful create response; separately force-kill after acknowledged keyed creation and restart | Same retained key/request retrieves the original creation result without needing the unknown case ID and without another case/history entry. Test response loss and process termination separately. |
| P5-10 | Repeat after host replacement and controlled clock advances/reversals | Binding and original result remain unchanged; no expiration, eviction or reuse based on time. |
| P5-11 | Corrupt a binding, its result, target case or history, or make storage unavailable | Generic `500`, without replacement/rebind, false payload conflict or partial success; invalid HTTP input still fails before I/O. No production test/reset routes. |
| P5-12 | Explicitly upgrade frozen version-1/version-2 stores, including empty/all-status/multi-batch inputs, and run setup plus the complete suite | Original case values and version-2 histories retained; version-1 baselines remain honest; old cases have no invented bindings. Upgrade failures/interruption leave a wholly supported old/new format. Existing lifecycle, list, history, root/health and all quality gates remain intact locally and in Linux CI. |

## Review and implementation sequence

1. Review optional-key behavior, syntax, normalized equality, `422`, original-result replay and retained-binding lifetime. Amend assumptions before implementation if the desired capability differs.
2. Propose/review the required compatibility/storage ADR, including atomic binding ownership, supported old-format upgrades, inspection and uncertain-outcome recovery. Keep the existing production boundaries unless concrete requirements justify a change.
3. Implement the focused create extension and actual-engine coverage, retaining existing keyless contracts. Use deterministic concurrency, response-loss and process-interruption controls, disposable stores and no arbitrary sleeps or production fault switches.
4. Run restore, format verification, warnings-as-errors, Release build and the full suite; demonstrate key reuse after a lost response/restart and replay after later status changes.
5. Update the walkthrough, architecture and implemented-scope README evidence only after the feature exists. Keep the profile on implemented Phase 4 until then.

This PR proposes requirements only. It adds no runtime behavior, schema, upgrade, dependency, infrastructure or test framework.
