using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace EnterpriseCaseIntelligence.Api.Tests.Workspace;

[Trait("Category", "Browser")]
public sealed class CaseLookupBrowserTests(WorkspaceBrowser driver) : IClassFixture<WorkspaceBrowser>
{
    private static async Task Lookup(IPage page, string id)
    {
        await page.GetByLabel("Open case by ID", new() { Exact = true }).FillAsync(id);
        await page.GetByLabel("Open case by ID", new() { Exact = true }).PressAsync("Enter");
    }

    [Fact]
    public async Task KnownCaseOutsideTheFilteredPageOpensWithoutPagingOrChangingTheList()
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var ids = new List<Guid>();
        for (var i = 0; i < 23; i++) ids.Add(await session.CreateAsync($"Batch synthetic case {i:D2}"));
        var target = ids.OrderBy(id => id.ToString("D"), StringComparer.Ordinal).Last();
        await session.SetStatusAsync(target, "InReview");
        await session.SetStatusAsync(target, "Closed");
        var page = session.Page;
        var listRequests = new List<string>();
        var reads = new List<string>();
        page.Request += (_, request) =>
        {
            var path = new Uri(request.Url).AbsolutePath;
            if (path == "/cases") listRequests.Add(request.Url);
            if (path.StartsWith("/cases/", StringComparison.Ordinal)) reads.Add(request.Method + " " + path);
        };
        await session.OpenAsync();
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(20);
        await page.GetByLabel("Status", new() { Exact = true }).SelectOptionAsync("New");
        await page.GetByLabel("Find cases").FillAsync("Batch");
        await page.GetByLabel("Find cases").PressAsync("Enter");
        await Expect(page.Locator("#search-context")).ToHaveTextAsync("Using the submitted title search.");
        await Expect(page.Locator("#next-page")).ToBeVisibleAsync();
        var visibleIds = await page.Locator("#case-list button").EvaluateAllAsync<string[]>("buttons => buttons.map(b => b.dataset.id)");
        var before = listRequests.Count;
        await page.GetByLabel("Open case by ID").FillAsync("  " + target.ToString("D").ToUpperInvariant() + "  ");
        await page.GetByRole(AriaRole.Button, new() { Name = "Open case", Exact = true }).ClickAsync();
        await Expect(page.Locator("#case-id")).ToHaveTextAsync(target.ToString("D"));
        await Expect(page.Locator("#case-status")).ToHaveTextAsync("Closed");
        await Expect(page.Locator("#case-history li")).ToHaveCountAsync(3);
        await Expect(page.GetByLabel("Open case by ID")).ToHaveValueAsync(target.ToString("D"));
        Assert.Equal(before, listRequests.Count);
        Assert.Equal(new[] { $"GET /cases/{target:D}", $"GET /cases/{target:D}/history" }, reads.Order(StringComparer.Ordinal));
        Assert.Equal(visibleIds, await page.Locator("#case-list button").EvaluateAllAsync<string[]>("buttons => buttons.map(b => b.dataset.id)"));
        await Expect(page.Locator("#case-list button[aria-pressed='true']")).ToHaveCountAsync(0);
        await page.Locator("#next-page").ClickAsync();
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(2);
        Assert.Equal(before + 1, listRequests.Count);
        Assert.Contains("after=", listRequests[^1]);
        Assert.Contains("status=New", listRequests[^1]);
        Assert.Contains("title=Batch", listRequests[^1]);
        await Expect(page.Locator("#case-id")).ToHaveTextAsync(target.ToString("D"));
        Assert.Equal(23, session.Database.Count());
        Assert.Empty(session.BrowserErrors);
    }

    [Fact]
    public async Task InvalidInputNeverMakesCaseRequestsAndCanRecoverWithKeyboardOnly()
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var id = await session.CreateAsync("Synthetic keyboard lookup");
        var page = session.Page;
        await session.OpenAsync();
        await page.Locator("#case-list button").ClickAsync();
        await Expect(page.Locator("#case-id")).ToHaveTextAsync(id.ToString("D"));
        await Expect(page.Locator("#case-history li")).ToHaveCountAsync(1);
        var requests = 0;
        page.Request += (_, request) => { if (new Uri(request.Url).AbsolutePath.StartsWith("/cases", StringComparison.Ordinal)) Interlocked.Increment(ref requests); };
        foreach (var invalid in new[] { "", " ", Guid.Empty.ToString("D"), id.ToString("N"), "{" + id.ToString("D") + "}", "/cases/" + id.ToString("D"), "https://example.invalid/", id.ToString("D") + "?x=1", id.ToString("D") + "#main", new string('g', 36), "<img src=x onerror=alert('SYNTHETIC_MARKER')>" })
        {
            await Lookup(page, invalid);
            await Expect(page.Locator("#case-lookup-error")).ToContainTextAsync("8-4-4-4-12");
            await Expect(page.GetByLabel("Open case by ID")).ToHaveAttributeAsync("aria-invalid", "true");
            await Expect(page.GetByLabel("Open case by ID")).ToBeFocusedAsync();
            await Expect(page.Locator("#case-id")).ToHaveTextAsync(id.ToString("D"));
        }
        Assert.Equal(0, requests);
        await Lookup(page, id.ToString("D"));
        await Expect(page.Locator("#case-lookup-error")).ToHaveTextAsync("");
        await Expect(page.GetByLabel("Open case by ID")).ToHaveAttributeAsync("aria-invalid", "false");
        await Expect(page.Locator("#case-title")).ToHaveTextAsync("Synthetic keyboard lookup");
        await Expect(page.Locator("#case-history li")).ToHaveCountAsync(1);
        Assert.Equal("", new Uri(page.Url).Query);
        Assert.Equal("", new Uri(page.Url).Fragment);
        Assert.Equal(0, await page.EvaluateAsync<int>("localStorage.length + sessionStorage.length"));
        Assert.Equal(0, await page.Locator("#case-lookup-form img").CountAsync());
        if (Environment.GetEnvironmentVariable("ECI_WORKSPACE_SCREENSHOTS") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await page.ScreenshotAsync(new() { Path = Path.Combine(directory, "case-lookup-desktop.png"), FullPage = true });
        }
        await page.SetViewportSizeAsync(375, 950);
        await page.EvaluateAsync("document.documentElement.style.fontSize = '200%'");
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        await Expect(page.GetByLabel("Open case by ID")).ToBeVisibleAsync();
        if (Environment.GetEnvironmentVariable("ECI_WORKSPACE_SCREENSHOTS") is { Length: > 0 } mobileDirectory)
            await page.ScreenshotAsync(new() { Path = Path.Combine(mobileDirectory, "case-lookup-mobile.png"), FullPage = true });
        Assert.Empty(session.BrowserErrors);
    }

    [Fact]
    public async Task MissingUnavailableAndMismatchedCaseResponsesDoNotInventSuccessAndCanRecover()
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var id = await session.CreateAsync("Recovered synthetic case");
        var page = session.Page;
        await session.OpenAsync();
        await Lookup(page, Guid.NewGuid().ToString("D"));
        await Expect(page.Locator("#detail-message")).ToContainTextAsync("could not be found");
        await Expect(page.Locator("#history-message")).ToContainTextAsync("could not be loaded");
        await Expect(page.Locator("#case-detail")).Not.ToBeVisibleAsync();
        var routePattern = "**/cases/" + id.ToString("D");
        foreach (var mismatch in new[] { false, true })
        {
            await page.RouteAsync(routePattern, route => route.FulfillAsync(new()
            {
                Status = mismatch ? 200 : 503,
                ContentType = mismatch ? "application/json" : "text/html",
                Body = mismatch ? JsonSerializer.Serialize(new { id = Guid.NewGuid(), title = "Unexpected synthetic case", description = "Wrong identity.", status = "New" }) : "SYNTHETIC_SENSITIVE_MARKER"
            }));
            await Lookup(page, id.ToString("D"));
            await Expect(page.Locator("#detail-message")).ToContainTextAsync("could not be loaded");
            await Expect(page.Locator("#case-detail")).Not.ToBeVisibleAsync();
            await Expect(page.Locator("#case-history li")).ToHaveCountAsync(1);
            Assert.DoesNotContain("SYNTHETIC_SENSITIVE_MARKER", await page.Locator("body").InnerTextAsync());
            await page.UnrouteAsync(routePattern);
        }
        await page.Locator("#refresh-case").ClickAsync();
        await Expect(page.Locator("#case-title")).ToHaveTextAsync("Recovered synthetic case");
        await Expect(page.Locator("#case-history li")).ToHaveCountAsync(1);
        Assert.Equal(1, session.Database.Count());
        Assert.Empty(session.BrowserErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateLookupSuccessOrFailureCannotOverrideANewerListSelection(bool fail)
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var first = await session.CreateAsync("Earlier lookup");
        var second = await session.CreateAsync("Latest list selection");
        var page = session.Page;
        await session.OpenAsync();
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(2);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await page.RouteAsync($"**/cases/{first:D}**", async route =>
        {
            var response = await route.FetchAsync();
            if (Interlocked.Increment(ref count) == 2) held.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (fail) await route.FulfillAsync(new() { Status = 500, Body = "SYNTHETIC_SENSITIVE_MARKER" });
            else await route.FulfillAsync(new() { Response = response });
            await response.DisposeAsync();
        });
        var detailDone = page.WaitForResponseAsync(response => new Uri(response.Url).AbsolutePath == $"/cases/{first:D}");
        var historyDone = page.WaitForResponseAsync(response => new Uri(response.Url).AbsolutePath == $"/cases/{first:D}/history");
        try
        {
            await Lookup(page, first.ToString("D"));
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await page.Locator($"#case-list button[data-id='{second:D}']").ClickAsync();
            await Expect(page.Locator("#case-title")).ToHaveTextAsync("Latest list selection");
            await Expect(page.Locator("#case-history li")).ToHaveCountAsync(1);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(detailDone, historyDone);
        await page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
        await Expect(page.Locator("#case-title")).ToHaveTextAsync("Latest list selection");
        await Expect(page.GetByLabel("Open case by ID")).ToHaveValueAsync(second.ToString("D"));
        await Expect(page.Locator("#detail-message")).ToHaveTextAsync("");
        await Expect(page.Locator("#history-message")).ToHaveTextAsync("");
        Assert.DoesNotContain("SYNTHETIC_SENSITIVE_MARKER", await page.Locator("body").InnerTextAsync());
        Assert.Empty(session.BrowserErrors);
    }

    [Fact]
    public async Task LookupPreservesAnUncertainDraftUntilItsExplicitSameKeyRetry()
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var known = await session.CreateAsync("Known synthetic work");
        var page = session.Page;
        var keys = new List<string>();
        var bodies = new List<string>();
        await page.RouteAsync("**/cases", async route =>
        {
            if (route.Request.Method != "POST") { await route.ContinueAsync(); return; }
            keys.Add(route.Request.Headers["idempotency-key"]);
            bodies.Add(route.Request.PostData!);
            if (keys.Count == 1)
            {
                var response = await route.FetchAsync();
                Assert.Equal(201, response.Status);
                await response.DisposeAsync();
                await route.AbortAsync();
            }
            else await route.ContinueAsync();
        });
        await session.OpenAsync();
        await page.Locator("#new-case").ClickAsync();
        await page.GetByLabel("Title").FillAsync("Frozen synthetic draft");
        await page.GetByLabel("Description").FillAsync("Retain this exact creation payload.");
        await page.Locator("#create-submit").ClickAsync();
        await Expect(page.Locator("#create-message")).ToContainTextAsync("could not be confirmed");
        await page.Locator("#close-draft").ClickAsync();
        await Lookup(page, known.ToString("D"));
        await Expect(page.Locator("#case-title")).ToHaveTextAsync("Known synthetic work");
        Assert.Single(keys);
        await page.GetByRole(AriaRole.Button, new() { Name = "Resume draft", Exact = true }).ClickAsync();
        await Expect(page.GetByLabel("Title")).ToHaveValueAsync("Frozen synthetic draft");
        await Expect(page.GetByLabel("Description")).ToHaveValueAsync("Retain this exact creation payload.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Retry same draft", Exact = true }).ClickAsync();
        await Expect(page.Locator("#case-title")).ToHaveTextAsync("Frozen synthetic draft");
        Assert.Equal(2, keys.Count);
        Assert.Equal(keys[0], keys[1]);
        Assert.Equal(bodies[0], bodies[1]);
        Assert.Equal(2, session.Database.Count());
        Assert.Equal(1L, session.Database.Scalar("SELECT count(*) FROM case_creation_keys"));
        Assert.Empty(session.BrowserErrors);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task DeniedLookupClearsProtectedFieldsAndCannotBeResubmittedWithoutAccess(int status)
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var id = await session.CreateAsync("Synthetic restricted case");
        var page = session.Page;
        await session.OpenAsync();
        await Lookup(page, id.ToString("D"));
        await Expect(page.Locator("#case-title")).ToHaveTextAsync("Synthetic restricted case");
        await Expect(page.Locator("#case-history li")).ToHaveCountAsync(1);
        await page.RouteAsync("**/cases/" + id.ToString("D"), route => route.FulfillAsync(new() { Status = status, Body = "SYNTHETIC_PRIVATE_DENIAL" }));
        await Lookup(page, id.ToString("D"));
        await Expect(page.Locator("#protected-workspace")).Not.ToBeVisibleAsync();
        await Expect(page.GetByLabel("Open case by ID")).ToHaveValueAsync("");
        await Expect(page.Locator("#case-lookup-error")).ToHaveTextAsync("");
        await Expect(page.Locator("#case-title")).ToHaveTextAsync("");
        await Expect(page.Locator("#case-history li")).ToHaveCountAsync(0);
        var afterDenial = 0;
        page.Request += (_, request) => { if (new Uri(request.Url).AbsolutePath.StartsWith("/cases", StringComparison.Ordinal)) Interlocked.Increment(ref afterDenial); };
        await page.EvaluateAsync("() => { document.getElementById('case-lookup').value = '00000000-0000-0000-0000-000000000000'; document.getElementById('case-lookup-form').requestSubmit(); }");
        Assert.Equal(0, afterDenial);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_DENIAL", await page.Locator("body").InnerTextAsync());
        Assert.Equal(1, session.Database.Count());
        Assert.Empty(session.BrowserErrors);
    }
}
