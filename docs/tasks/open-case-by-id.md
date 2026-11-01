# Open a known case by ID in the workspace

**Status: implemented and verified locally.** This is a focused product follow-up to the [browser workspace](../requirements/phase-6-case-workspace.md) and [title search](../requirements/phase-7-case-title-search.md). Free-host eligibility remains unresolved, so this slice improves the runnable local journey without selecting a cloud platform or changing persistence.

## Scenario and behavior

A service-desk Reader or Caseworker has a synthetic case ID from an earlier review or HTTP response. They should be able to open its current details/history directly without finding its title or traversing list pages. Possession of the ID grants no permission; the same Reader/Caseworker read policies apply.

Add **Open case by ID** to the details panel. Submit with **Open case** or Enter. Accept a non-empty full hexadecimal `8-4-4-4-12` hyphenated UUID, normalize letter case and surrounding whitespace, and reject other text before requesting case data. Reject the all-zero ID locally, matching the existing API validation. A syntactically valid non-empty unknown ID goes through the normal existing not-found contract. Do not accept URLs, paths, braces, unhyphenated IDs, query strings or fragments as shortcuts.

Reuse `GET /cases/{id}` and `GET /cases/{id}/history` for the selected ID. Details and history remain independent observations with separate failure/retry feedback. One explicit valid submission makes these two bounded reads; it does not search, scan, preload or refresh the list. Preserve the submitted title filter, status, current page and continuation. Opening a case outside that list is valid and does not imply it matches the filter. List selection and confirmed creation keep the lookup field synchronized with the selected ID.

Invalid input marks/focuses the field with a fixed accessible explanation and leaves the previous selection intact. A valid submission hides the earlier case while loading. Missing or unavailable data must not appear as a successful case or empty history; a returned case with a different ID is an invalid response. Keep explicit Refresh controls, latest-selection generation checks and generic messages that never render raw server/proxy error bodies.

Keep lookup state in the open page only. Do not put it in the workspace address, persistent browser storage or a new logging/analytics channel. The existing case-ID API paths necessarily contain the requested ID and may appear in HTTP diagnostics. Lookup IDs are separate from creation idempotency keys. Opening another case must not replace a frozen uncertain creation payload/key, trigger a write/retry or navigate away from the page. Existing explicit resume/retry behavior remains.

On session denial, expiry, access refresh or sign-out, clear the lookup input and validation together with other protected views. Ignore earlier-session responses. While read access is unavailable, the hidden form cannot issue requests even if submitted programmatically. LocalDemo remains loopback-only and synthetic; this feature does not establish hosted acceptance.

## Acceptance and evidence

| ID | Required evidence | Coverage |
| --- | --- | --- |
| CL-01 | Open a known case outside the current filtered page with exactly its detail/history reads; keep filters, page and Next continuation | `KnownCaseOutsideTheFilteredPageOpensWithoutPagingOrChangingTheList` in [lookup browser tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Workspace/CaseLookupBrowserTests.cs) |
| CL-02 | Invalid, URL/path, HTML-looking and malformed inputs issue no case requests; keyboard validation/recovery works; no page URL or browser-storage persistence; narrow/zoomed layout fits | `InvalidInputNeverMakesCaseRequestsAndCanRecoverWithKeyboardOnly` in the same tests; desktop/mobile screenshots supplement automated checks |
| CL-03 | Unknown ID, unavailable server and mismatched returned ID never invent success; explicit refresh recovers with real retained state | `MissingUnavailableAndMismatchedCaseResponsesDoNotInventSuccessAndCanRecover` |
| CL-04 | Delayed earlier detail/history successes or failures cannot overwrite a newer list selection or its lookup field | Both `LateLookupSuccessOrFailureCannotOverrideANewerListSelection` cases |
| CL-05 | A lost committed creation remains one frozen draft/key through lookup; only explicit retry reconciles it | `LookupPreservesAnUncertainDraftUntilItsExplicitSameKeyRetry` checks the real store and retained binding |
| CL-06 | Denied reads clear the new protected fields and prevent hidden resubmission; Reader lookup works without mutation controls; expiry/sign-out clears the input | Both `DeniedLookupClearsProtectedFieldsAndCannotBeResubmittedWithoutAccess` cases plus [authenticated browser tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Security/AuthenticatedBrowserTests.cs), including direct/proxied HTTPS and both certificate sources |

The existing [ADR 0008](../adr/0008-serve-a-local-browser-case-workspace.md) covers the same-host embedded presentation boundary. No API operation, business field, lifecycle rule, schema/format version, migration, EF query, dependency or production project is added. Existing EF Core Code First and retained-data guarantees remain applicable; no database change is needed for this read-only UI slice.

Validation: 703 tests passed (42 Domain, 625 non-browser API/storage/process and 36 real-browser tests), including all browser journeys against a fresh published API. Restore/tool restore, format verification, Release builds, offline probe-envelope checks and EF model/snapshot agreement passed. Desktop and narrow/200%-text screenshots were inspected. Cloud eligibility and hosted acceptance remain separate.
