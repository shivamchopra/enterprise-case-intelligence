# 0008: Serve a local browser case workspace

## Status

Accepted for the local Phase 6 workspace.

## Context

The existing API implements a complete small case lifecycle, but demonstrating it requires curl. The [reviewed workspace requirements](../requirements/phase-6-case-workspace.md) add browser interaction without new domain rules, deployment services or persistence changes.

## Decision

Serve a small HTML document at `/workspace` and two same-origin CSS/JavaScript assets through explicitly mapped endpoints in Api. Embed the three assets in the application assembly, so build/publish and working-directory changes cannot omit them. Do not enable arbitrary filesystem/static-directory serving or an SPA fallback. Preserve `/`, `/health` and existing API contracts.

Use native HTML controls and plain JavaScript with fetch against the existing case operations. Each list/detail/history read has its own generation number; obsolete responses are discarded. Mutations remain pending until HTTP completion and are never optimistic. Creation freezes its payload and quoted UUID key for explicit same-page retries; uncertain requests require an explicit acknowledgment before a replacement draft. Draft/key state lives only in memory, with an unload warning while uncertain.

Apply a restrictive Content-Security-Policy to the workspace assets: same-origin scripts/styles/connections, no inline execution, frames, forms or external assets. Use textContent and DOM construction for case text, fixed client explanations for errors, no persistent browser storage and no telemetry. This is a loopback synthetic demonstration, with no identity/access-control boundary added by the UI.

Use the Microsoft.Playwright .NET library in the existing xUnit API test project. Real Chromium drives the actual loopback API child process and disposable SQLite stores. Test-side network routing can withhold/drop a response after the real server commits or delay a read; production receives no fault switches. A separate browser-test CI job installs the pinned library's Chromium and tests a temporary published API from unrelated test output. Core .NET gates can run with `Category!=Browser` when a browser is unavailable; the full gate includes both categories.

## Consequences and alternatives

There is still one production application, no production Node dependency, new package, domain field or migration. Small, explicit client state requires careful browser coverage; revisit a component framework when actual UI complexity justifies it. A separate SPA/toolchain or Blazor application would add machinery beyond the current journey. Server-side form handlers would duplicate the existing HTTP workflow and complicate uncertain creation retries.

Only the mapped resources are public; publishing never makes the store/configuration directory a static root. Browser tests add a development/CI Chromium download and require execution outside restricted environments that prevent loopback/browser subprocesses. Chromium coverage does not claim a full cross-browser or automated accessibility audit. Native keyboard controls, narrow-screen layout, zoom and a visual review supplement interaction tests.

References: [Minimal API responses](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/responses?view=aspnetcore-10.0), [Playwright .NET library](https://playwright.dev/dotnet/docs/library) and [network routing](https://playwright.dev/dotnet/docs/api/class-route).
