using System.Collections.Concurrent;
using EnterpriseCaseIntelligence.Domain.Cases;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace EnterpriseCaseIntelligence.Api.Tests.Workspace;

[Trait("Category", "Browser")]
public sealed class CaseClosureBriefBrowserTests(WorkspaceBrowser driver) : IClassFixture<WorkspaceBrowser>
{
    [Fact]
    public async Task ExplicitReadShowsThreeFactsAndCitationsFocusTheirSources()
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var id = await CloseAsync(session, "Synthetic closed guide");
        var page = session.Page;
        await page.SetViewportSizeAsync(390, 844);
        var requests = new ConcurrentQueue<string>();
        page.Request += (_, request) =>
        {
            if (new Uri(request.Url).AbsolutePath.EndsWith("/closure-brief", StringComparison.Ordinal))
                requests.Enqueue(request.Method);
        };
        await session.OpenAsync();
        await Select(page, id);
        await Expect(page.Locator("#case-status")).ToHaveTextAsync("Closed");
        await Expect(page.Locator("#case-history-entry-3")).ToContainTextAsync("In review → Closed");
        await Expect(page.Locator("#closure-brief-facts")).Not.ToBeVisibleAsync();
        Assert.Empty(requests);
        await page.GetByRole(AriaRole.Button, new() { Name = "View closure brief" }).PressAsync("Enter");
        await Expect(page.Locator("#closure-brief-facts dt")).ToHaveTextAsync(["Current status", "Previous status", "Recorded closure time"]);
        await Expect(page.Locator("#closure-brief-facts dd").Last)
            .ToContainTextAsync(session.Store.History(id)![^1].RecordedAtUtc + " (UTC)");
        Assert.Equal("GET", Assert.Single(requests));

        var facts = page.Locator("#closure-brief-facts");
        var caseLink = facts.GetByRole(AriaRole.Link, new() { Name = "Case status" });
        await Expect(caseLink).ToHaveAttributeAsync("href", $"/cases/{id:D}");
        await caseLink.PressAsync("Enter");
        await Expect(page.Locator("#case-status")).ToBeFocusedAsync();

        var historyLinks = facts.GetByRole(AriaRole.Link, new() { Name = "History entry 3" });
        await Expect(historyLinks).ToHaveCountAsync(2);
        await Expect(historyLinks.Last).ToHaveAttributeAsync("href", $"/cases/{id:D}/history");
        await historyLinks.Last.PressAsync("Enter");
        await Expect(page.Locator("#case-history-entry-3")).ToBeFocusedAsync();
        Assert.Equal("/workspace", new Uri(page.Url).AbsolutePath);
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        Assert.Empty(session.BrowserErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedBriefCannotRestoreAFormerSelectionOrAnEarlierReadOfTheSameCase(bool reselect)
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var closed = await CloseAsync(session, "Synthetic old selection");
        var open = await session.CreateAsync("Synthetic new selection");
        var page = session.Page;
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var pattern = $"**/cases/{closed:D}/closure-brief";
        await page.RouteAsync(pattern, async route =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                await using var response = await route.FetchAsync();
                held.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await route.FulfillAsync(new() { Response = response });
            }
            else await route.FulfillAsync(new() { Status = 503, Body = "SYNTHETIC_SENSITIVE_MARKER" });
        });
        try
        {
            await session.OpenAsync();
            await Select(page, closed);
            await page.Locator("#view-closure-brief").ClickAsync();
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Select(page, open);
            if (reselect) await Select(page, closed);
            await page.Locator("#view-closure-brief").ClickAsync();
            var text = reselect ? "could not be verified" : "This case is open";
            await Expect(page.Locator("#closure-brief-message")).ToContainTextAsync(text);
            var delayed = page.WaitForResponseAsync(response => response.Url.EndsWith($"/{closed:D}/closure-brief", StringComparison.Ordinal) && response.Status == 200);
            release.TrySetResult();
            await (await delayed).FinishedAsync();
            await page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
            await Expect(page.Locator("#closure-brief-message")).ToContainTextAsync(text);
            await Expect(page.Locator("#closure-brief-facts dd")).ToHaveCountAsync(0);
            Assert.Empty(session.BrowserErrors);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task LegacyClosedBaselineExplainsWhyItHasNoClosureTime()
    {
        Guid id = default;
        await using var session = await BrowserSession.StartAsync(driver.Browser, database =>
        {
            id = LegacyCaseStore.Initialize(database).Single(record => record.Status == CaseStatus.Closed).Id;
            database.Database.Upgrade();
        });
        await session.OpenAsync();
        await Select(session.Page, id);
        await session.Page.Locator("#view-closure-brief").ClickAsync();
        await Expect(session.Page.Locator("#closure-brief-message")).ToContainTextAsync("No closure transition or closure time was recorded");
        await Expect(session.Page.Locator("#closure-brief-facts dd")).ToHaveCountAsync(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnverifiableBriefClearsFactsAndHidesRawFailureContent(bool wrongCase)
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var id = await CloseAsync(session, "Synthetic unreadable brief");
        var page = session.Page;
        await session.OpenAsync();
        await Select(page, id);
        await page.Locator("#view-closure-brief").ClickAsync();
        await Expect(page.Locator("#closure-brief-facts dd")).ToHaveCountAsync(3);
        if (wrongCase)
        {
            await page.RouteAsync("**/closure-brief", async route =>
            {
                await using var response = await route.FetchAsync();
                var body = (await response.TextAsync()).Replace(id.ToString("D"), Guid.Empty.ToString("D"), StringComparison.Ordinal);
                await route.FulfillAsync(new() { Response = response, Body = body });
            });
        }
        else session.Database.Execute("UPDATE case_status_history SET recorded_at_utc='SYNTHETIC_SENSITIVE_MARKER' WHERE sequence=3");

        await page.Locator("#view-closure-brief").ClickAsync();
        await Expect(page.Locator("#closure-brief-message")).ToContainTextAsync("could not be verified");
        await Expect(page.Locator("#closure-brief-facts dd")).ToHaveCountAsync(0);
        await Expect(page.Locator("#protected-workspace")).Not.ToContainTextAsync("SYNTHETIC_SENSITIVE_MARKER");
        Assert.Empty(session.BrowserErrors);
    }

    private static Task Select(IPage page, Guid id) => page.Locator($"button[data-id='{id:D}']").ClickAsync();

    private static async Task<Guid> CloseAsync(BrowserSession session, string title)
    {
        var id = await session.CreateAsync(title);
        await session.SetStatusAsync(id, "InReview");
        await session.SetStatusAsync(id, "Closed");
        return id;
    }
}
