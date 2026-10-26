using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace EnterpriseCaseIntelligence.Api.Tests.Security;

public sealed class CaseIngressTests
{
    private static void TrustedRequest(Microsoft.AspNetCore.Http.HttpContext context)
    { context.Connection.RemoteIpAddress = IPAddress.Loopback; context.Request.Scheme = "http"; }

    private static async Task InvalidOrigin(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location); Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("about:blank", body.RootElement.GetProperty("type").GetString());
        Assert.Equal("invalid_origin", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(4, body.RootElement.EnumerateObject().Count());
    }

    [Theory]
    [MemberData(nameof(CaseSecurityTests.Routes), MemberType = typeof(CaseSecurityTests))]
    public async Task TrustedProxyPreservesAuthenticationBeforeInputParsing(string method, string path)
    {
        await using var host = new SecurityHost(); host.Start(proxy: true, requestSetup: TrustedRequest);
        using var client = host.CreateClient(); client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        using var request = new HttpRequestMessage(new(method), path);
        if (method is "POST" or "PUT") request.Content = new StringContent("malformed", Encoding.UTF8, "text/plain");
        File.Delete(host.Database.Path);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("authentication_required", json.RootElement.GetProperty("code").GetString());
        Assert.False(File.Exists(host.Database.Path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http")]
    [InlineData("https, http")]
    [InlineData("https, https")]
    [InlineData("")]
    public async Task MissingOrChainedForwardedProtocolIsRejected(string? proto)
    {
        await using var host = new SecurityHost(); host.Start(proxy: true, requestSetup: TrustedRequest);
        using var client = host.CreateClient();
        if (proto is not null) client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-Proto", proto);
        using var response = await client.GetAsync("/cases"); await InvalidOrigin(response);
    }

    [Theory]
    [InlineData("198.51.100.9", "http")]
    [InlineData("127.0.0.2", "http")]
    [InlineData("::1", "http")]
    [InlineData(null, "http")]
    [InlineData("127.0.0.1", "https")]
    public async Task PhysicalPeerAndBackendProtocolCannotBeSpoofedByHeaders(string? peer, string scheme)
    {
        await using var host = new SecurityHost();
        host.Start(proxy: true, requestSetup: context => { context.Connection.RemoteIpAddress = peer is null ? null : IPAddress.Parse(peer); context.Request.Scheme = scheme; });
        using var client = host.CreateClient(host.Ticket("Caseworker"));
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        using var response = await client.GetAsync("/cases"); await InvalidOrigin(response);
    }

    [Theory]
    [InlineData("X-Forwarded-Host")]
    [InlineData("X-Forwarded-For")]
    [InlineData("X-Forwarded-Prefix")]
    [InlineData("Forwarded")]
    public async Task OtherForwardingHeadersAreNotAcceptedAsAnIngressChain(string header)
    {
        await using var host = new SecurityHost(); host.Start(proxy: true, requestSetup: TrustedRequest);
        using var client = host.CreateClient(); client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.TryAddWithoutValidation(header, "synthetic-untrusted-value");
        using var response = await client.GetAsync("/auth/login"); await InvalidOrigin(response);
    }

    [Fact]
    public async Task ValidForwardedSchemeDoesNotAuthorizeAnUnexpectedHost()
    {
        await using var host = new SecurityHost(); host.Start(proxy: true, requestSetup: TrustedRequest);
        using var client = host.CreateClient(); client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        using var response = await client.GetAsync("https://untrusted.example/auth/login"); await InvalidOrigin(response);
    }

    [Fact]
    public async Task MappedLoopbackPeerWorksAndCookieCsrfRoleAndReplayContractsArePreserved()
    {
        await using var host = new SecurityHost();
        host.Start(proxy: true, requestSetup: context => { context.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:127.0.0.1"); context.Request.Scheme = "http"; });
        using var client = host.CreateClient(host.Ticket("Caseworker")); client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        using var missingCsrf = await client.PostAsJsonAsync("/cases", new { title = "Synthetic", description = "Fictional" });
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        Assert.Contains("csrf", await missingCsrf.Content.ReadAsStringAsync());
        await CaseSecurityTests.Csrf(client);
        client.DefaultRequestHeaders.Add("Idempotency-Key", '"' + Guid.NewGuid().ToString("D") + '"');
        var payload = new { title = "Proxy synthetic case", description = "Fictional retained creation." };
        using var created = await client.PostAsJsonAsync("/cases", payload); Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var replay = await client.PostAsJsonAsync("/cases", payload); Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(await created.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(1, host.Database.Count());
        using var reader = host.CreateClient(host.Ticket("Reader")); reader.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        using var denied = await reader.PostAsJsonAsync("/cases", payload); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Theory]
    [InlineData("Security:Ingress", "Unknown")]
    [InlineData("urls", "http://0.0.0.0:5080")]
    [InlineData("urls", "http://localhost:5080")]
    [InlineData("urls", "https://127.0.0.1:5080")]
    [InlineData("urls", "http://127.0.0.1:0")]
    [InlineData("urls", "http://127.0.0.1:5080;http://127.0.0.1:5081")]
    [InlineData("Kestrel:Endpoints:Unexpected:Url", "http://0.0.0.0:5080")]
    [InlineData("ForwardedHeaders_Enabled", "true")]
    public async Task InconsistentProxyHostingConfigurationFailsBeforeCaseIo(string name, string value)
    {
        await using var host = new SecurityHost(); File.Delete(host.Database.Path);
        var settings = new Dictionary<string, string>
        {
            ["CaseStorage:DatabasePath"] = host.Database.Path,
            ["Security:Mode"] = "Oidc",
            ["Security:Ingress"] = "LoopbackProxy",
            ["urls"] = "http://127.0.0.1:5080",
            ["Security:PublicOrigin"] = "https://localhost",
            ["Security:Authority"] = SecurityHost.Issuer,
            ["Security:Issuer"] = SecurityHost.Issuer,
            ["Security:ClientId"] = SecurityHost.ClientId,
            ["Security:ClientSecret"] = "test-only-secret",
            ["Security:RoleClaim"] = "roles",
            ["Security:ReaderRole"] = "Reader",
            ["Security:CaseworkerRole"] = "Caseworker",
            ["Security:EnvironmentId"] = "synthetic-tests",
            ["Security:SessionRevision"] = "test-revision",
            ["Security:KeyDirectory"] = host.Keys,
            ["Security:CertificatePath"] = Path.Combine(host.Database.DirectoryPath, "test.pfx")
        };
        settings[name] = value;
        var (exit, output) = await ApiProcess.RunRawAsync(settings.SelectMany(setting => new[] { "--" + setting.Key, setting.Value }).ToArray());
        Assert.Equal(1, exit); Assert.Contains("Security configuration unavailable", output);
        Assert.DoesNotContain("test-only-secret", output); Assert.DoesNotContain(host.Database.Path, output);
        Assert.False(File.Exists(host.Database.Path));
    }

    [Theory]
    [InlineData("ForwardedHeaders_Enabled")]
    [InlineData("ASPNETCORE_FORWARDEDHEADERS_ENABLED")]
    public async Task LocalDemoRejectsFrameworkWideUnrestrictedForwarding(string setting)
    {
        using var database = new TemporaryCaseDatabase(initialize: false);
        var (exit, output) = await ApiProcess.RunRawAsync("--Security:Mode", "LocalDemo", "--urls", "http://127.0.0.1:0", "--" + setting, "true", "--CaseStorage:DatabasePath", database.Path);
        Assert.Equal(1, exit); Assert.Contains("Security configuration unavailable", output); Assert.False(File.Exists(database.Path));
    }
}
