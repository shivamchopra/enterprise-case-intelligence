# Phase 5 requirements: retry-safe case creation

**Status: Reviewed and implemented.** Requirements were reviewed in PR #18 and [ADR 0006](../adr/0006-bind-creation-keys-in-the-local-case-store.md) in PR #19. The API supports optional durable creation keys, now with EF-managed format 4; see [current implementation coverage](#current-implementation-coverage).

[EF Core adoption / ADR 0007](../adr/0007-adopt-ef-core-code-first.md) now provides format 4 and direct adoption of retained versions 1–3 while preserving this workflow contract. Historical format references below describe their original phase.

## Workflow assessment and outcome

Before Phase 5, the workflow preserved case state and history after restart. Its [response-loss tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseResponseLossTests.cs) also show that a create can commit before the client receives its response. Without the generated ID, the client cannot reliably distinguish that case from another case with identical text. Retrying creates another independent case.

Use an optional client-generated key for one logical `POST /cases` operation. A matching retry returns the original creation result without another case or history entry, including after response loss or API restart. These are local reference-project assumptions, not validated enterprise requirements.

This addresses a demonstrated failure boundary in the existing store. A new cloud deployment, broker or service would not by itself identify two requests as the same intended creation.

## Scope and compatibility

- Keep the existing request body, text normalization/limits, server-generated case ID, four-field response and `New → InReview → Closed` lifecycle.
- Make the key optional. Requests without one retain today's distinct-create behavior, including when their normalized text is identical.
- Apply the new contract only to `POST /cases`. Do not add a new route, body field, initial status, case field or client-selected case ID.
- Keep one API process per intact local SQLite store. Retain generic storage failures, commit-before-success and explicit offline format changes.
- Do not add authentication, authorization, a generic response cache, TTL cleanup, key management endpoints, background workers, messaging, distributed locks, a server database, cloud hosting or AI.
- The key is request metadata, not an actor identity, secret, permission, case ID or deduplication rule for every request with similar text. There is one key namespace for case creation in each configured store, without tenant/user scoping in this local demonstration.

## Request contract

Accept an optional `Idempotency-Key` HTTP request header containing **one quoted, non-empty hyphenated UUID**, for example:

```http
POST /cases HTTP/1.1
Content-Type: application/json
Idempotency-Key: "b6ab3cd4-87cf-4bdd-ae55-103ea9978018"

{"title":"Correct the sample guide","description":"Update the fictional example section."}
```

The quotes are part of the header value. Header names follow normal case-insensitive HTTP handling. Normalize uppercase UUID hex to the same lowercase key; do not trim inside the quotes. Reject empty/all-zero IDs, unquoted values, invalid UUID syntax, escapes, lists/comma-joined values, repeated field values and field parameters. Ordinary transport whitespace outside the field value does not change the key. A missing header is different from an empty header.

This is a project-specific restricted syntax and optional-key policy. The [IETF header draft](https://www.ietf.org/archive/id/draft-ietf-httpapi-idempotency-key-header-07.html) is a design reference, not a claim of full conformance; its [Datatracker record](https://datatracker.ietf.org/doc/draft-ietf-httpapi-idempotency-key-header/) identifies it as an expired Internet-Draft rather than an RFC. The contract here defines the supported behavior, including serialization of concurrent local requests instead of a separate in-progress response.

Clients must choose a fresh key for each intended new case and retain it with the intended request until its outcome is resolved. Retrying the same logical creation uses the same key and equivalent normalized fields. Choosing a new key after losing a response can create a duplicate. This feature does not change the requirement to correct malformed input before retrying.

### Input validation and request equality

Preserve UTF-8 `application/json` handling and strict case-sensitive body properties. Validate media type, JSON, required string fields, duplicates/unknown properties, normalized lengths and key syntax before any storage access, even when a supplied key was previously used. Unsupported media types retain `415`; invalid body/field/key input uses the existing `400 validation_failed` problem contract. Key errors use `errors.idempotencyKey`. Error order for multiple invalid inputs is not contractual beyond the existing media-type precedence.

Compare the valid normalized `title` and `description` using exact ordinal string equality. Surrounding whitespace removed by the existing normalization is insignificant. JSON property order, equivalent JSON escapes and transport formatting are insignificant. Case changes, internal whitespace and different Unicode sequences remain significant; do not add case folding or Unicode normalization.

An invalid request must not claim a key. A later valid request may use that still-unbound key, including with corrected text.

## Binding and replay behavior

| Valid request | Outcome |
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

Request-time validation applies to the selected binding and target; offline inspection validates all bindings. A deleted binding or external edit that changes its lookup identity cannot be distinguished from an unused key by a bounded lookup. This does not add tamper evidence or promise detection of arbitrary file edits; the retry guarantee assumes intact application-owned retained state. ADR 0006 makes this corruption boundary explicit.

Do not persist validation, conflict or transient error responses as the key's result. A known pre-commit failure leaves no binding or partial case/history. An uncertain commit or lost response does not prove rollback; retry the same key and equivalent request to reconcile. Provider failures remain generic `500`; there is no successful fallback to memory or automatic replay of a whole uncertain transaction.

## Atomicity, concurrency and lifetime

For a new binding, key ownership, case creation and its `Created` history entry must share one transaction and commit boundary. Repeated lookup, equality checking and the decision to create/replay must be serialized with that transaction in the existing host/store boundary. Persist no independently committed pending reservation and add no background completion mechanism.

With available storage, simultaneous equal requests using one key yield one new case, one binding and one `Created` entry; both return the same creation result after the first commit. Simultaneous different requests using one key allow at most one request definition to bind it; the other returns `422`. Arrival order is not promised. Provider/transport failures can still interrupt responses; they must not break the single-binding invariant.

Bindings survive normal restart, force-killed API recovery and response loss with the same intact retained store. They do not expire or get evicted based on elapsed time, process lifetime or clock changes. No TTL, release/rebind, cleanup command or quota is added. Retained bindings consume disk alongside cases; this is not an unlimited-capacity promise.

Request-time lookup/replay must be bounded to the matching binding and its case/history, without loading the complete collection or key set into memory. This bounds materialization, without promising a database scan cost or measured response time.

Deleting/replacing/losing a store, unsupported concurrent processes, different stores and retries with new/omitted keys are outside the deduplication guarantee. This is a durable local retry contract, not a general exactly-once delivery claim or a sensitive-data permission boundary.

## Existing stores and compatibility decision

Phase 4 initialized/hosted version 2 and explicitly upgraded version 1. [ADR 0006](../adr/0006-bind-creation-keys-in-the-local-case-store.md), reviewed in PR #19, selects version 3 with atomic bindings and direct offline upgrades from both source formats. Every original case value and version-2 history value is preserved. Version 1 retains the honest-baseline rule, without inventing earlier transitions. All old cases start without bindings; keys are never inferred from IDs, titles or history.

The original ADR 0006 selected format 3. The current EF implementation in ADR 0007 hosts only format 4 and never upgrades, repairs or resets during normal startup. Initialization refuses every existing file. `inspect-store` validates versions 1/2/3/4; `upgrade-store` adopts a valid 1/2/3 source into 4 in one transaction and refuses version 4. Stop all store users first. After an uncertain outcome, inspect before another mutation: valid 1/2/3 is an uncommitted source, valid 4 is committed. See the [current walkthrough](../usage/local-case-workflow.md#inspect-and-upgrade-an-older-store).

## Acceptance criteria

The reviewed obligations below are implemented; evidence is mapped after the criteria.

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

## Current implementation coverage

Tests run against disposable SQLite files, the real HTTP endpoints and real API/upgrade child processes. Domain remains unchanged. The binding table and format-3 commands are implemented within Api, without a new package, production project, background service or production fault switch.

| Criteria | Automated evidence |
| --- | --- |
| P5-01 | [Key API tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseKeyApiTests.cs): keyless and distinct-key requests remain independent, with one `Created` entry each. |
| P5-02 | Key API tests verify generated IDs, four-field `201`, Location and absence of key response metadata; [failure tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseKeyFailureTests.cs) reopen all three committed tables. |
| P5-03 | Key API tests replay reordered/escaped JSON, trimmed text, uppercase hex and case-insensitive header names without changing database bytes. |
| P5-04 | Key API tests reject changed fields, case/internal spaces and distinct Unicode/NUL suffixes with safe `422`, preserving the original binding. |
| P5-05 | Key API tests cover malformed/repeated/list/empty headers, invalid JSON/fields/limits/media, known keys while storage is offline, and corrected requests using previously unclaimed keys. [Real HTTP header tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseKeyProtocolTests.cs) cover supplied empty/whitespace-only and repeated values. |
| P5-06 | Key API tests replay at New/InReview/Closed, retaining current case/history; a replacement store with a throwing clock proves no replay or mismatch clock read. |
| P5-07 | Key API tests coordinate simultaneous equal/differing requests with a barrier, verify one winner/replay or `422`, and exercise independent stores. Failure tests verify unique-key/target and foreign-key enforcement. |
| P5-08 | Key failure tests use actual SQLite triggers with ABORT/IGNORE on each of the three writes plus a commit-lock failure; reopened connections see no partial records and the same-key retry creates one result. Constraint failures remain `500`. |
| P5-09 | [Response-loss tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseResponseLossTests.cs) discard the committed keyed-create response, restart and retry without its ID. [Process recovery](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseProcessRecoveryTests.cs) separately force-kills the API after acknowledged keyed writes and replays the original `New` result while current state stays Closed. |
| P5-10 | Key API tests replace the store/connection and control equal/backward/advanced clock values; retained keys replay without consulting the clock. |
| P5-11 | Key failure tests damage selected targets/history/provenance/IDs and duplicate a selected row; equal and differing requests fail safely before replay/mismatch, without repair. An unrelated invalid binding leaves bounded lookup usable but fails offline inspection; a 205-binding fixture verifies inspection past two batches. Key API tests cover offline storage. |
| P5-12 | [Key upgrade tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseKeyUpgradeTests.cs) use frozen versions 1/2 in UTF-8/UTF-16, empty/all-status/205-case inputs, exact version-2 history fields, no clock or inferred keys, invalid sources and commit-lock rollback for both formats. [Existing upgrade tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseStoreUpgradeTests.cs) fail after DDL/partial baselines; [upgrade process tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseUpgradeProcessTests.cs) kill a controlled in-progress upgrade and reconcile lost completion from both source formats. |

The complete suite retains the existing lifecycle, list, history, foundation and process-recovery checks. Restore, format verification, warnings-as-errors, Release build and all tests are required locally and in Linux CI. These establish the stated local process/retained-file behavior, not power-loss recovery, external tamper detection or production readiness.
