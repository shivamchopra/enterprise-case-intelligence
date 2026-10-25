using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace EnterpriseCaseIntelligence.Api.Tests.Workspace;

[Trait("Category", "Browser")]
public sealed class CaseTitleSearchBrowserTests(WorkspaceBrowser driver) : IClassFixture<WorkspaceBrowser>
{
    private static async Task Search(IPage page, string title)
    {
        await page.GetByLabel("Find cases", new() { Exact = true }).FillAsync(title);
        await page.GetByRole(AriaRole.Button, new() { Name = "Search", Exact = true }).ClickAsync();
    }

    [Fact]
    public async Task SubmittedSearchPersistsAcrossPagingFiltersRefreshMutationAndRestart()
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        for (var i = 0; i < 25; i++)
        {
            var id = await session.CreateAsync($"Needle synthetic {i:D2}");
            if (i == 0) { await session.SetStatusAsync(id, "InReview"); await session.SetStatusAsync(id, "Closed"); }
        }
        var closed = await session.CreateAsync("Other closed case");
        await session.SetStatusAsync(closed, "InReview");
        await session.SetStatusAsync(closed, "Closed");
        await session.CreateAsync("Other new case");
        var page = session.Page;
        var requests = new List<string>();
        page.Request += (_, request) => { if (new Uri(request.Url).AbsolutePath == "/cases") requests.Add(request.Url); };
        await session.OpenAsync();
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(20);
        Assert.Single(requests);
        await page.GetByLabel("Find cases").FillAsync("Needle");
        await Expect(page.Locator("#search-context")).ToContainTextAsync("Text edited");
        Assert.Single(requests);
        await page.GetByLabel("Find cases").PressAsync("Enter");
        await Expect(page.Locator("#search-context")).ToHaveTextAsync("Using the submitted title search.");
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(20);
        Assert.Contains("title=Needle", requests[^1]);
        var submittedCount = requests.Count;
        await page.GetByLabel("Find cases").FillAsync("Unsubmitted edit");
        Assert.Equal(submittedCount, requests.Count);
        await page.GetByRole(AriaRole.Button, new() { Name = "Next page" }).ClickAsync();
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(5);
        Assert.Contains("title=Needle", requests[^1]);
        Assert.Contains("after=", requests[^1]);
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh list" }).ClickAsync();
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(20);
        Assert.Contains("title=Needle", requests[^1]);
        Assert.DoesNotContain("after=", requests[^1]);
        await page.GetByLabel("Status", new() { Exact = true }).SelectOptionAsync("Closed");
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(1);
        Assert.Contains("title=Needle", requests[^1]);
        await page.GetByRole(AriaRole.Button, new() { Name = "Clear search", Exact = true }).ClickAsync();
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(2);
        await Expect(page.GetByLabel("Find cases")).ToHaveValueAsync("");
        Assert.Contains("status=Closed", requests[^1]);
        Assert.DoesNotContain("title=", requests[^1]);
        await page.GetByLabel("Status", new() { Exact = true }).SelectOptionAsync("New");
        await Search(page, "Other");
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(1);
        await page.Locator("#case-list button").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Start review", Exact = true }).ClickAsync();
        await Expect(page.Locator("#case-status")).ToHaveTextAsync("In review");
        await Expect(page.Locator("#list-message")).ToContainTextAsync("No cases match");
        Assert.Contains("title=Other", requests[^1]);
        Assert.Contains("status=New", requests[^1]);
        Assert.DoesNotContain("after=", requests[^1]);
        await session.Host.KillAsync();
        using var restarted = await ApiProcess.StartAsync(session.Database.Path);
        await page.GotoAsync(new Uri(restarted.Address, "/workspace").ToString());
        await Search(page, "Other");
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(2);
        Assert.Empty(session.BrowserErrors);
    }

    [Fact]
    public async Task InvalidAndUnavailableSearchesAreDistinctFromNoMatchesAndCanRecover()
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        await session.CreateAsync("Sample guide");
        var page = session.Page;
        await session.OpenAsync();
        await Search(page, " ");
        await Expect(page.Locator("#search-error")).ToContainTextAsync("1–120");
        await Expect(page.GetByLabel("Find cases")).ToHaveAttributeAsync("aria-invalid", "true");
        await Expect(page.GetByLabel("Find cases")).ToHaveValueAsync(" ");
        await Search(page, new string('x', 121));
        await Expect(page.Locator("#search-error")).ToContainTextAsync("1–120");
        await Search(page, "guide");
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(1);
        await Expect(page.Locator("#search-error")).ToHaveTextAsync("");
        await Search(page, "Guide");
        await Expect(page.Locator("#list-message")).ToContainTextAsync("No cases match");
        var moved = session.Database.Path + ".offline";
        File.Move(session.Database.Path, moved);
        try
        {
            await Search(page, "guide");
            await Expect(page.Locator("#list-message")).ToContainTextAsync("could not be loaded");
            Assert.DoesNotContain("No cases match", await page.Locator("#list-message").InnerTextAsync());
            await Expect(page.GetByLabel("Find cases")).ToHaveValueAsync("guide");
            Assert.False(File.Exists(session.Database.Path));
        }
        finally { File.Move(moved, session.Database.Path); }
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh list" }).ClickAsync();
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(1);
        await page.GetByRole(AriaRole.Button, new() { Name = "Clear search", Exact = true }).ClickAsync();
        await Expect(page.Locator("#search-context")).ToHaveTextAsync("Showing all titles.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObsoleteSearchSuccessOrFailureCannotOverrideClearedView(bool fail)
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        await session.CreateAsync("Needle case");
        await session.CreateAsync("Other case");
        var page = session.Page;
        await session.OpenAsync();
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(2);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/cases?title=Needle", async route =>
        {
            var response = await route.FetchAsync();
            held.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (fail) await route.FulfillAsync(new() { Status = 500, Body = "SYNTHETIC_SENSITIVE_MARKER" });
            else await route.FulfillAsync(new() { Response = response });
            await response.DisposeAsync();
        });
        var obsolete = page.WaitForResponseAsync(response => response.Url.EndsWith("?title=Needle", StringComparison.Ordinal));
        try
        {
            await Search(page, "Needle");
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Search(page, "Other");
            await Expect(page.Locator("#case-list li")).ToHaveCountAsync(1);
            await Expect(page.Locator("#case-list button")).ToContainTextAsync("Other case");
            await page.GetByRole(AriaRole.Button, new() { Name = "Clear search", Exact = true }).ClickAsync();
            await Expect(page.Locator("#case-list li")).ToHaveCountAsync(2);
        }
        finally { release.TrySetResult(); }
        await obsolete;
        await page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(2);
        await Expect(page.Locator("#list-message")).ToHaveTextAsync("");
        await Expect(page.Locator("#next-page")).Not.ToBeVisibleAsync();
        await Expect(page.GetByLabel("Find cases")).ToHaveValueAsync("");
        Assert.DoesNotContain("SYNTHETIC_SENSITIVE_MARKER", await page.Locator("body").InnerTextAsync());
    }

    [Fact]
    public async Task LiteralSearchTextIsSafeKeyboardAccessibleAndNotPersistedInThePageUrl()
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        const string title = "<img src=x onerror=alert('fictional')> Café 😀 %_";
        await session.CreateAsync(title);
        var page = session.Page;
        await session.OpenAsync();
        await page.GetByLabel("Find cases").FocusAsync();
        await page.Keyboard.InsertTextAsync(title);
        await page.Keyboard.PressAsync("Enter");
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(1);
        await Expect(page.Locator("#search-context")).ToHaveTextAsync("Using the submitted title search.");
        await page.Locator("#case-list button").ClickAsync();
        await Expect(page.Locator("#case-title")).ToHaveTextAsync(title);
        Assert.Equal(0, await page.Locator("#case-list img, #case-detail img").CountAsync());
        Assert.Equal("", new Uri(page.Url).Query);
        Assert.Equal(0, await page.EvaluateAsync<int>("localStorage.length + sessionStorage.length"));
        await page.SetViewportSizeAsync(375, 950);
        await page.EvaluateAsync("document.documentElement.style.fontSize = '200%'");
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        Assert.Empty(session.BrowserErrors);
        if (Environment.GetEnvironmentVariable("ECI_WORKSPACE_SCREENSHOTS") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await page.EvaluateAsync("document.documentElement.style.fontSize = ''");
            await page.ScreenshotAsync(new() { Path = Path.Combine(directory, "title-search-mobile.png"), FullPage = true });
        }
    }
}
