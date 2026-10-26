using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace EnterpriseCaseIntelligence.Api.Tests.Security;

// Real TLS-to-HTTP transport fixture, not a production reverse proxy. It never
// follows redirects or shares a cookie container between incoming users.
internal sealed class TestHttpsProxy : IAsyncDisposable
{
    private WebApplication? _server;
    private readonly HttpClient _backend = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(20) };
    private static bool Omit(string name) => name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("X-Original-", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase) || name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase);

    public async Task Start(Uri origin, Uri backend, X509Certificate2 certificate)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, origin.Port, endpoint => endpoint.UseHttps(certificate)));
        _server = builder.Build();
        _server.Run(async context =>
        {
            if (context.Request.Host.Value != origin.Authority) { context.Response.StatusCode = 400; return; }
            using var outgoing = new HttpRequestMessage(new(context.Request.Method), new UriBuilder(backend) { Path = context.Request.Path.Value, Query = context.Request.QueryString.Value }.Uri);
            if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")) outgoing.Content = new StreamContent(context.Request.Body);
            foreach (var header in context.Request.Headers)
            {
                if (!Omit(header.Key) && !outgoing.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                    outgoing.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
            outgoing.Headers.Host = origin.Authority;
            outgoing.Headers.Add("X-Forwarded-Proto", "https");
            using var response = await _backend.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers))
                if (!header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) && !header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                    context.Response.Headers[header.Key] = header.Value.ToArray();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
        await _server.StartAsync();
    }
    public async ValueTask DisposeAsync() { if (_server is not null) await _server.DisposeAsync(); _backend.Dispose(); }
}
