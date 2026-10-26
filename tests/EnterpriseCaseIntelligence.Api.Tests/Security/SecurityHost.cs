using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseCaseIntelligence.Api.Tests.Security;

// Test-only protocol provider, generated certificates and encrypted framework
// tickets. None of these hooks or identity routes exists in the application.
internal sealed class SecurityHost : IAsyncDisposable
{
    public const string Issuer = "https://synthetic-identity.example/tenant";
    public const string ClientId = "synthetic-client";
    public const string CookieName = "__Host-eci-session";
    public TemporaryCaseDatabase Database { get; } = new();
    public TestIdentityProvider Provider { get; } = new();
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public Uri Origin { get; private set; } = new("https://localhost");
    public Uri? Backend { get; private set; }
    private TestHttpsProxy? _proxy;
    public string Keys => Path.Combine(Database.DirectoryPath, "keys");
    public MutableSecurityClock Clock { get; } = new();
    public X509Certificate2 Certificate { get; }

    public SecurityHost()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        Certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
        File.WriteAllBytes(Path.Combine(Database.DirectoryPath, "test.pfx"), Certificate.Export(X509ContentType.Pfx));
        Directory.CreateDirectory(Keys);
    }

    public void Start(bool network = false, string revision = "test-revision", string environment = "synthetic-tests", bool proxy = false, Action<HttpContext>? requestSetup = null)
    {
        if (network)
        {
            using var port = new TcpListener(IPAddress.Loopback, 0);
            port.Start();
            Origin = new($"https://localhost:{((IPEndPoint)port.LocalEndpoint).Port}");
        }
        Backend = proxy ? new Uri($"http://127.0.0.1:{ReservePort()}") : null;
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CaseStorage:DatabasePath"] = Database.Path,
                ["Security:Mode"] = "Oidc",
                ["Security:Ingress"] = proxy ? "LoopbackProxy" : "DirectHttps",
                ["urls"] = Backend?.AbsoluteUri,
                ["Security:Authority"] = Issuer,
                ["Security:Issuer"] = Issuer,
                ["Security:ClientId"] = ClientId,
                ["Security:ClientSecret"] = "test-only-secret",
                ["Security:PublicOrigin"] = Origin.AbsoluteUri,
                ["Security:EnvironmentId"] = environment,
                ["Security:SessionRevision"] = revision,
                ["Security:KeyDirectory"] = Keys,
                ["Security:CertificatePath"] = Path.Combine(Database.DirectoryPath, "test.pfx"),
                ["Security:RoleClaim"] = "roles",
                ["Security:ReaderRole"] = "Reader",
                ["Security:CaseworkerRole"] = "Caseworker"
            }));
            builder.ConfigureServices(services =>
            {
                if (requestSetup is not null) services.AddSingleton<IStartupFilter>(new RequestSetupFilter(requestSetup));
                services.PostConfigure<OpenIdConnectOptions>("eci-oidc", options =>
                {
                    // Static metadata/backchannel is exclusively a test seam.
                    // The production handler always fetches HTTPS authority metadata.
                    options.Configuration = Provider.Configuration;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(Provider.Configuration);
                    options.Backchannel = new HttpClient(Provider.Backchannel);
                });
                services.PostConfigure<CookieAuthenticationOptions>("eci-cookie", options => options.TimeProvider = Clock);
            });
        });
        if (network) Factory.UseKestrel(options =>
        {
            if (proxy) options.Listen(IPAddress.Loopback, Backend!.Port);
            else options.Listen(IPAddress.Loopback, Origin.Port, endpoint => endpoint.UseHttps(Certificate));
        });
        using var client = CreateClient(); // Start and validate configuration now.
    }

    public async Task StartProxyAsync()
    {
        _proxy = new TestHttpsProxy();
        await _proxy.Start(Origin, Backend!, Certificate);
    }

    private static int ReservePort()
    {
        using var port = new TcpListener(IPAddress.Loopback, 0); port.Start();
        return ((IPEndPoint)port.LocalEndpoint).Port;
    }

    public HttpClient CreateClient(string? ticket = null)
    {
        var client = Factory.CreateClient(new() { BaseAddress = Origin, AllowAutoRedirect = false, HandleCookies = true });
        if (ticket is not null) client.DefaultRequestHeaders.Add("Cookie", CookieName + "=" + ticket);
        return client;
    }

    public string Ticket(string role, string subject = "fictional-user", string revision = "test-revision", DateTimeOffset? expires = null)
    {
        var identity = new ClaimsIdentity("eci-cookie", "eci_subject", "eci_role");
        identity.AddClaims([new("eci_subject", subject), new("eci_issuer", Issuer), new("eci_revision", revision), new("eci_role", role)]);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), new AuthenticationProperties
        { IssuedUtc = Clock.GetUtcNow(), ExpiresUtc = expires ?? Clock.GetUtcNow().AddMinutes(30), AllowRefresh = false }, "eci-cookie");
        return Factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("eci-cookie").TicketDataFormat.Protect(ticket);
    }

    public async Task<(HttpClient Client, HttpResponseMessage Response)> Login(string role = "Reader", string? fault = null)
    {
        Provider.Role = role; Provider.Fault = fault;
        var client = CreateClient();
        using var challenge = await client.GetAsync("/auth/login");
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var query = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("form_post", query["response_mode"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(new Uri(Origin, "/auth/callback").AbsoluteUri, query["redirect_uri"]);
        var code = Provider.Issue(query.ToDictionary(item => item.Key, item => item.Value.ToString()));
        var response = await client.PostAsync("/auth/callback", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["code"] = code, ["state"] = fault == "state" ? "invalid-state" : query["state"].ToString() }));
        return (client, response);
    }

    public async ValueTask DisposeAsync()
    {
        if (_proxy is not null) await _proxy.DisposeAsync();
        if (Factory is not null) await Factory.DisposeAsync();
        await Provider.DisposeAsync(); Certificate.Dispose(); Database.Dispose();
    }
}

internal sealed class RequestSetupFilter(Action<HttpContext> setup) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, continuation) => { setup(context); await continuation(context); });
        next(app);
    };
}

internal sealed class MutableSecurityClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class TestIdentityProvider : IAsyncDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _codes = new();
    private WebApplication? _server;
    public string Role { get; set; } = "Reader";
    public string? Fault { get; set; }
    public OpenIdConnectConfiguration Configuration { get; }
    public HttpMessageHandler Backchannel { get; }
    public TestIdentityProvider()
    {
        Configuration = new() { Issuer = SecurityHost.Issuer, AuthorizationEndpoint = SecurityHost.Issuer + "/authorize", TokenEndpoint = SecurityHost.Issuer + "/token" };
        Configuration.SigningKeys.Add(new RsaSecurityKey(_rsa) { KeyId = "disposable-test-key" });
        Backchannel = new TokenBackchannel(this);
    }

    public string Issue(Dictionary<string, string> query)
    {
        var code = Guid.NewGuid().ToString("N"); _codes[code] = query; return code;
    }

    public async Task StartBrowserEndpoint()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        _server = builder.Build();
        _server.MapGet("/authorize", (HttpContext context) =>
        {
            var query = context.Request.Query.ToDictionary(item => item.Key, item => item.Value.ToString());
            var code = Issue(query);
            string Encode(string value) => WebUtility.HtmlEncode(value);
            return Results.Content($"<form method='post' action='{Encode(query["redirect_uri"])}'><input name='code' value='{code}'><input name='state' value='{Encode(query["state"])}'></form><script>document.forms[0].submit()</script>", "text/html");
        });
        await _server.StartAsync(); Configuration.AuthorizationEndpoint = _server.Urls.Single() + "/authorize";
    }

    private sealed class TokenBackchannel(TestIdentityProvider provider) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var fields = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (!provider._codes.TryRemove(fields["code"].ToString(), out var original) || fields["client_id"] != SecurityHost.ClientId ||
                fields["client_secret"] != "test-only-secret" || fields["redirect_uri"] != original["redirect_uri"] ||
                WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(fields["code_verifier"].ToString()))) != original["code_challenge"])
                return new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_grant\"}") };
            var now = DateTime.UtcNow;
            var key = provider.Fault == "signature" ? new RsaSecurityKey(RSA.Create(2048)) { KeyId = "disposable-test-key" } : provider.Configuration.SigningKeys.Single();
            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = provider.Fault == "issuer" ? "https://untrusted.example" : SecurityHost.Issuer,
                Audience = provider.Fault == "audience" ? "wrong-client" : SecurityHost.ClientId,
                NotBefore = now.AddMinutes(-10),
                IssuedAt = now.AddMinutes(-10),
                Expires = provider.Fault == "expired" ? now.AddMinutes(-5) : now.AddMinutes(5),
                SigningCredentials = new(key, SecurityAlgorithms.RsaSha256),
                Claims = new Dictionary<string, object>
                {
                    ["sub"] = "fictional-oidc-user",
                    ["roles"] = new[] { provider.Role },
                    ["nonce"] = provider.Fault == "nonce" ? "wrong-nonce" : original["nonce"]
                }
            };
            if (provider.Fault == "subject") descriptor.Claims.Remove("sub");
            var token = new JsonWebTokenHandler().CreateToken(descriptor);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { id_token = token, access_token = "test-unused-token", token_type = "Bearer", expires_in = 300 }), Encoding.UTF8, "application/json") };
        }
    }
    public async ValueTask DisposeAsync() { if (_server is not null) await _server.DisposeAsync(); Backchannel.Dispose(); _rsa.Dispose(); }
}
