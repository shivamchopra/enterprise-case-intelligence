# Phase 7: find cases by title

**Status: Implemented local title-search workflow.** A fictional service-desk staff member needs to find a known synthetic case without paging through every saved title. This extends the local browser/API journey; it is a reference-project assumption, not validated enterprise research.

## API contract

Extend `GET /cases` with optional, case-sensitive query name `title`, supplied at most once. After normal query decoding, trim surrounding .NET whitespace and require 1–120 UTF-16 code units of well-formed text. Omission retains the previous all-title behavior. A present empty/whitespace-only/overlong value or duplicate returns `400 validation_failed` with `errors.title`, before storage I/O. Other unknown/duplicate/invalid query rules remain in force. Error responses never echo search text.

Match a **literal, case-sensitive substring of title only**. `guide` matches `Sample guide` but not `Sample Guide`. Internal whitespace, punctuation, accents, Unicode normalization forms and embedded NULs are significant. `%`, `_`, backslash and quotes are literal characters, not patterns or SQL. No case folding, accent folding, tokenization, relevance ranking, description search or regex is selected. This explicit first slice avoids implying a broader language-aware search contract.

Combine title and status predicates and the exclusive canonical-ID boundary before ordering/taking the existing page. Keep default 20/max 100, `limit + 1` lookahead, exact four-field cases, `items`/`nextAfter`, no totals and no chronological/priority order. Continue with the same title/status; changing either starts a new traversal without `after`. Boundaries remain plain comparison IDs, not retained search sessions. Changes between requests can change membership; Refresh starts again, without a traversal snapshot guarantee.

## Browser behavior

Add a labeled **Find cases** field with a visible title/case-sensitive explanation, a **Search** submit action and **Clear search**. Submit (including Enter) starts at the first page. An empty field omits the filter; a present whitespace-only value receives normal validation. Clear empties/removes the title filter, restarts paging and preserves the status filter. Next, status changes, Refresh and post-mutation list refreshes retain the last submitted title text. Editing alone sends no requests and does not change that active search; explain that distinction beside the field.

Keep existing generation checks: an older list success or failure cannot overwrite a newer search/clear/filter result. Invalid input and unavailable storage get accessible, distinct failure states, never an empty-result fallback. Preserve input for correction/retry. Render no raw server errors or executable search text. The browser address stays `/workspace` (an accessibility fragment is allowed), with no persistent browser storage or polling. Search text is necessarily sent in the API query string and can appear in HTTP diagnostics; use synthetic text only. The existing creation-key/draft and status workflows retain their behavior.

## Implementation decision and limits

Use the existing concrete store and EF Core LINQ `string.Contains`, translated by the SQLite provider to `instr(...) > 0`, with a parameterized value. Apply it before `Take(limit + 1)` and restore/validate every materialized row, including lookahead. Do not load/filter the whole collection in memory. This bounds materialization, not SQLite scan work or response latency; no substring index or large-dataset performance claim is made. There is no new table, migration, model snapshot, package, layer or service.

Keep read-only behavior, missing-file refusal, safe failures and retained-format compatibility. Malformed matching/lookahead rows fail the operation; rows excluded by filters are outside that page's validation. Deep offline inspection still validates all data.

This is an amendment to [Phase 3 browsing](phase-3-case-list.md) and [Phase 6 workspace](phase-6-case-workspace.md). No architecture boundary changes, so a new ADR is unnecessary. [Microsoft's SQLite mapping](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/functions) documents Contains translation; [SQLite's core functions](https://www.sqlite.org/lang_corefunc.html) define instr, and its [NUL guidance](https://www.sqlite.org/nulinstr.html) describes embedded text limitations that require provider tests.

## Acceptance and evidence

| ID | Scenario | Required result |
| --- | --- | --- |
| TS-01 | Omit title; search literal case variants, punctuation, Unicode, internal whitespace and NULs | Previous behavior when omitted; exact title-only literal membership, including encoded query names/values, with no wildcard interpretation or normalization/case folding. |
| TS-02 | Combine matches/nonmatches, statuses, valid/nonexistent/uppercase boundaries and limits | Filter before paging, canonical order, correct lookahead/last continuation, no gaps/duplicates for unchanged data and fixed predicates. |
| TS-03 | Send empty/blank/overlong/repeated title or invalid other parameters while storage is missing | Safe `400` with appropriate fields, no file creation/I/O or echoed search text. Boundary lengths succeed; valid requests against missing storage still fail safely. |
| TS-04 | Place corruption outside and then inside matching lookahead | Only bounded rows materialized; matching corruption fails without partial success or repair. Read-only values/file bytes/format remain intact. |
| TS-05 | Change matches between pages, restart hosting and query separate files | Current read views and retained continuation semantics; no hidden search session, mutation or store leakage. |
| TS-06 | Browser search/Enter, edit without submit, Next, status/filter changes, Refresh and Clear | Explicit submitted criteria, correct first-page resets/retention, bounded requests, accessible validation and safe failure/retry. |
| TS-07 | Delay an old search success/failure while applying another search or clearing | Latest view wins, with no stale list, continuation or error overriding it. |
| TS-08 | Search safe synthetic HTML-looking/Unicode text with keyboard/narrow viewport and mutate a matching case | No execution/external assets/storage/page-URL leakage; real workflow still works and list refresh retains submitted title. |
| TS-09 | Run backend/browser/publication and EF gates | Existing 434 tests plus new coverage pass, no model drift, and README/walkthrough/architecture/roadmap describe shipped behavior accurately. |

## Current implementation coverage

| Criteria | Evidence |
| --- | --- |
| TS-01–02 | [API/provider tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseTitleSearchTests.cs) exercise literal title-only membership, case/Unicode forms, punctuation/NUL, decoding/trim, UTF-16 limits, status/boundary composition, canonical traversals and page bounds against actual SQLite. |
| TS-03–04 | API tests move storage before invalid requests, verify generic valid-request failures without file creation, and place damaged nonmatches/beyond-lookahead rows outside the successful first page. Moving matching corruption into lookahead fails the entire page without mutation; deep inspection still finds excluded damage. Public store arguments validate before opening a missing path. |
| TS-05 | API tests change membership between pages, reopen a retained file with a fresh host and compare an independent file. [Browser tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Workspace/CaseTitleSearchBrowserTests.cs) additionally restart the real child process and search retained cases again. |
| TS-06–08 | Browser tests cover explicit submit/Enter, no requests while editing, retained submitted search, Next/filter/Refresh/Clear, validation vs missing-store/no-match states, delayed old successes/failures, safe Unicode/HTML-looking input, keyboard/text enlargement and mutation-triggered refreshes. |
| TS-09 | [CI](../../.github/workflows/ci.yml) retains backend/EF gates and runs all browser scenarios against published output. [Walkthrough](../usage/local-case-workflow.md#find-cases-by-title) shows browser and curl usage. Existing Phase 1–6 coverage remains in the suite. |

[Case endpoints](../../src/EnterpriseCaseIntelligence.Api/Cases/CaseEndpoints.cs) and the [title validator](../../src/EnterpriseCaseIntelligence.Api/Cases/CaseTitleFilter.cs) validate decoded text before store access. [SqliteCaseStore.List](../../src/EnterpriseCaseIntelligence.Api/Cases/SqliteCaseStore.cs) keeps title/status/boundary predicates inside the translated EF query, before ordering and `Take(limit + 1)`. No schema or model change was required.
