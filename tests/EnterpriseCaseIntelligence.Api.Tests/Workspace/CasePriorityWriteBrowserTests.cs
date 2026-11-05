using System.Text.Json;
using EnterpriseCaseIntelligence.Domain.Cases;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace EnterpriseCaseIntelligence.Api.Tests.Workspace;

[Trait("Category", "Browser")]
public sealed class CasePriorityWriteBrowserTests(WorkspaceBrowser driver) : IClassFixture<WorkspaceBrowser>
{
    private static async Task Select(IPage page, Guid id)
    {
        await page.GetByLabel("Open case by ID").FillAsync(id.ToString("D"));
        await page.GetByLabel("Open case by ID").PressAsync("Enter");
        await Expect(page.Locator("#priority-form")).ToBeVisibleAsync();
    }

    private static async Task Save(IPage page, string priority)
    {
        await page.GetByLabel("Change priority").SelectOptionAsync(priority);
        await page.Locator("#save-priority").ClickAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PendingPriorityOrStatusWriteBlocksDuplicateAndCompetingActionsAcrossSelections(bool priorityWrite)
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var first = await session.CreateAsync("Pending synthetic choice");
        var second = await session.CreateAsync("Latest selected synthetic case");
        var page = session.Page;
        await session.OpenAsync();
        await Select(page, first);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<string>();
        await page.RouteAsync("**/cases/**", async route =>
        {
            if (route.Request.Method != "PUT") { await route.ContinueAsync(); return; }
            writes.Add(route.Request.Url);
            held.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await route.ContinueAsync();
        });
        try
        {
            if (priorityWrite) await Save(page, "High");
            else await page.Locator("#advance-case").ClickAsync();
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Expect(page.Locator("#save-priority")).ToBeDisabledAsync();
            await Expect(page.Locator("#priority-choice")).ToBeDisabledAsync();
            await Expect(page.Locator("#advance-case")).ToBeDisabledAsync();
            await page.Locator("#priority-form").DispatchEventAsync("submit");
            await page.Locator("#advance-case").DispatchEventAsync("click");
            await Select(page, second);
            await Expect(page.Locator("#case-title")).ToHaveTextAsync("Latest selected synthetic case");
            await Expect(page.Locator("#priority-choice")).ToBeDisabledAsync();
        }
        finally { release.TrySetResult(); }
        await Expect(page.Locator("#mutation-message")).ToContainTextAsync(priorityWrite ? "Priority change confirmed" : "Status change confirmed");
        await Expect(page.Locator("#priority-choice")).ToBeEnabledAsync();
        await Expect(page.Locator("#case-title")).ToHaveTextAsync("Latest selected synthetic case");
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("Normal");
        Assert.Single(writes);
        Assert.Equal(priorityWrite ? CasePriority.High : CasePriority.Normal, session.Store.Priority(first)!.Priority);
        Assert.Equal(priorityWrite ? CaseStatus.New : CaseStatus.InReview, session.Store.Find(first)!.Status);
        Assert.Equal(CasePriority.Normal, session.Store.Priority(second)!.Priority);
        Assert.Empty(session.BrowserErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrLostPriorityResponseRequiresFreshCaseAndPriorityAndNeverAutomaticallyReplays(bool commit)
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var id = await session.CreateAsync("Uncertain priority choice");
        var page = session.Page;
        await session.OpenAsync();
        await Select(page, id);
        var writes = 0;
        var pattern = $"**/cases/{id:D}/priority";
        await page.RouteAsync(pattern, async route =>
        {
            if (route.Request.Method != "PUT") { await route.ContinueAsync(); return; }
            Interlocked.Increment(ref writes);
            if (commit)
            {
                var response = await route.FetchAsync(); Assert.Equal(200, response.Status); await response.DisposeAsync();
                await route.AbortAsync();
            }
            else await route.FulfillAsync(new() { Status = 500, Body = "SYNTHETIC_PRIVATE_ERROR" });
        });
        await Save(page, "High");
        await Expect(page.Locator("#mutation-message")).ToContainTextAsync("could not be confirmed");
        await Expect(page.Locator("#case-detail")).Not.ToBeVisibleAsync();
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("");
        await Expect(page.Locator("#priority-form")).Not.ToBeVisibleAsync();
        Assert.Equal(commit ? CasePriority.High : CasePriority.Normal, session.Store.Priority(id)!.Priority);
        session.Store.ChangePriority(id, CasePriority.Low); // Another explicit choice after the uncertain response.
        await page.Locator("#refresh-priority").ClickAsync();
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("Low");
        await Expect(page.Locator("#priority-form")).Not.ToBeVisibleAsync();
        await page.Locator("#priority-form").DispatchEventAsync("submit");
        Assert.Equal(1, writes);
        await page.Locator("#refresh-case").ClickAsync();
        await Expect(page.Locator("#priority-form")).ToBeVisibleAsync();
        await Expect(page.Locator("#priority-choice")).ToHaveValueAsync("Low");
        await Expect(page.Locator("#case-status")).ToHaveTextAsync("New");
        Assert.Equal(1, writes);
        Assert.Equal(CasePriority.Low, session.Store.Priority(id)!.Priority);
        Assert.Single(session.Store.History(id)!);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_ERROR", await page.Locator("body").InnerTextAsync());
        Assert.Empty(session.BrowserErrors);
    }

    [Fact]
    public async Task ConcurrentCloseRejectsPriorityAndRefreshReconcilesWithoutAnotherWrite()
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var id = await session.CreateAsync("Concurrent Closed priority case");
        var page = session.Page;
        await session.OpenAsync();
        await Select(page, id);
        await session.SetStatusAsync(id, "InReview");
        await session.SetStatusAsync(id, "Closed");
        await Save(page, "High");
        await Expect(page.Locator("#mutation-message")).ToContainTextAsync("case is closed");
        await Expect(page.Locator("#priority-form")).Not.ToBeVisibleAsync();
        await page.Locator("#refresh-case").ClickAsync();
        await Expect(page.Locator("#case-status")).ToHaveTextAsync("Closed");
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("Normal");
        await Expect(page.Locator("#priority-form")).Not.ToBeVisibleAsync();
        Assert.Equal(CasePriority.Normal, session.Store.Priority(id)!.Priority);
        Assert.Equal(3, session.Store.History(id)!.Count);
        Assert.Empty(session.BrowserErrors);
    }

    [Fact]
    public async Task ConfirmedPriorityWriteRemainsConfirmedWhenEveryFollowupReadFails()
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var id = await session.CreateAsync("Confirmed priority with unavailable reads");
        var page = session.Page;
        await session.OpenAsync();
        await Select(page, id);
        var failReads = false;
        await page.RouteAsync("**/cases**", async route =>
        {
            if (route.Request.Method == "PUT")
            {
                var response = await route.FetchAsync(); failReads = true;
                await route.FulfillAsync(new() { Response = response }); await response.DisposeAsync();
            }
            else if (failReads) await route.FulfillAsync(new() { Status = 500, Body = "SYNTHETIC_PRIVATE_ERROR" });
            else await route.ContinueAsync();
        });
        await Save(page, "High");
        await Expect(page.Locator("#mutation-message")).ToContainTextAsync("Priority change confirmed");
        foreach (var message in new[] { "priority-message", "detail-message", "history-message", "list-message", "overview-message" })
            await Expect(page.Locator("#" + message)).ToContainTextAsync("could not be loaded");
        await Expect(page.Locator("#priority-form")).Not.ToBeVisibleAsync();
        Assert.Equal(CasePriority.High, session.Store.Priority(id)!.Priority);
        Assert.Single(session.Store.History(id)!);
        await page.UnrouteAsync("**/cases**");
        await page.Locator("#refresh-case").ClickAsync();
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("High");
        await Expect(page.Locator("#priority-form")).ToBeVisibleAsync();
        await Expect(page.Locator("#mutation-message")).ToContainTextAsync("Priority change confirmed");
        Assert.Empty(session.BrowserErrors);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UncertainPriorityOrStatusWriteInvalidatesAlreadyPendingCurrentReads(bool priorityWrite)
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var id = await session.CreateAsync("Pending old reads around uncertain mutation");
        var page = session.Page;
        await session.OpenAsync();
        await Select(page, id);
        var writeHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/cases/**", async route =>
        {
            if (route.Request.Method == "PUT")
            {
                writeHeld.TrySetResult(); await commit.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var response = await route.FetchAsync(); await response.DisposeAsync();
                await route.AbortAsync();
            }
            else if (new Uri(route.Request.Url).AbsolutePath.EndsWith("/priority", StringComparison.Ordinal))
            {
                var response = await route.FetchAsync(); readHeld.TrySetResult();
                await releaseRead.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await route.FulfillAsync(new() { Response = response }); await response.DisposeAsync();
            }
            else await route.ContinueAsync();
        });
        var done = page.WaitForResponseAsync(response => new Uri(response.Url).AbsolutePath == $"/cases/{id:D}/priority");
        try
        {
            if (priorityWrite) await Save(page, "High");
            else await page.Locator("#advance-case").ClickAsync();
            await writeHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await page.Locator("#refresh-case").ClickAsync();
            await readHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));
            commit.TrySetResult();
            await Expect(page.Locator("#mutation-message")).ToContainTextAsync("could not be confirmed");
        }
        finally { commit.TrySetResult(); releaseRead.TrySetResult(); }
        await done;
        await page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("");
        await Expect(page.Locator("#priority-form")).Not.ToBeVisibleAsync();
        await Expect(page.Locator("#case-detail")).Not.ToBeVisibleAsync();
        await page.UnrouteAsync("**/cases/**");
        await page.Locator("#refresh-case").ClickAsync();
        await Expect(page.Locator("#priority-form")).ToBeVisibleAsync();
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync(priorityWrite ? "High" : "Normal");
        await Expect(page.Locator("#case-status")).ToHaveTextAsync(priorityWrite ? "New" : "In review");
        Assert.Empty(session.BrowserErrors);
    }

    [Fact]
    public async Task PriorityActionsPreserveFrozenCreationRetryAndReplayReadsTheCurrentClosedPriority()
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var existing = await session.CreateAsync("Existing synthetic priority case");
        var page = session.Page;
        var payloads = new List<string>();
        var keys = new List<string>();
        Guid created = default;
        await page.RouteAsync("**/cases", async route =>
        {
            if (route.Request.Method != "POST") { await route.ContinueAsync(); return; }
            payloads.Add(route.Request.PostData!); keys.Add(route.Request.Headers["idempotency-key"]);
            if (payloads.Count == 1)
            {
                var response = await route.FetchAsync();
                created = (await response.JsonAsync())!.Value.GetProperty("id").GetGuid();
                await response.DisposeAsync();
                session.Store.ChangePriority(created, CasePriority.High);
                await session.SetStatusAsync(created, "InReview"); await session.SetStatusAsync(created, "Closed");
                await route.AbortAsync();
            }
            else await route.ContinueAsync();
        });
        await session.OpenAsync();
        await page.Locator("#new-case").ClickAsync();
        await page.GetByLabel("Title").FillAsync("Frozen priority creation");
        await page.GetByLabel("Description").FillAsync("Fictional retained payload");
        await page.Locator("#create-submit").ClickAsync();
        await Expect(page.Locator("#create-message")).ToContainTextAsync("could not be confirmed");
        await page.Locator("#close-draft").ClickAsync();
        await Select(page, existing);
        await Save(page, "High");
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("High");
        await page.GetByLabel("Priority", new() { Exact = true }).SelectOptionAsync("High");
        await page.Locator("#refresh-priority").ClickAsync();
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("High");
        await page.Locator("#new-case").ClickAsync();
        await Expect(page.Locator("#title")).ToHaveAttributeAsync("readonly", "");
        await Expect(page.Locator("#title")).ToHaveValueAsync("Frozen priority creation");
        await page.Locator("#create-submit").ClickAsync();
        await Expect(page.Locator("#case-id")).ToHaveTextAsync(created.ToString("D"));
        await Expect(page.Locator("#case-status")).ToHaveTextAsync("Closed");
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("High");
        await Expect(page.Locator("#priority-form")).Not.ToBeVisibleAsync();
        Assert.Equal(2, payloads.Count);
        Assert.Equal(payloads[0], payloads[1]); Assert.Equal(keys[0], keys[1]);
        Assert.Equal(2, session.Database.Count());
        Assert.Equal(1L, session.Database.Scalar("SELECT count(*) FROM case_creation_keys"));
        Assert.Equal(3, session.Store.History(created)!.Count);
        Assert.Empty(session.BrowserErrors);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task AccessLossClearsPriorityAndLateResponsesCannotRestoreItOrWrite(int status)
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var id = await session.CreateAsync("Protected synthetic priority");
        session.Store.ChangePriority(id, CasePriority.High);
        var page = session.Page;
        await session.OpenAsync();
        await Select(page, id);
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("High");
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/priority", async route =>
        {
            var response = await route.FetchAsync(); held.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await route.FulfillAsync(new() { Response = response }); await response.DisposeAsync();
        });
        var done = page.WaitForResponseAsync(response => new Uri(response.Url).AbsolutePath.EndsWith("/priority", StringComparison.Ordinal));
        try
        {
            await page.Locator("#refresh-priority").ClickAsync();
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await page.RouteAsync("**/cases", route => route.FulfillAsync(new() { Status = status, Body = "SYNTHETIC_PRIVATE_ERROR" }));
            await page.Locator("#refresh-list").ClickAsync();
            await Expect(page.Locator("#protected-workspace")).Not.ToBeVisibleAsync();
        }
        finally { release.TrySetResult(); }
        await done;
        await page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("");
        await Expect(page.Locator("#priority-choice")).ToHaveValueAsync("");
        await Expect(page.Locator("#priority-form")).Not.ToBeVisibleAsync();
        var writes = 0;
        page.Request += (_, request) => { if (request.Method == "PUT") Interlocked.Increment(ref writes); };
        await page.Locator("#priority-form").DispatchEventAsync("submit");
        Assert.Equal(0, writes);
        Assert.Empty(session.BrowserErrors);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task DenialAfterCommittedPriorityWriteClearsProtectedChoiceWithoutReplay(int status)
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var id = await session.CreateAsync("Denied priority completion");
        var page = session.Page;
        await session.OpenAsync();
        await Select(page, id);
        var writes = 0;
        await page.RouteAsync("**/priority", async route =>
        {
            if (route.Request.Method != "PUT") { await route.ContinueAsync(); return; }
            Interlocked.Increment(ref writes);
            var response = await route.FetchAsync(); Assert.Equal(200, response.Status); await response.DisposeAsync();
            await route.FulfillAsync(new() { Status = status, Body = "SYNTHETIC_PRIVATE_ERROR" });
        });
        await Save(page, "High");
        await Expect(page.Locator("#protected-workspace")).Not.ToBeVisibleAsync();
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("");
        await Expect(page.Locator("#priority-choice")).ToHaveValueAsync("");
        await Expect(page.Locator("#mutation-message")).ToHaveTextAsync("");
        await page.Locator("#priority-form").DispatchEventAsync("submit");
        Assert.Equal(1, writes);
        Assert.Equal(CasePriority.High, session.Store.Priority(id)!.Priority);
        Assert.Empty(session.BrowserErrors);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PendingWriteAcrossAccessRefreshRequiresFreshStateForTheSameReselectedCase(bool priorityWrite)
    {
        await using var session = await BrowserSession.StartAsync(driver.Browser);
        var id = await session.CreateAsync("Access refreshed during pending write");
        var page = session.Page;
        await session.OpenAsync();
        await Select(page, id);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/cases/**", async route =>
        {
            if (route.Request.Method != "PUT") { await route.ContinueAsync(); return; }
            held.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await route.ContinueAsync();
        });
        var done = page.WaitForResponseAsync(response => response.Request.Method == "PUT");
        try
        {
            if (priorityWrite) await Save(page, "High");
            else await page.Locator("#advance-case").ClickAsync();
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await page.Locator("#refresh-access").ClickAsync();
            await Expect(page.Locator("#case-priority")).ToHaveTextAsync("");
            await Select(page, id);
            await Expect(page.Locator("#case-priority")).ToHaveTextAsync("Normal");
            await Expect(page.Locator("#priority-choice")).ToBeDisabledAsync();
        }
        finally { release.TrySetResult(); }
        await done;
        await Expect(page.Locator("#detail-message")).ToContainTextAsync("before another action");
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("");
        await Expect(page.Locator("#priority-form")).Not.ToBeVisibleAsync();
        await Expect(page.Locator("#mutation-message")).ToHaveTextAsync("");
        await page.Locator("#refresh-case").ClickAsync();
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync(priorityWrite ? "High" : "Normal");
        await Expect(page.Locator("#case-status")).ToHaveTextAsync(priorityWrite ? "New" : "In review");
        await Expect(page.Locator("#priority-choice")).ToBeEnabledAsync();
        Assert.Empty(session.BrowserErrors);
    }
}
