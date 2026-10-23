using System.Net;
using System.Net.Http.Json;
using EnterpriseCaseIntelligence.Api.Cases;
using Microsoft.Playwright;

namespace EnterpriseCaseIntelligence.Api.Tests.Workspace;

public sealed class WorkspaceBrowser : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null) await Browser.CloseAsync();
        _playwright?.Dispose();
    }
}

internal sealed class BrowserSession : IAsyncDisposable
{
    public TemporaryCaseDatabase Database { get; }
    public ApiProcess Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public IBrowserContext Context { get; private set; } = null!;
    public IPage Page { get; private set; } = null!;
    public List<string> BrowserErrors { get; } = [];

    private BrowserSession(TemporaryCaseDatabase database) => Database = database;

    public static async Task<BrowserSession> StartAsync(IBrowser browser, Action<TemporaryCaseDatabase>? prepare = null)
    {
        var session = new BrowserSession(new(initialize: prepare is null));
        try
        {
            prepare?.Invoke(session.Database);
            session.Host = await ApiProcess.StartAsync(session.Database.Path);
            session.Client = new() { BaseAddress = session.Host.Address };
            session.Context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1100 } });
            session.Page = await session.Context.NewPageAsync();
            session.Page.SetDefaultTimeout(10_000);
            session.Page.PageError += (_, error) => session.BrowserErrors.Add(error);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    public async Task OpenAsync() => await Page.GotoAsync(new Uri(Host.Address, "/workspace").ToString());

    public async Task<Guid> CreateAsync(string title, string description = "Fictional documentation correction.")
    {
        using var response = await Client.PostAsJsonAsync("/cases", new { title, description });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CaseValue>())!.Id;
    }

    public async Task SetStatusAsync(Guid id, string status)
    {
        using var response = await Client.PutAsJsonAsync($"/cases/{id:D}/status", new { status });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public SqliteCaseStore Store => new(Database.Database);

    public async ValueTask DisposeAsync()
    {
        if (Context is not null) await Context.CloseAsync();
        Client?.Dispose();
        Host?.Dispose();
        Database.Dispose();
    }

    private sealed record CaseValue(Guid Id);
}
