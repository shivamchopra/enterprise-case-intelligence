using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EnterpriseCaseIntelligence.Api.Cases;
using EnterpriseCaseIntelligence.Domain.Cases;

namespace EnterpriseCaseIntelligence.Api.Tests.Security;

public sealed class CaseSecurityTests
{
    private sealed record CaseValue(Guid Id, string Title, string Description, string Status);
    private static readonly string Id = Guid.NewGuid().ToString("D");
    public static TheoryData<string, string> Routes => new()
    {
        { "GET", "/cases?limit=bad" }, { "GET", "/cases/invalid" }, { "GET", $"/cases/{Id}/history" },
        { "POST", "/cases" }, { "PUT", $"/cases/{Id}/status" }
    };
    private static HttpRequestMessage Malformed(string method, string path) => new(new(method), path)
    { Content = method is "POST" or "PUT" ? new StringContent("malformed", Encoding.UTF8, "text/plain") : null };
    private static async Task Denial(HttpResponseMessage response, int status)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("about:blank", body.RootElement.GetProperty("type").GetString());
        Assert.Equal(status == 401 ? "authentication_required" : "access_denied", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(4, body.RootElement.EnumerateObject().Count());
    }
    internal static async Task Csrf(HttpClient client)
    {
        using var session = await client.GetAsync("/auth/session");
        session.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Remove("X-ECI-CSRF");
        client.DefaultRequestHeaders.Add("X-ECI-CSRF", json.RootElement.GetProperty("csrfToken").GetString());
    }

    [Theory, MemberData(nameof(Routes))]
    public async Task AnonymousDeniedBeforeInputOrStorage(string method, string path)
    {
        await using var host = new SecurityHost(); host.Start();
        using var client = host.CreateClient();
        File.Delete(host.Database.Path); // A denial must not need the store.
        using var response = await client.SendAsync(Malformed(method, path));
        await Denial(response, 401);
        Assert.False(File.Exists(host.Database.Path));
    }

    [Theory, MemberData(nameof(Routes))]
    public async Task UnmappedIdentityGetsNoCasePermission(string method, string path)
    {
        await using var host = new SecurityHost(); host.Start();
        using var client = host.CreateClient(host.Ticket("Unknown"));
        using var response = await client.SendAsync(Malformed(method, path));
        await Denial(response, 403);
    }

    [Theory]
    [InlineData("POST", "/cases")]
    [InlineData("PUT", "/cases/invalid/status")]
    public async Task ReaderCannotWriteEvenWithValidCsrfAndMalformedInput(string method, string path)
    {
        await using var host = new SecurityHost(); host.Start();
        using var client = host.CreateClient(host.Ticket("Reader")); await Csrf(client);
        using var response = await client.SendAsync(Malformed(method, path));
        await Denial(response, 403); Assert.Equal(0, host.Database.Count());
    }

    [Theory]
    [InlineData("POST", "/cases")]
    [InlineData("PUT", "/cases/invalid/status")]
    [InlineData("POST", "/auth/logout")]
    public async Task AuthorizedWriteRequiresCsrfBeforeBinding(string method, string path)
    {
        await using var host = new SecurityHost(); host.Start();
        using var client = host.CreateClient(host.Ticket("Caseworker"));
        foreach (var token in new string?[] { null, "forged-token" })
        {
            client.DefaultRequestHeaders.Remove("X-ECI-CSRF");
            if (token is not null) client.DefaultRequestHeaders.Add("X-ECI-CSRF", token);
            using var response = await client.SendAsync(Malformed(method, path));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("validation_failed", body.RootElement.GetProperty("code").GetString());
            Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("csrf", out _));
        }
        Assert.Equal(0, host.Database.Count());
    }

    [Fact]
    public async Task WorkersShareRetainedKeysAndReadersReadWithoutMutating()
    {
        await using var host = new SecurityHost(); host.Start();
        using var first = host.CreateClient(host.Ticket("Caseworker", "first-worker")); await Csrf(first);
        var key = '"' + Guid.NewGuid().ToString("D") + '"';
        first.DefaultRequestHeaders.Add("Idempotency-Key", key);
        var payload = new { title = "Synthetic authenticated case", description = "Fictional documentation issue." };
        using var created = await first.PostAsJsonAsync("/cases", payload);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var record = await created.Content.ReadFromJsonAsync<CaseValue>();
        using var changed = await first.PutAsJsonAsync($"/cases/{record!.Id}/status", new { status = "InReview" });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var second = host.CreateClient(host.Ticket("Caseworker", "second-worker")); await Csrf(second);
        second.DefaultRequestHeaders.Add("Idempotency-Key", key);
        using var replay = await second.PostAsJsonAsync("/cases", payload);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(record, await replay.Content.ReadFromJsonAsync<CaseValue>());
        using var reader = host.CreateClient(host.Ticket("Reader")); await Csrf(reader);
        foreach (var path in new[] { "/cases?title=authenticated", $"/cases/{record.Id}", $"/cases/{record.Id}/history" })
        {
            using var read = await reader.GetAsync(path); Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }
        reader.DefaultRequestHeaders.Add("Idempotency-Key", key);
        using var deniedReplay = await reader.PostAsJsonAsync("/cases", payload); await Denial(deniedReplay, 403);
        using var deniedNoop = await reader.PutAsJsonAsync($"/cases/{record.Id}/status", new { status = "InReview" }); await Denial(deniedNoop, 403);
        Assert.Equal(1, host.Database.Count());
        Assert.Equal(1L, host.Database.Scalar("SELECT count(*) FROM case_creation_keys"));
        Assert.Equal(2L, host.Database.Scalar("SELECT count(*) FROM case_status_history"));
    }

    [Fact]
    public async Task RequestTokenCannotBeReusedByAnotherSubject()
    {
        await using var host = new SecurityHost(); host.Start();
        using var client = host.CreateClient(host.Ticket("Caseworker", "first-user")); await Csrf(client);
        client.DefaultRequestHeaders.Remove("Cookie"); client.DefaultRequestHeaders.Add("Cookie", SecurityHost.CookieName + "=" + host.Ticket("Caseworker", "second-user"));
        using var response = await client.PostAsJsonAsync("/cases", new { title = "Synthetic", description = "Fictional" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(0, host.Database.Count());
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("revision")]
    [InlineData("damaged")]
    public async Task InvalidSessionsFailClosed(string reason)
    {
        await using var host = new SecurityHost(); host.Start();
        var ticket = host.Ticket("Reader", revision: reason == "revision" ? "old-revision" : "test-revision", expires: reason == "expired" ? host.Clock.Now.AddMinutes(-1) : null);
        using var client = host.CreateClient(reason == "damaged" ? ticket + "corruption" : ticket);
        using var response = await client.GetAsync("/cases"); await Denial(response, 401);
    }

    [Fact]
    public async Task SessionExpiresAbsolutelyWithoutSlidingAndKeysAreEncrypted()
    {
        await using var host = new SecurityHost(); host.Start();
        using var client = host.CreateClient(host.Ticket("Reader"));
        host.Clock.Now = host.Clock.Now.AddMinutes(29);
        using var first = await client.GetAsync("/cases"); Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.False(first.Headers.Contains("Set-Cookie"));
        host.Clock.Now = host.Clock.Now.AddMinutes(2);
        using var expired = await client.GetAsync("/cases"); await Denial(expired, 401);
        var key = Assert.Single(Directory.GetFiles(host.Keys, "key-*.xml"));
        var xml = await File.ReadAllTextAsync(key);
        Assert.Contains("encryptedSecret", xml); Assert.DoesNotContain("<masterKey", xml);
    }

    [Theory]
    [InlineData("Reader", true, false)]
    [InlineData("Caseworker", true, true)]
    [InlineData("Unknown", false, false)]
    public async Task RealOidcCodePkceCallbackProjectsOnlyVerifiedPermissions(string role, bool read, bool write)
    {
        await using var host = new SecurityHost(); host.Start();
        var (client, response) = await host.Login(role); using var loginClient = client; using var callback = response;
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode); Assert.Equal("/workspace", callback.Headers.Location!.OriginalString);
        var cookie = Assert.Single(callback.Headers.GetValues("Set-Cookie"), value => value.StartsWith(SecurityHost.CookieName + "=", StringComparison.Ordinal));
        Assert.Contains("secure", cookie); Assert.Contains("httponly", cookie); Assert.Contains("samesite=lax", cookie);
        Assert.DoesNotContain("domain=", cookie); Assert.DoesNotContain("expires=", cookie);
        using var session = await client.GetAsync("/auth/session");
        using var body = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal(read, body.RootElement.GetProperty("canRead").GetBoolean());
        Assert.Equal(write, body.RootElement.GetProperty("canWrite").GetBoolean());
        Assert.DoesNotContain("fictional-oidc-user", await session.Content.ReadAsStringAsync());
        await Csrf(client);
        using var signout = await client.PostAsync("/auth/logout", null); Assert.Equal(HttpStatusCode.OK, signout.StatusCode);
        using var denied = await client.GetAsync("/cases"); await Denial(denied, 401);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("nonce")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("signature")]
    [InlineData("expired")]
    [InlineData("subject")]
    public async Task InvalidOidcResponsesNeverCreateASession(string fault)
    {
        await using var host = new SecurityHost(); host.Start();
        var (client, response) = await host.Login("Caseworker", fault); using var loginClient = client; using var callback = response;
        await Denial(callback, 401);
        using var cases = await client.GetAsync("/cases"); await Denial(cases, 401);
    }

    [Theory]
    [InlineData("synthetic-tests", "test-revision", 200)]
    [InlineData("another-environment", "test-revision", 401)]
    [InlineData("synthetic-tests", "revoked-revision", 401)]
    public async Task RetainedKeysSurviveRestartButEnvironmentAndRevisionIsolateSessions(string environment, string revision, int status)
    {
        await using var host = new SecurityHost(); host.Start();
        var ticket = host.Ticket("Reader");
        var keys = Directory.GetFiles(host.Keys, "key-*.xml").Select(File.ReadAllText).ToArray();
        await host.Factory.DisposeAsync();
        host.Start(environment: environment, revision: revision);
        using var client = host.CreateClient(ticket);
        using var response = await client.GetAsync("/cases"); Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(keys, Directory.GetFiles(host.Keys, "key-*.xml").Select(File.ReadAllText).ToArray());
        Assert.Equal(0, host.Database.Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCorrelationOrNonceCookieRejectsCallback(bool keepCorrelation)
    {
        await using var host = new SecurityHost(); host.Start();
        using var challengeClient = host.CreateClient();
        using var challenge = await challengeClient.GetAsync("/auth/login");
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(challenge.Headers.Location!.Query);
        var code = host.Provider.Issue(query.ToDictionary(item => item.Key, item => item.Value.ToString()));
        using var callbackClient = host.CreateClient();
        if (keepCorrelation)
        {
            var correlation = Assert.Single(challenge.Headers.GetValues("Set-Cookie"), value => value.StartsWith(".AspNetCore.Correlation.", StringComparison.Ordinal));
            callbackClient.DefaultRequestHeaders.Add("Cookie", correlation.Split(';')[0]);
        }
        using var callback = await callbackClient.PostAsync("/auth/callback", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["code"] = code, ["state"] = query["state"].ToString() }));
        await Denial(callback, 401);
    }

    [Fact]
    public async Task OriginAndReturnTargetsCannotBeSelectedByTheCaller()
    {
        await using var host = new SecurityHost(); host.Start();
        using var client = host.CreateClient();
        using var openRedirect = await client.GetAsync("/auth/login?returnUrl=https://untrusted.example"); Assert.Equal(HttpStatusCode.BadRequest, openRedirect.StatusCode);
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https"); client.DefaultRequestHeaders.Add("X-Forwarded-Host", "localhost");
        using var http = await client.GetAsync("http://localhost/cases"); Assert.Equal(HttpStatusCode.BadRequest, http.StatusCode);
        using var otherHost = await client.GetAsync("https://untrusted.example/cases"); Assert.Equal(HttpStatusCode.BadRequest, otherHost.StatusCode);
    }

    [Fact]
    public async Task LocalDemoRejectsNonLoopbackHostAndIgnoresUntrustedIdentityCookies()
    {
        using var database = new TemporaryCaseDatabase(); using var factory = database.CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", "__Host-eci-session=forged-identity");
        client.DefaultRequestHeaders.Add("X-ECI-Role", "Caseworker");
        using var local = await client.GetAsync("/cases"); Assert.Equal(HttpStatusCode.OK, local.StatusCode);
        using var callback = await client.PostAsync("/auth/callback", new StringContent("forged")); Assert.Equal(HttpStatusCode.NotFound, callback.StatusCode);
        using var foreignHost = await client.GetAsync("http://untrusted.example/cases"); await Denial(foreignHost, 403);
        Assert.Equal(0, database.Count());
    }

    [Theory]
    [InlineData(null, "http://127.0.0.1:0", null)]
    [InlineData("LocalDemo", "http://0.0.0.0:8000", null)]
    [InlineData("LocalDemo", "http://localhost:8000", "--Security:ClientId")]
    [InlineData("LocalDemo", "http://localhost:8000", "--Security:CertificateSource")]
    [InlineData("LocalDemo", "http://localhost:8000", "--Security:CertificatePfx")]
    [InlineData("LocalDemo", "http://localhost:8000", "--Kestrel:Endpoints:Public:Url")]
    [InlineData("Oidc", "https://localhost:8000", null)]
    public async Task InvalidHostingConfigurationFailsWithoutTouchingStore(string? mode, string urls, string? extra)
    {
        using var database = new TemporaryCaseDatabase(initialize: false);
        var arguments = new List<string> { "--CaseStorage:DatabasePath", database.Path, "--urls", urls };
        if (mode is not null) arguments.AddRange(["--Security:Mode", mode]);
        if (extra is not null) arguments.AddRange([extra, "synthetic-value"]);
        var (code, output) = await ApiProcess.RunRawAsync(arguments.ToArray());
        Assert.Equal(1, code); Assert.Contains("Security configuration unavailable", output); Assert.False(File.Exists(database.Path));
    }
}
