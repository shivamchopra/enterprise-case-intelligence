using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace EnterpriseCaseIntelligence.Api.Tests.Workspace;

[Trait("Category", "Browser")]
public sealed class CaseOverviewBrowserTests(WorkspaceBrowser driver) : IClassFixture<WorkspaceBrowser>
{
    private static async Task Counts(IPage page, string newCases, string inReview, string closed, string total)
    {
        await Expect(page.Locator("#overview-counts")).ToBeVisibleAsync();
        await Expect(page.Locator("#overview-new")).ToHaveTextAsync(newCases);
        await Expect(page.Locator("#overview-inReview")).ToHaveTextAsync(inReview);
        await Expect(page.Locator("#overview-closed")).ToHaveTextAsync(closed);
        await Expect(page.Locator("#overview-total")).ToHaveTextAsync(total);
    }

    [Fact]
    public async Task TotalsAreIndependentOfTheListAndRefreshAfterConfirmedWrites()
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var page = session.Page;
        var requests = 0;
        page.Request += (_, request) => { if (new Uri(request.Url).AbsolutePath == "/cases/summary") Interlocked.Increment(ref requests); };
        await session.OpenAsync();
        await Counts(page, "0", "0", "0", "0");
        var ids = new List<Guid>();
        for (var i = 0; i < 23; i++) ids.Add(await session.CreateAsync($"Synthetic work {i:D2}"));
        await session.SetStatusAsync(ids[0], "InReview");
        await session.SetStatusAsync(ids[1], "InReview");
        await session.SetStatusAsync(ids[1], "Closed");
        await page.Locator("#refresh-list").ClickAsync();
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(20);
        await Counts(page, "0", "0", "0", "0"); // External changes require an explicit totals refresh.
        Assert.Equal(1, requests);
        await page.Locator("#refresh-overview").ClickAsync();
        await Counts(page, "21", "1", "1", "23");
        await page.Locator("#next-page").ClickAsync();
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(3);
        await page.GetByLabel("Status", new() { Exact = true }).SelectOptionAsync("Closed");
        await page.GetByLabel("Find cases").FillAsync("No match");
        await page.GetByLabel("Find cases").PressAsync("Enter");
        await Expect(page.Locator("#list-message")).ToContainTextAsync("No cases match");
        await page.GetByLabel("Open case by ID").FillAsync(ids[2].ToString("D"));
        await page.GetByLabel("Open case by ID").PressAsync("Enter");
        await Expect(page.Locator("#case-status")).ToHaveTextAsync("New");
        await Counts(page, "21", "1", "1", "23");
        Assert.Equal(2, requests);
        await page.Locator("#advance-case").ClickAsync();
        await Counts(page, "20", "2", "1", "23");
        await Expect(page.GetByLabel("Find cases")).ToHaveValueAsync("No match");
        await Expect(page.GetByLabel("Status", new() { Exact = true })).ToHaveValueAsync("Closed");
        await page.Locator("#new-case").ClickAsync();
        await page.GetByLabel("Title").FillAsync("New synthetic work");
        await page.GetByLabel("Description").FillAsync("Fictional overview journey.");
        await page.Locator("#create-submit").ClickAsync();
        await Counts(page, "21", "2", "1", "24");
        Assert.Equal(4, requests);
        Assert.Equal(24, session.Database.Count());
        await page.SetViewportSizeAsync(375, 950);
        await page.EvaluateAsync("document.documentElement.style.fontSize = '200%'");
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        if (Environment.GetEnvironmentVariable("ECI_WORKSPACE_SCREENSHOTS") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await page.EvaluateAsync("window.scrollTo(0, 0)");
            await page.ScreenshotAsync(new() { Path = Path.Combine(directory, "workload-overview-mobile.png"), FullPage = true });
        }
        Assert.Empty(session.BrowserErrors);
    }

    [Fact]
    public async Task LoadingAndFailedTotalsAreDistinctFromZeroAndDoNotDisableCaseWork()
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        await session.CreateAsync("Synthetic independent case");
        var page = session.Page;
        await session.OpenAsync();
        await Counts(page, "1", "0", "0", "1");
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/cases/summary", async route =>
        {
            held.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await route.FulfillAsync(new() { Status = 503, Body = "SYNTHETIC_SENSITIVE_MARKER" });
        });
        try
        {
            await page.Locator("#refresh-overview").ClickAsync();
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Expect(page.Locator("#overview-message")).ToContainTextAsync("Loading");
            await Expect(page.Locator("#overview-counts")).Not.ToBeVisibleAsync();
            await page.Locator("#case-list button").ClickAsync();
            await Expect(page.Locator("#case-title")).ToHaveTextAsync("Synthetic independent case");
        }
        finally { release.TrySetResult(); }
        await Expect(page.Locator("#overview-message")).ToContainTextAsync("could not be loaded");
        await Expect(page.Locator("#overview-total")).ToHaveTextAsync("");
        Assert.DoesNotContain("SYNTHETIC_SENSITIVE_MARKER", await page.Locator("body").InnerTextAsync());
        await page.UnrouteAsync("**/cases/summary");
        await page.Locator("#refresh-overview").ClickAsync();
        await Counts(page, "1", "0", "0", "1");
        await page.RouteAsync("**/cases", route => route.FulfillAsync(new() { Status = 500, Body = "SYNTHETIC_SENSITIVE_MARKER" }));
        await page.Locator("#advance-case").ClickAsync();
        await Counts(page, "0", "1", "0", "1");
        await Expect(page.Locator("#mutation-message")).ToContainTextAsync("Status change confirmed");
        await Expect(page.Locator("#list-message")).ToContainTextAsync("could not be loaded");
        await Expect(page.Locator("#case-status")).ToHaveTextAsync("In review");
        Assert.Empty(session.BrowserErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderTotalsSuccessOrFailureCannotReplaceANewerRefresh(bool fail)
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var page = session.Page;
        await session.OpenAsync();
        await Counts(page, "0", "0", "0", "0");
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await page.RouteAsync("**/cases/summary", async route =>
        {
            if (Interlocked.Increment(ref count) != 1) { await route.ContinueAsync(); return; }
            var response = await route.FetchAsync();
            held.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (fail) await route.FulfillAsync(new() { Status = 500, Body = "SYNTHETIC_SENSITIVE_MARKER" });
            else await route.FulfillAsync(new() { Response = response });
            await response.DisposeAsync();
        });
        var oldDone = page.WaitForResponseAsync(response => new Uri(response.Url).AbsolutePath == "/cases/summary");
        try
        {
            await page.Locator("#refresh-overview").ClickAsync();
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await session.CreateAsync("Later synthetic case");
            await page.Locator("#refresh-overview").ClickAsync();
            await Counts(page, "1", "0", "0", "1");
        }
        finally { release.TrySetResult(); }
        await oldDone;
        await page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
        await Counts(page, "1", "0", "0", "1");
        await Expect(page.Locator("#overview-message")).ToHaveTextAsync("");
        Assert.Equal(2, count);
        Assert.Empty(session.BrowserErrors);
    }

    [Theory]
    [InlineData("{\"new\":-1,\"inReview\":0,\"closed\":0,\"total\":-1}")]
    [InlineData("{\"new\":0.5,\"inReview\":0,\"closed\":0,\"total\":0.5}")]
    [InlineData("{\"new\":\"1\",\"inReview\":0,\"closed\":0,\"total\":1}")]
    [InlineData("{\"new\":9007199254740992,\"inReview\":0,\"closed\":0,\"total\":9007199254740992}")]
    [InlineData("{\"new\":1,\"inReview\":0,\"closed\":0,\"total\":0}")]
    [InlineData("{\"total\":0}")]
    public async Task InvalidTotalsAreUnavailableAndCanRecoverWithoutAFalseZero(string body)
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        await session.CreateAsync("Synthetic visible case");
        var page = session.Page;
        await page.RouteAsync("**/cases/summary", route => route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = body }));
        await session.OpenAsync();
        await Expect(page.Locator("#overview-message")).ToContainTextAsync("could not be loaded");
        await Expect(page.Locator("#overview-counts")).Not.ToBeVisibleAsync();
        await Expect(page.Locator("#overview-total")).ToHaveTextAsync("");
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(1);
        await page.UnrouteAsync("**/cases/summary");
        await page.Locator("#refresh-overview").ClickAsync();
        await Counts(page, "1", "0", "0", "1");
        Assert.Empty(session.BrowserErrors);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task AccessLossClearsCountsAndLateSuccessCannotRestoreThem(int status)
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        await session.CreateAsync("Synthetic protected counts");
        var page = session.Page;
        await session.OpenAsync();
        await Counts(page, "1", "0", "0", "1");
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/cases/summary", async route =>
        {
            var response = await route.FetchAsync();
            held.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await route.FulfillAsync(new() { Response = response });
            await response.DisposeAsync();
        });
        var completed = page.WaitForResponseAsync(response => new Uri(response.Url).AbsolutePath == "/cases/summary");
        try
        {
            await page.Locator("#refresh-overview").ClickAsync();
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await page.RouteAsync("**/cases", route => route.FulfillAsync(new() { Status = status, Body = "SYNTHETIC_PRIVATE_DENIAL" }));
            await page.Locator("#refresh-list").ClickAsync();
            await Expect(page.Locator("#protected-workspace")).Not.ToBeVisibleAsync();
        }
        finally { release.TrySetResult(); }
        await completed;
        await page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
        foreach (var field in new[] { "new", "inReview", "closed", "total" })
            await Expect(page.Locator("#overview-" + field)).ToHaveTextAsync("");
        var afterDenial = 0;
        page.Request += (_, request) => { if (new Uri(request.Url).AbsolutePath == "/cases/summary") Interlocked.Increment(ref afterDenial); };
        await page.Locator("#refresh-overview").DispatchEventAsync("click");
        Assert.Equal(0, afterDenial);
        Assert.Empty(session.BrowserErrors);
    }
}
