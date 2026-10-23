using System.Net.Http.Json;
using System.Text.Json;
using EnterpriseCaseIntelligence.Domain.Cases;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace EnterpriseCaseIntelligence.Api.Tests.Workspace;

[Trait("Category", "Browser")]
public sealed class WorkspaceBrowserTests(WorkspaceBrowser driver) : IClassFixture<WorkspaceBrowser>
{
    private static Task VisibleText(IPage page, string id, string text) => Expect(page.Locator("#" + id)).ToContainTextAsync(text);

    private static async Task Draft(IPage page, string title = "Correct the fictional guide", string description = "A synthetic description.")
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "+ New case", Exact = true }).ClickAsync();
        await page.GetByLabel("Title", new() { Exact = false }).FillAsync(title);
        await page.GetByLabel("Description", new() { Exact = false }).FillAsync(description);
    }

    [Fact]
    public async Task EmptyCreateReviewCloseAndRestartUseRealRetainedData()
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var page = session.Page;
        await session.OpenAsync();
        await VisibleText(page, "list-message", "No cases match");
        await Draft(page, "  Correct the fictional guide  ", "Line one.\nLine two.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create case", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Dialog)).Not.ToBeVisibleAsync();
        await Expect(page.Locator("#case-title")).ToHaveTextAsync("Correct the fictional guide");
        await Expect(page.Locator("#case-description")).ToHaveTextAsync("Line one.\nLine two.");
        await VisibleText(page, "case-history", "Case created");
        var id = Guid.Parse(await page.Locator("#case-id").InnerTextAsync());
        await page.GetByRole(AriaRole.Button, new() { Name = "Start review", Exact = true }).ClickAsync();
        await Expect(page.Locator("#case-status")).ToHaveTextAsync("In review");
        await VisibleText(page, "case-history", "New → In review");
        if (Environment.GetEnvironmentVariable("ECI_WORKSPACE_SCREENSHOTS") is { Length: > 0 } previewDirectory)
        {
            Directory.CreateDirectory(previewDirectory);
            await page.ScreenshotAsync(new() { Path = Path.Combine(previewDirectory, "workspace-journey.png"), FullPage = true });
        }
        await page.GetByRole(AriaRole.Button, new() { Name = "Close case", Exact = true }).ClickAsync();
        await Expect(page.Locator("#case-status")).ToHaveTextAsync("Closed");
        await Expect(page.Locator("#advance-case")).Not.ToBeVisibleAsync();
        await Expect(page.Locator("#case-history li")).ToHaveCountAsync(3);
        Assert.Equal(1, session.Database.Count());
        Assert.Equal(CaseStatus.Closed, session.Store.Find(id)!.Status);
        await session.Host.KillAsync();
        using var restarted = await ApiProcess.StartAsync(session.Database.Path);
        await page.GotoAsync(new Uri(restarted.Address, "/workspace").ToString());
        await page.Locator("#case-list button").ClickAsync();
        await Expect(page.Locator("#case-status")).ToHaveTextAsync("Closed");
        await Expect(page.Locator("#case-history li")).ToHaveCountAsync(3);
        Assert.Empty(session.BrowserErrors);
    }

    [Fact]
    public async Task PaginationFiltersAndRefreshNeverLoadTheWholeCollection()
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var ids = new List<Guid>();
        for (var i = 0; i < 25; i++) ids.Add(await session.CreateAsync($"Synthetic case {i:D2}"));
        await session.SetStatusAsync(ids[0], "InReview");
        await session.SetStatusAsync(ids[1], "InReview");
        await session.SetStatusAsync(ids[1], "Closed");
        var requests = new List<string>();
        session.Page.Request += (_, request) => { if (new Uri(request.Url).AbsolutePath == "/cases") requests.Add(request.Url); };
        await session.OpenAsync();
        await Expect(session.Page.Locator("#case-list li")).ToHaveCountAsync(20);
        Assert.Single(requests);
        var firstIds = await session.Page.Locator("#case-list button").EvaluateAllAsync<string[]>("buttons => buttons.map(b => b.dataset.id)");
        await session.Page.GetByRole(AriaRole.Button, new() { Name = "Next page" }).ClickAsync();
        await Expect(session.Page.Locator("#case-list li")).ToHaveCountAsync(5);
        await Expect(session.Page.Locator("#next-page")).Not.ToBeVisibleAsync();
        var lastIds = await session.Page.Locator("#case-list button").EvaluateAllAsync<string[]>("buttons => buttons.map(b => b.dataset.id)");
        Assert.Equal(ids.Select(id => id.ToString("D")).Order(StringComparer.Ordinal), firstIds.Concat(lastIds));
        foreach (var status in new[] { ("InReview", 1), ("Closed", 1), ("New", 20), ("", 20) })
        {
            await session.Page.GetByLabel("Status", new() { Exact = true }).SelectOptionAsync(status.Item1);
            await Expect(session.Page.Locator("#case-list li")).ToHaveCountAsync(status.Item2);
            if (status.Item1.Length > 0) Assert.DoesNotContain("after=", requests[^1]);
        }
        await session.Page.GetByRole(AriaRole.Button, new() { Name = "Next page" }).ClickAsync();
        await Expect(session.Page.Locator("#case-list li")).ToHaveCountAsync(5);
        await session.Page.GetByRole(AriaRole.Button, new() { Name = "Refresh list" }).ClickAsync();
        await Expect(session.Page.Locator("#case-list li")).ToHaveCountAsync(20);
        Assert.DoesNotContain("after=", requests[^1]);
    }

    [Fact]
    public async Task LostCommittedCreationRetriesOneKeyAndReadsCurrentState()
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var page = session.Page;
        var sentKeys = new List<string>();
        var sentBodies = new List<string>();
        await page.RouteAsync("**/cases", async route =>
        {
            if (route.Request.Method != "POST") { await route.ContinueAsync(); return; }
            sentKeys.Add(route.Request.Headers["idempotency-key"]);
            sentBodies.Add(route.Request.PostData!);
            if (sentKeys.Count == 1)
            {
                // Forward to the actual server, change committed state, then drop
                // only the browser response. No production failure route exists.
                var response = await route.FetchAsync();
                Assert.Equal(201, response.Status);
                using var body = JsonDocument.Parse(await response.TextAsync());
                var id = body.RootElement.GetProperty("id").GetGuid();
                await session.SetStatusAsync(id, "InReview");
                await session.SetStatusAsync(id, "Closed");
                await response.DisposeAsync();
                await route.AbortAsync();
            }
            else await route.ContinueAsync();
        });
        await session.OpenAsync();
        await Draft(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create case", Exact = true }).ClickAsync();
        await VisibleText(page, "create-message", "could not be confirmed");
        await Expect(page.Locator("#title")).ToHaveAttributeAsync("readonly", "");
        await VisibleText(page, "uncertain-note", "Reloading or closing");
        await page.GetByRole(AriaRole.Button, new() { Name = "Retry same draft" }).ClickAsync();
        await Expect(page.Locator("#case-status")).ToHaveTextAsync("Closed");
        Assert.Equal(2, sentKeys.Count);
        Assert.Equal(sentKeys[0], sentKeys[1]);
        Assert.Matches("^\"[0-9a-f-]{36}\"$", sentKeys[0]);
        Assert.Equal(sentBodies[0], sentBodies[1]);
        Assert.Equal(1, session.Database.Count());
        Assert.Equal(1L, session.Database.Scalar("SELECT count(*) FROM case_creation_keys"));
        Assert.Equal(3L, session.Database.Scalar("SELECT count(*) FROM case_status_history"));
        Assert.Empty(session.BrowserErrors);
    }

    [Fact]
    public async Task UncertainDraftRequiresAcknowledgmentAndKeyMismatchNeverReplacesIt()
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var page = session.Page;
        var keys = new List<string>();
        await page.RouteAsync("**/cases", async route =>
        {
            if (route.Request.Method != "POST") { await route.ContinueAsync(); return; }
            keys.Add(route.Request.Headers["idempotency-key"]);
            await route.FulfillAsync(new() { Status = 422, Body = "SYNTHETIC_SENSITIVE_MARKER" });
        });
        await session.OpenAsync();
        await Draft(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create case", Exact = true }).ClickAsync();
        await VisibleText(page, "create-message", "already bound");
        await page.GetByRole(AriaRole.Button, new() { Name = "Retry same draft" }).ClickAsync();
        await VisibleText(page, "create-message", "already bound");
        Assert.Equal(keys[0], keys[1]);
        await page.GetByRole(AriaRole.Button, new() { Name = "Use a different draft" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Start new draft", Exact = true })).ToBeDisabledAsync();
        await page.GetByRole(AriaRole.Checkbox).CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Start new draft", Exact = true }).ClickAsync();
        await VisibleText(page, "create-message", "may have created a separate case");
        await Expect(page.Locator("#title")).ToHaveValueAsync("");
        await page.GetByLabel("Title").FillAsync("A different synthetic draft");
        await page.GetByLabel("Description").FillAsync("Fictional replacement.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create case", Exact = true }).ClickAsync();
        await VisibleText(page, "create-message", "already bound");
        Assert.NotEqual(keys[0], keys[2]);
        Assert.DoesNotContain("SYNTHETIC_SENSITIVE_MARKER", await page.Locator("body").InnerTextAsync());
    }

    [Fact]
    public async Task ValidationKeepsDraftAndPreventsDuplicatePendingSubmissions()
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var page = session.Page;
        await session.OpenAsync();
        await Draft(page, " ", "A retained description.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create case", Exact = true }).ClickAsync();
        await VisibleText(page, "title-error", "1–120");
        await Expect(page.Locator("#title")).ToBeFocusedAsync();
        await Expect(page.Locator("#description")).ToHaveValueAsync("A retained description.");
        Assert.Equal(0, session.Database.Count());
        // JavaScript trims BOM but .NET does not. The real API remains the
        // authority when browser-side normalization would accept this title.
        await page.GetByLabel("Title").FillAsync("\uFEFF" + new string('t', 120));
        await page.GetByRole(AriaRole.Button, new() { Name = "Create case", Exact = true }).ClickAsync();
        await VisibleText(page, "create-message", "request was rejected");
        await VisibleText(page, "title-error", "1–120");
        await Expect(page.Locator("#description")).ToHaveValueAsync("A retained description.");
        await page.GetByLabel("Title").FillAsync(" " + new string('t', 120) + " ");
        await page.GetByLabel("Description").FillAsync(new string('d', 2001));
        await page.GetByRole(AriaRole.Button, new() { Name = "Create case", Exact = true }).ClickAsync();
        await VisibleText(page, "description-error", "1–2,000");
        await page.GetByLabel("Description").FillAsync(" " + new string('d', 2000) + " ");
        var received = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/cases", async route =>
        {
            if (route.Request.Method != "POST") { await route.ContinueAsync(); return; }
            received.TrySetResult(route);
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await route.ContinueAsync();
        });
        try
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Create case", Exact = true }).ClickAsync();
            await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Expect(page.Locator("#create-submit")).ToBeDisabledAsync();
            await page.Locator("#create-form").EvaluateAsync("form => form.dispatchEvent(new Event('submit', {cancelable:true}))");
        }
        finally { release.TrySetResult(); }
        await Expect(page.Locator("#case-title")).ToHaveTextAsync(new string('t', 120));
        Assert.Equal(1, session.Database.Count());
    }

    [Fact]
    public async Task PendingStatusMutationDisablesDuplicatesAndKeepsTheLatestSelection()
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var first = await session.CreateAsync("Pending status case");
        var second = await session.CreateAsync("Other selected case");
        var page = session.Page;
        await session.OpenAsync();
        await page.Locator($"button[data-id='{first:D}']").ClickAsync();
        await Expect(page.Locator("#case-title")).ToHaveTextAsync("Pending status case");
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        await page.RouteAsync("**/status", async route =>
        {
            Interlocked.Increment(ref writes);
            held.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await route.ContinueAsync();
        });
        try
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Start review", Exact = true }).ClickAsync();
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Expect(page.Locator("#advance-case")).ToBeDisabledAsync();
            await page.Locator("#advance-case").DispatchEventAsync("click");
            await page.Locator($"button[data-id='{second:D}']").ClickAsync();
            await Expect(page.Locator("#case-title")).ToHaveTextAsync("Other selected case");
        }
        finally { release.TrySetResult(); }
        await VisibleText(page, "mutation-message", "Status change confirmed");
        await Expect(page.Locator("#advance-case")).ToBeEnabledAsync();
        await Expect(page.Locator("#case-title")).ToHaveTextAsync("Other selected case");
        Assert.Equal(1, writes);
        Assert.Equal(CaseStatus.InReview, session.Store.Find(first)!.Status);
        Assert.Equal(CaseStatus.New, session.Store.Find(second)!.Status);
    }

    [Fact]
    public async Task ConflictAndIndependentReadFailuresAreSafeAndRecoverable()
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var id = await session.CreateAsync("A synthetic conflict");
        var page = session.Page;
        await session.OpenAsync();
        await page.Locator("#case-list button").ClickAsync();
        await Expect(page.Locator("#case-status")).ToHaveTextAsync("New");
        await session.SetStatusAsync(id, "InReview");
        await session.SetStatusAsync(id, "Closed");
        await page.GetByRole(AriaRole.Button, new() { Name = "Start review", Exact = true }).ClickAsync();
        await VisibleText(page, "mutation-message", "action was rejected");
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh case", Exact = true }).ClickAsync();
        await Expect(page.Locator("#case-status")).ToHaveTextAsync("Closed");
        await page.RouteAsync("**/history", route => route.FulfillAsync(new() { Status = 500, Body = "SYNTHETIC_SENSITIVE_MARKER" }));
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh history" }).ClickAsync();
        await VisibleText(page, "history-message", "could not be loaded");
        await Expect(page.Locator("#case-history li")).ToHaveCountAsync(0);
        await Expect(page.Locator("#case-status")).ToHaveTextAsync("Closed");
        await page.UnrouteAsync("**/history");
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh history" }).ClickAsync();
        await Expect(page.Locator("#case-history li")).ToHaveCountAsync(3);
        await page.RouteAsync("**/cases", route => route.FulfillAsync(new() { Status = 500, Body = "SYNTHETIC_SENSITIVE_MARKER" }));
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh list" }).ClickAsync();
        await VisibleText(page, "list-message", "could not be loaded");
        Assert.DoesNotContain("No cases match", await page.Locator("#list-message").InnerTextAsync());
        Assert.DoesNotContain("SYNTHETIC_SENSITIVE_MARKER", await page.Locator("body").InnerTextAsync());
        await page.UnrouteAsync("**/cases");
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh list" }).ClickAsync();
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task ConfirmedWriteIsStillReportedWhenFollowingReadsFail()
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var id = await session.CreateAsync("Confirmed write with failed refresh");
        var page = session.Page;
        await session.OpenAsync();
        await page.Locator("#case-list button").ClickAsync();
        await Expect(page.Locator("#case-status")).ToHaveTextAsync("New");
        var failReads = false;
        await page.RouteAsync("**/cases/**", async route =>
        {
            if (route.Request.Method == "PUT")
            {
                var response = await route.FetchAsync();
                failReads = true;
                await route.FulfillAsync(new() { Response = response });
                await response.DisposeAsync();
            }
            else if (failReads) await route.FulfillAsync(new() { Status = 500, Body = "not public" });
            else await route.ContinueAsync();
        });
        await page.GetByRole(AriaRole.Button, new() { Name = "Start review", Exact = true }).ClickAsync();
        await VisibleText(page, "mutation-message", "Status change confirmed");
        await VisibleText(page, "detail-message", "could not be loaded");
        await VisibleText(page, "history-message", "could not be loaded");
        Assert.Equal(CaseStatus.InReview, session.Store.Find(id)!.Status);
        await page.UnrouteAsync("**/cases/**");
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh case", Exact = true }).ClickAsync();
        await Expect(page.Locator("#case-status")).ToHaveTextAsync("In review");
    }

    [Fact]
    public async Task DelayedFilterAndSelectionResponsesCannotOverwriteCurrentView()
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var first = await session.CreateAsync("First selection");
        var second = await session.CreateAsync("Latest selection");
        var page = session.Page;
        await session.OpenAsync();
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(2);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/cases?status=New", async route =>
        {
            var response = await route.FetchAsync();
            held.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await route.FulfillAsync(new() { Response = response });
            await response.DisposeAsync();
        });
        var obsoleteListDone = page.WaitForResponseAsync(response => response.Url.Contains("?status=New", StringComparison.Ordinal));
        try
        {
            await page.GetByLabel("Status", new() { Exact = true }).SelectOptionAsync("New");
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await page.GetByLabel("Status", new() { Exact = true }).SelectOptionAsync("Closed");
            await VisibleText(page, "list-message", "No cases match");
        }
        finally { release.TrySetResult(); }
        // Wait for completion of the obsolete HTTP operation, not a timing sleep.
        await obsoleteListDone;
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(0);
        await page.GetByLabel("Status", new() { Exact = true }).SelectOptionAsync("");
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(2);
        await session.SetStatusAsync(first, "InReview");
        held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldReads = 0;
        await page.RouteAsync("**/cases/**", async route =>
        {
            if (!new Uri(route.Request.Url).AbsolutePath.StartsWith($"/cases/{first:D}", StringComparison.Ordinal))
            { await route.ContinueAsync(); return; }
            var response = await route.FetchAsync();
            if (Interlocked.Increment(ref heldReads) == 2) held.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await route.FulfillAsync(new() { Response = response });
            await response.DisposeAsync();
        });
        var obsoleteDone = page.WaitForResponseAsync(response => new Uri(response.Url).AbsolutePath == $"/cases/{first:D}");
        var obsoleteHistoryDone = page.WaitForResponseAsync(response => new Uri(response.Url).AbsolutePath == $"/cases/{first:D}/history");
        try
        {
            await page.Locator($"button[data-id='{first:D}']").ClickAsync();
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await page.Locator($"button[data-id='{second:D}']").ClickAsync();
            await Expect(page.Locator("#case-title")).ToHaveTextAsync("Latest selection");
        }
        finally { release.TrySetResult(); }
        await obsoleteDone;
        await obsoleteHistoryDone;
        // Let browser fetch continuations finish before checking the final view.
        await page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
        await Expect(page.Locator("#case-title")).ToHaveTextAsync("Latest selection");
        await Expect(page.Locator("#case-history li")).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task UpgradedBaselineIsNeverPresentedAsAnOriginalCreation()
    {
        Guid baselineId = default;
        await using var session = await BrowserSession.StartAsync(driver.Browser, database =>
        {
            baselineId = VersionTwoCaseStore.Initialize(database)[0].Id;
            database.Database.Upgrade();
        });
        await session.OpenAsync();
        await session.Page.Locator($"button[data-id='{baselineId:D}']").ClickAsync();
        await VisibleText(session.Page, "case-history", "State first recorded at upgrade");
        Assert.DoesNotContain("Case created", await session.Page.Locator("#case-history").InnerTextAsync());
    }

    [Fact]
    public async Task SafeRenderingKeyboardAndNarrowLayoutUseOnlyLocalAssets()
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        const string title = "<img src=x onerror=alert('fictional')> — Café 😀";
        await session.CreateAsync(title, "<script>window.syntheticExecuted = true</script>\nSecond line.");
        var page = session.Page;
        var requests = new List<string>();
        page.Request += (_, request) => requests.Add(request.Url);
        await session.OpenAsync();
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(1);
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = "Skip to workspace" })).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Enter");
        await Expect(page.Locator("#main")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("#new-case")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Enter");
        await Expect(page.GetByLabel("Title")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.GetByRole(AriaRole.Dialog)).Not.ToBeVisibleAsync();
        await page.Locator("#case-list button").FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await Expect(page.Locator("#case-title")).ToHaveTextAsync(title);
        Assert.Null(await page.EvaluateAsync<object?>("window.syntheticExecuted"));
        Assert.Equal(0, await page.Locator("#case-detail img, #case-detail script").CountAsync());
        Assert.Equal("pre-wrap", await page.Locator("#case-description").EvaluateAsync<string>("e => getComputedStyle(e).whiteSpace"));
        await page.SetViewportSizeAsync(375, 900);
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        await page.EvaluateAsync("document.documentElement.style.fontSize = '200%'");
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        Assert.All(requests, url => Assert.Equal(session.Host.Address.Authority, new Uri(url).Authority));
        Assert.Equal(0, await page.EvaluateAsync<int>("localStorage.length + sessionStorage.length"));
        Assert.Equal("/workspace", new Uri(page.Url).AbsolutePath);
        Assert.Empty(session.BrowserErrors);
        if (Environment.GetEnvironmentVariable("ECI_WORKSPACE_SCREENSHOTS") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await page.EvaluateAsync("document.documentElement.style.fontSize = ''");
            await page.ScreenshotAsync(new() { Path = Path.Combine(directory, "workspace-mobile.png"), FullPage = true });
            await page.SetViewportSizeAsync(1440, 1100);
            await page.ScreenshotAsync(new() { Path = Path.Combine(directory, "workspace-desktop.png"), FullPage = true });
        }
    }
}
