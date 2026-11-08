using EnterpriseCaseIntelligence.Api.Tests.Workspace;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace EnterpriseCaseIntelligence.Api.Tests.Security;

[Trait("Category", "Browser")]
public sealed class AuthenticatedBrowserTests(WorkspaceBrowser driver) : IClassFixture<WorkspaceBrowser>
{
    private async Task<(IBrowserContext Context, IPage Page)> Open(SecurityHost host, bool proxy = false, bool configurationCertificate = false)
    {
        await host.Provider.StartBrowserEndpoint(); host.Start(network: true, proxy: proxy,
            configurationOverrides: configurationCertificate ? host.ConfigurationCertificate() : null);
        if (proxy) await host.StartProxyAsync();
        var context = await driver.Browser.NewContextAsync(new() { IgnoreHTTPSErrors = true }); // Generated test certificate only.
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(10_000);
        await page.GotoAsync(new Uri(host.Origin, "/workspace").AbsoluteUri);
        await Expect(page.Locator("#sign-in")).ToBeVisibleAsync();
        await Expect(page.Locator("#protected-workspace")).Not.ToBeVisibleAsync();
        return (context, page);
    }

    private static async Task Login(IPage page)
    {
        await page.Locator("#sign-in").ClickAsync();
        await Expect(page.Locator("#sign-out")).ToBeVisibleAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task WorkerSignsInCreatesProgressesAndSignsOutWithRealCookiesAndCsrf(bool proxy, bool configurationCertificate)
    {
        await using var host = new SecurityHost(); host.Provider.Role = "Caseworker";
        var (context, page) = await Open(host, proxy, configurationCertificate); await using var browserContext = context;
        var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
        await Login(page);
        await Expect(page.Locator("#access-label")).ToHaveTextAsync("CASEWORKER");
        await page.Locator("#new-case").ClickAsync();
        await page.GetByLabel("Title").FillAsync("Authenticated fictional case");
        await page.GetByLabel("Description").FillAsync("Synthetic secure browser journey.");
        await page.Locator("#create-submit").ClickAsync();
        await Expect(page.Locator("#case-title")).ToHaveTextAsync("Authenticated fictional case");
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("Normal");
        var priorityTokens = new List<string>();
        page.Request += (_, request) =>
        {
            if (request.Method == "PUT" && new Uri(request.Url).AbsolutePath.EndsWith("/priority", StringComparison.Ordinal))
                priorityTokens.Add(request.Headers.GetValueOrDefault("x-eci-csrf", ""));
        };
        await page.GetByLabel("Change priority").SelectOptionAsync("High");
        await page.Locator("#save-priority").ClickAsync();
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("High");
        Assert.NotEmpty(Assert.Single(priorityTokens));
        await page.Locator("#advance-case").ClickAsync();
        await Expect(page.Locator("#case-status")).ToHaveTextAsync("In review");
        Assert.Equal(1, host.Database.Count());
        await Expect(page.Locator("#overview-inReview")).ToHaveTextAsync("1");
        await Expect(page.Locator("#overview-total")).ToHaveTextAsync("1");
        if (Environment.GetEnvironmentVariable("ECI_WORKSPACE_SCREENSHOTS") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await Expect(page.Locator("#case-history li")).ToHaveCountAsync(2);
            await page.SetViewportSizeAsync(1440, 1100);
            await page.EvaluateAsync("window.scrollTo(0, 0)");
            await page.ScreenshotAsync(new() { Path = Path.Combine(directory, "authenticated-case-workspace.png"), FullPage = true });
        }
        var heldLogout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLogout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionChecks = 0;
        page.Request += (_, request) => { if (new Uri(request.Url).AbsolutePath == "/auth/session") Interlocked.Increment(ref sessionChecks); };
        await page.RouteAsync("**/auth/logout", async route =>
        {
            heldLogout.TrySetResult(); await releaseLogout.Task.WaitAsync(TimeSpan.FromSeconds(15)); await route.ContinueAsync();
        });
        try
        {
            await page.Locator("#sign-out").ClickAsync();
            await heldLogout.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Expect(page.Locator("#refresh-access")).ToBeDisabledAsync();
            await page.Locator("#refresh-access").DispatchEventAsync("click");
            Assert.Equal(0, sessionChecks);
            await Expect(page.Locator("#case-title")).ToHaveTextAsync("");
            await Expect(page.Locator("#case-priority")).ToHaveTextAsync("");
            await Expect(page.Locator("#priority-choice")).ToHaveValueAsync("");
        }
        finally { releaseLogout.TrySetResult(); }
        await Expect(page.Locator("#access-message")).ToContainTextAsync("Signed out of this application");
        await Expect(page.Locator("#case-title")).ToHaveTextAsync("");
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(0);
        await Expect(page.Locator("#overview-total")).ToHaveTextAsync("");
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("");
        await Expect(page.Locator("#priority-choice")).ToHaveValueAsync("");
        var denied = await context.APIRequest.GetAsync(new Uri(host.Origin, "/cases").AbsoluteUri);
        Assert.Equal(401, denied.Status);
        Assert.Equal(0, await page.EvaluateAsync<int>("localStorage.length + sessionStorage.length"));
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ReaderHasNoMutationControlsAndDirectWritesAreDenied(bool proxy, bool configurationCertificate)
    {
        await using var host = new SecurityHost();
        var record = new EnterpriseCaseIntelligence.Api.Cases.SqliteCaseStore(host.Database.Database).Create("Reader-visible fictional case", "Synthetic description.");
        new EnterpriseCaseIntelligence.Api.Cases.SqliteCaseStore(host.Database.Database).ChangePriority(record.Id, EnterpriseCaseIntelligence.Domain.Cases.CasePriority.High);
        var (context, page) = await Open(host, proxy, configurationCertificate); await using var browserContext = context;
        await Expect(page.Locator("#case-lookup-form")).Not.ToBeVisibleAsync();
        await Login(page);
        await Expect(page.Locator("#access-label")).ToHaveTextAsync("READER");
        await Expect(page.Locator("#overview-new")).ToHaveTextAsync("1");
        await Expect(page.Locator("#overview-total")).ToHaveTextAsync("1");
        await page.GetByLabel("Open case by ID").FillAsync(record.Id.ToString("D"));
        await page.GetByLabel("Open case by ID").PressAsync("Enter");
        await Expect(page.Locator("#case-title")).ToHaveTextAsync("Reader-visible fictional case");
        await Expect(page.Locator("#case-history li")).ToHaveCountAsync(1);
        await Expect(page.Locator("#new-case")).Not.ToBeVisibleAsync();
        await Expect(page.Locator("#advance-case")).Not.ToBeVisibleAsync();
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("High");
        await Expect(page.Locator("#priority-form")).Not.ToBeVisibleAsync();
        await page.GetByLabel("Priority", new() { Exact = true }).SelectOptionAsync("High");
        await Expect(page.Locator("#case-list li")).ToHaveCountAsync(1);
        var priorityStatus = await page.EvaluateAsync<int>("async () => { const session = await (await fetch('/auth/session')).json(); return (await fetch('/cases/" + record.Id.ToString("D") + "/priority', {method:'PUT',headers:{'Content-Type':'application/json','X-ECI-CSRF':session.csrfToken},body:JSON.stringify({priority:'High'})})).status; }");
        Assert.Equal(403, priorityStatus);
        var status = await page.EvaluateAsync<int>("async () => (await fetch('/cases', {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({title:'Forbidden',description:'Fictional'})})).status");
        Assert.Equal(403, status); Assert.Equal(1, host.Database.Count());
        await page.Locator("#sign-out").ClickAsync();
        await Expect(page.Locator("#sign-in")).ToBeVisibleAsync();
        await Expect(page.GetByLabel("Open case by ID")).ToHaveValueAsync("");
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("");
        await Expect(page.Locator("#priority-choice")).ToHaveValueAsync("");
    }

    [Theory]
    [InlineData("Reader", false)]
    [InlineData("Caseworker", true)]
    public async Task AuthorizedClosureBriefClearsOnSignOut(string role, bool proxy)
    {
        await using var host = new SecurityHost();
        host.Provider.Role = role;
        var store = new EnterpriseCaseIntelligence.Api.Cases.SqliteCaseStore(host.Database.Database);
        var id = store.Create("Synthetic closed guide", "Fictional description.").Id;
        store.ChangeStatus(id, Domain.Cases.CaseStatus.InReview);
        store.ChangeStatus(id, Domain.Cases.CaseStatus.Closed);
        var (context, page) = await Open(host, proxy);
        await using var browserContext = context;
        await Expect(page.Locator("#closure-brief")).Not.ToBeVisibleAsync();
        await Login(page);
        await page.Locator($"button[data-id='{id:D}']").ClickAsync();
        await page.Locator("#view-closure-brief").ClickAsync();
        await Expect(page.Locator("#closure-brief-facts dd")).ToHaveCountAsync(3);
        await page.Locator("#sign-out").ClickAsync();
        await Expect(page.Locator("#closure-brief")).Not.ToBeVisibleAsync();
        await Expect(page.Locator("#closure-brief-facts dd")).ToHaveCountAsync(0);
        var denied = await context.APIRequest.GetAsync(new Uri(host.Origin, $"/cases/{id:D}/closure-brief").AbsoluteUri);
        Assert.Equal(401, denied.Status);
    }

    [Fact]
    public async Task ExpiryClearsViewsAndLatePriorSessionResponsesCannotRestoreThem()
    {
        await using var host = new SecurityHost();
        new EnterpriseCaseIntelligence.Api.Cases.SqliteCaseStore(host.Database.Database).Create("Private synthetic title", "Private synthetic description.");
        var (context, page) = await Open(host); await using var browserContext = context;
        await Login(page);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldReads = 0;
        var finishedReads = 0;
        async Task DelayRead(IRoute route)
        {
            var response = await route.FetchAsync();
            if (Interlocked.Increment(ref heldReads) == 2) held.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await route.FulfillAsync(new() { Response = response }); await response.DisposeAsync();
            if (Interlocked.Increment(ref finishedReads) == 2) completed.TrySetResult();
        }
        await page.RouteAsync("**/history", DelayRead);
        await page.RouteAsync("**/priority", DelayRead);
        try
        {
            await page.Locator("#case-list button").ClickAsync();
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Expect(page.Locator("#case-title")).ToHaveTextAsync("Private synthetic title");
            host.Clock.Now = host.Clock.Now.AddMinutes(31);
            await page.Locator("#refresh-list").ClickAsync();
            await Expect(page.Locator("#access-message")).ToContainTextAsync("Sign in to continue");
        }
        finally { release.TrySetResult(); }
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // A subsequent access check also cannot restore protected content.
        await page.Locator("#refresh-access").ClickAsync();
        await Expect(page.Locator("#sign-in")).ToBeVisibleAsync();
        await Expect(page.Locator("#case-title")).ToHaveTextAsync("");
        await Expect(page.Locator("#case-description")).ToHaveTextAsync("");
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("");
        await Expect(page.Locator("#priority-choice")).ToHaveValueAsync("");
        await Expect(page.GetByLabel("Open case by ID")).ToHaveValueAsync("");
        await Expect(page.Locator("#case-history li")).ToHaveCountAsync(0);
        await Expect(page.Locator("#protected-workspace")).Not.ToBeVisibleAsync();
    }

    [Fact]
    public async Task SessionDenialAfterLostCommittedWriteClearsDraftViewWithoutAutomaticReplay()
    {
        await using var host = new SecurityHost(); host.Provider.Role = "Caseworker";
        var (context, page) = await Open(host); await using var browserContext = context;
        await Login(page);
        var writes = 0;
        await page.RouteAsync("**/cases", async route =>
        {
            if (route.Request.Method != "POST") { await route.ContinueAsync(); return; }
            Interlocked.Increment(ref writes);
            var committed = await route.FetchAsync(); Assert.Equal(201, committed.Status);
            host.Clock.Now = host.Clock.Now.AddMinutes(31);
            // Test-only transport loss/denial after a real commit.
            await route.FulfillAsync(new() { Status = 401, ContentType = "application/problem+json", Body = "{}" });
        });
        await page.Locator("#new-case").ClickAsync();
        await page.GetByLabel("Title").FillAsync("Uncertain synthetic case");
        await page.GetByLabel("Description").FillAsync("Fictional lost completion.");
        await page.Locator("#create-submit").ClickAsync();
        await Expect(page.Locator("#access-message")).ToContainTextAsync("may already have committed");
        await Expect(page.Locator("#create-dialog")).Not.ToBeVisibleAsync();
        await Expect(page.Locator("#title")).ToHaveValueAsync("");
        await page.Locator("#refresh-access").ClickAsync();
        await Expect(page.Locator("#sign-in")).ToBeVisibleAsync();
        Assert.Equal(1, writes); Assert.Equal(1, host.Database.Count());
        Assert.Equal(1L, host.Database.Scalar("SELECT count(*) FROM case_creation_keys"));
    }

    [Fact]
    public async Task IdlePageClearsProtectedViewsAtTheAbsoluteSessionDeadline()
    {
        await using var host = new SecurityHost();
        var idleStore = new EnterpriseCaseIntelligence.Api.Cases.SqliteCaseStore(host.Database.Database);
        var idle = idleStore.Create("Idle synthetic case", "Fictional description.");
        idleStore.ChangePriority(idle.Id, EnterpriseCaseIntelligence.Domain.Cases.CasePriority.High);
        var (context, page) = await Open(host); await using var browserContext = context;
        await page.Clock.InstallAsync();
        await Login(page);
        await page.Locator("#case-list button").ClickAsync();
        await Expect(page.Locator("#case-title")).ToHaveTextAsync("Idle synthetic case");
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("High");
        await page.GetByLabel("Open case by ID").FillAsync("not-a-case-id");
        await page.GetByLabel("Open case by ID").PressAsync("Enter");
        await Expect(page.Locator("#case-lookup-error")).ToContainTextAsync("8-4-4-4-12");
        host.Clock.Now = host.Clock.Now.AddMinutes(31);
        await page.Clock.FastForwardAsync((long)TimeSpan.FromMinutes(31).TotalMilliseconds);
        await Expect(page.Locator("#access-message")).ToContainTextAsync("Your session has expired");
        await Expect(page.Locator("#protected-workspace")).Not.ToBeVisibleAsync();
        await Expect(page.Locator("#case-title")).ToHaveTextAsync("");
        await Expect(page.Locator("#sign-in")).ToBeVisibleAsync();
        await Expect(page.GetByLabel("Open case by ID")).ToHaveValueAsync("");
        await Expect(page.Locator("#case-lookup-error")).ToHaveTextAsync("");
        await Expect(page.Locator("#overview-total")).ToHaveTextAsync("");
        await Expect(page.Locator("#case-priority")).ToHaveTextAsync("");
        await Expect(page.Locator("#priority-choice")).ToHaveValueAsync("");
        var denied = await context.APIRequest.GetAsync(new Uri(host.Origin, "/cases").AbsoluteUri); Assert.Equal(401, denied.Status);
    }

    [Fact]
    public async Task UnmappedOidcIdentityGetsOnlyAccessExplanationAndCsrfProtectedSignout()
    {
        await using var host = new SecurityHost(); host.Provider.Role = "Unmapped";
        var (context, page) = await Open(host); await using var browserContext = context;
        await Login(page);
        await Expect(page.Locator("#access-message")).ToContainTextAsync("Access denied");
        await Expect(page.Locator("#protected-workspace")).Not.ToBeVisibleAsync();
        await page.Locator("#sign-out").ClickAsync();
        await Expect(page.Locator("#access-message")).ToContainTextAsync("Signed out of this application");
    }
}
