using System.Text.Json;
using EnterpriseCaseIntelligence.Domain.Cases;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace EnterpriseCaseIntelligence.Api.Tests.Workspace;

[Trait("Category", "Browser")]
public sealed class CasePriorityBrowserTests(WorkspaceBrowser driver) : IClassFixture<WorkspaceBrowser>
{
    private static async Task Select(IPage page, Guid id)
    {
        await page.GetByLabel("Open case by ID").FillAsync(id.ToString("D"));
        await page.GetByLabel("Open case by ID").PressAsync("Enter");
        await Expect(page.Locator("#case-id")).ToHaveTextAsync(id.ToString("D"));
    }

    private static async Task Choose(IPage page, string value)
    {
        await Expect(page.Locator("#priority-form")).ToBeVisibleAsync();
        await page.GetByLabel("Change priority", new() { Exact = true }).SelectOptionAsync(value);
        await page.GetByRole(AriaRole.Button, new() { Name = "Save priority", Exact = true }).ClickAsync();
    }

    [Fact]
    public async Task FilterCombinesBeforePagingAndPersistsAcrossLookupRefreshAndMutationsWithoutPerRowReads()
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var ids = new List<Guid>();
        for (var i = 0; i < 24; i++)
        {
            var id = await session.CreateAsync($"Needle synthetic {i:D2}");
            ids.Add(id);
            session.Store.ChangePriority(id, CasePriority.High);
        }
        var unrelated = await session.CreateAsync("Different title");
        var ordered = ids.OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToArray();
        var page = session.Page;
        var lists = new List<Uri>();
        var priorityReads = new List<string>();
        var summaries = 0;
        page.Request += (_, request) =>
        {
            var uri = new Uri(request.Url);
            if (uri.AbsolutePath == "/cases" && request.Method == "GET") lists.Add(uri);
            if (uri.AbsolutePath.EndsWith("/priority", StringComparison.Ordinal) && request.Method == "GET") priorityReads.Add(uri.AbsolutePath);
            if (uri.AbsolutePath == "/cases/summary") Interlocked.Increment(ref summaries);
        };
        await session.OpenAsync();
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(20);
        Assert.Empty(priorityReads);
        await page.GetByLabel("Find cases").FillAsync("Needle");
        await page.GetByLabel("Find cases").PressAsync("Enter");
        await page.GetByLabel("Status", new() { Exact = true }).SelectOptionAsync("New");
        await page.GetByLabel("Priority", new() { Exact = true }).SelectOptionAsync("High");
        await Expect(page.Locator("#next-page")).ToBeVisibleAsync();
        await page.Locator("#next-page").ClickAsync();
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(4);
        Assert.Contains("after=", lists[^1].Query);
        Assert.Contains("priority=High", lists[^1].Query);
        Assert.Contains("status=New", lists[^1].Query);
        Assert.Contains("title=Needle", lists[^1].Query);
        Assert.Empty(priorityReads);
        var beforeLookup = lists.Count;
        await Select(page, unrelated);
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("Normal");
        Assert.Equal(beforeLookup, lists.Count);
        Assert.Equal(new[] { $"/cases/{unrelated:D}/priority" }, priorityReads);
        await page.Locator("#refresh-list").ClickAsync();
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(20);
        Assert.DoesNotContain("after=", lists[^1].Query);
        await Select(page, ordered[^1]);
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("High");
        await Choose(page, "Low");
        await Expect(page.Locator("#mutation-message")).ToContainTextAsync("Priority change confirmed");
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("Low");
        Assert.Contains("priority=High", lists[^1].Query);
        Assert.DoesNotContain("after=", lists[^1].Query);
        Assert.Single(session.Store.History(ordered[^1])!);
        await page.Locator("#advance-case").ClickAsync();
        await Expect(page.Locator("#case-status")).ToHaveTextAsync("In review");
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("Low");
        Assert.Contains("priority=High", lists[^1].Query);
        Assert.Contains("status=New", lists[^1].Query);
        await page.GetByLabel("Priority", new() { Exact = true }).SelectOptionAsync("Low");
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(0);
        await page.GetByLabel("Status", new() { Exact = true }).SelectOptionAsync("InReview");
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(1);
        await page.GetByLabel("Priority", new() { Exact = true }).SelectOptionAsync("");
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(1);
        Assert.DoesNotContain("priority=", lists[^1].Query);
        Assert.Equal(3, summaries); // Access bootstrap plus the two confirmed writes; filters/lookup never reload totals.
        Assert.Equal("25", await page.Locator("#overview-total").InnerTextAsync());
        Assert.Empty(session.BrowserErrors);
    }

    [Fact]
    public async Task CreationLoadsNormalAndPrioritySaveRefreshesTheFilteredListWithoutInventingStatusHistory()
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var page = session.Page;
        await session.OpenAsync();
        await page.GetByLabel("Priority", new() { Exact = true }).SelectOptionAsync("Normal");
        await page.Locator("#new-case").ClickAsync();
        await page.GetByLabel("Title").FillAsync("Synthetic priority creation");
        await page.GetByLabel("Description").FillAsync("Fictional triage choice.");
        await page.Locator("#create-submit").ClickAsync();
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("Normal");
        await Expect(page.Locator("#save-priority")).ToBeDisabledAsync();
        var id = Guid.Parse(await page.Locator("#case-id").InnerTextAsync());
        await Choose(page, "High");
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("High");
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(0);
        await Expect(page.Locator("#case-status")).ToHaveTextAsync("New");
        await Expect(page.Locator("#case-history li")).ToHaveCountAsync(1);
        Assert.Equal(CasePriority.High, session.Store.Priority(id)!.Priority);
        await page.Locator("#advance-case").ClickAsync();
        await Expect(page.Locator("#case-status")).ToHaveTextAsync("In review");
        await page.Locator("#advance-case").ClickAsync();
        await Expect(page.Locator("#case-status")).ToHaveTextAsync("Closed");
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("High");
        await Expect(page.Locator("#priority-form")).Not.ToBeVisibleAsync();
        await Expect(page.Locator("#priority-section")).ToContainTextAsync("no recorded history or actor identity");
        Assert.Equal(1L, session.Database.Scalar("SELECT count(*) FROM case_creation_keys"));
        Assert.Equal(3, session.Store.History(id)!.Count);
        Assert.Empty(session.BrowserErrors);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothCurrentReadsAreRequiredBeforeEditingAndReadFailuresRecoverIndependently(bool failPriority)
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var id = await session.CreateAsync("Independent priority reads");
        session.Store.ChangePriority(id, CasePriority.High);
        var page = session.Page;
        var pattern = $"**/cases/{id:D}" + (failPriority ? "/priority" : "");
        await page.RouteAsync(pattern, route => route.FulfillAsync(new() { Status = 500, Body = "<script>SYNTHETIC_PRIVATE_ERROR</script>" }));
        await session.OpenAsync();
        await page.Locator("#case-list button").ClickAsync();
        await Expect(page.Locator(failPriority ? "#priority-message" : "#detail-message")).ToContainTextAsync("could not be loaded");
        await Expect(page.Locator("#priority-form")).Not.ToBeVisibleAsync();
        await Expect(page.Locator("#case-history li")).ToHaveCountAsync(1);
        if (failPriority)
        {
            await Expect(page.Locator("#case-title")).ToHaveTextAsync("Independent priority reads");
            await Expect(page.Locator("#case-priority")).ToHaveTextAsync("");
            await Expect(page.Locator("#priority-value")).Not.ToBeVisibleAsync();
            await Expect(page.Locator("#advance-case")).ToBeDisabledAsync();
        }
        else await Expect(page.Locator("#case-priority")).ToHaveTextAsync("High");
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_ERROR", await page.Locator("body").InnerTextAsync());
        await page.UnrouteAsync(pattern);
        await page.Locator(failPriority ? "#refresh-priority" : "#refresh-case").ClickAsync();
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("High");
        await Expect(page.Locator("#priority-form")).ToBeVisibleAsync();
        await Expect(page.Locator("#advance-case")).ToBeEnabledAsync();
        Assert.Empty(session.BrowserErrors);
    }

    [Theory]
    [InlineData("bad-case")]
    [InlineData("bad-priority")]
    [InlineData("extra")]
    public async Task InvalidPriorityResponsesNeverDisplayRawValuesOrSupplyADefault(string fault)
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var id = await session.CreateAsync("Invalid priority response");
        var page = session.Page;
        var body = fault switch
        {
            "bad-case" => JsonSerializer.Serialize(new { caseId = Guid.NewGuid(), priority = "High" }),
            "bad-priority" => JsonSerializer.Serialize(new { caseId = id, priority = "<img src=x onerror=alert('SYNTHETIC_PRIVATE_ERROR')>" }),
            _ => JsonSerializer.Serialize(new { caseId = id, priority = "High", unexpected = true })
        };
        await page.RouteAsync($"**/cases/{id:D}/priority", route => route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = body }));
        await session.OpenAsync();
        await page.Locator("#case-list button").ClickAsync();
        await Expect(page.Locator("#priority-message")).ToContainTextAsync("could not be loaded");
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("");
        await Expect(page.Locator("#priority-form")).Not.ToBeVisibleAsync();
        Assert.Equal(0, await page.Locator("#priority-section img").CountAsync());
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_ERROR", await page.Locator("body").InnerTextAsync());
        Assert.Empty(session.BrowserErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LatePrioritySuccessOrFailureCannotReplaceANewerRefreshOrSelection(bool fail)
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var first = await session.CreateAsync("Earlier priority selection");
        var second = await session.CreateAsync("Latest priority selection");
        session.Store.ChangePriority(second, CasePriority.High);
        var page = session.Page;
        await session.OpenAsync();
        await Select(page, first);
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("Normal");
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        await page.RouteAsync("**/priority", async route =>
        {
            if (route.Request.Method != "GET" || Interlocked.Increment(ref requests) != 1) { await route.ContinueAsync(); return; }
            var response = await route.FetchAsync(); held.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (fail) await route.FulfillAsync(new() { Status = 500, Body = "SYNTHETIC_PRIVATE_ERROR" });
            else await route.FulfillAsync(new() { Response = response });
            await response.DisposeAsync();
        });
        var done = page.WaitForResponseAsync(response => new Uri(response.Url).AbsolutePath == $"/cases/{first:D}/priority");
        try
        {
            await page.Locator("#refresh-priority").ClickAsync();
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Expect(page.Locator("#case-priority")).ToHaveTextAsync("");
            await Expect(page.Locator("#priority-form")).Not.ToBeVisibleAsync();
            session.Store.ChangePriority(first, CasePriority.Low);
            await page.Locator("#refresh-priority").ClickAsync();
            await Expect(page.Locator("#case-priority")).ToHaveTextAsync("Low");
            await Select(page, second);
            await Expect(page.Locator("#case-priority")).ToHaveTextAsync("High");
        }
        finally { release.TrySetResult(); }
        await done;
        await page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("High");
        await Expect(page.Locator("#priority-message")).ToHaveTextAsync("");
        Assert.Empty(session.BrowserErrors);
    }

    [Fact]
    public async Task KeyboardMobileZoomAndLocalOnlyRequestsSupportExplicitPriorityChoice()
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var id = await session.CreateAsync("Synthetic keyboard priority");
        var page = session.Page;
        var requests = new List<string>();
        page.Request += (_, request) => requests.Add(request.Url);
        await session.OpenAsync();
        await page.GetByLabel("Open case by ID").FocusAsync();
        await page.Keyboard.TypeAsync(id.ToString("D"));
        await page.Keyboard.PressAsync("Enter");
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("Normal");
        await Expect(page.Locator("#priority-form")).ToBeVisibleAsync();
        await page.GetByLabel("Change priority").FocusAsync();
        await Expect(page.GetByLabel("Change priority")).ToBeFocusedAsync();
        // Native letter navigation selects High; Tab reaches the explicit Save action.
        await page.Keyboard.PressAsync("h");
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("#priority-choice")).ToHaveValueAsync("High");
        await Expect(page.Locator("#save-priority")).ToBeFocusedAsync();
        Assert.NotEqual("none", await page.Locator("#save-priority").EvaluateAsync<string>("e => getComputedStyle(e).outlineStyle"));
        await page.Keyboard.PressAsync("Enter");
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("High");
        await page.SetViewportSizeAsync(375, 950);
        await page.EvaluateAsync("document.documentElement.style.fontSize = '200%'");
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        await Expect(page.GetByLabel("Priority", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByLabel("Change priority")).ToBeVisibleAsync();
        Assert.Equal(0, await page.EvaluateAsync<int>("localStorage.length + sessionStorage.length"));
        Assert.Equal("", new Uri(page.Url).Query);
        Assert.Equal("", new Uri(page.Url).Fragment);
        Assert.All(requests, url => Assert.Equal(session.Host.Address.Authority, new Uri(url).Authority));
        if (Environment.GetEnvironmentVariable("ECI_WORKSPACE_SCREENSHOTS") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await page.EvaluateAsync("window.scrollTo(0, 0)");
            await page.ScreenshotAsync(new() { Path = Path.Combine(directory, "case-priority-mobile.png"), FullPage = true });
            await page.EvaluateAsync("document.documentElement.style.fontSize = ''");
            await page.SetViewportSizeAsync(1440, 1100);
            await page.EvaluateAsync("window.scrollTo(0, 0)");
            await page.ScreenshotAsync(new() { Path = Path.Combine(directory, "case-priority-desktop.png"), FullPage = true });
        }
        Assert.Empty(session.BrowserErrors);
    }
}
