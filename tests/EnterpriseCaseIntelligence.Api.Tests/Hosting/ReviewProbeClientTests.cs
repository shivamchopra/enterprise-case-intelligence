using System.Net;
using System.Text;
using EnterpriseCaseIntelligence.ReviewProbe;
using Microsoft.Extensions.Configuration;

namespace EnterpriseCaseIntelligence.Api.Tests.Hosting;

public sealed class ReviewProbeClientTests
{
    [Fact]
    public void ProductionHandlerKeepsNormalTlsValidationAndDisablesRedirectsAndCookies()
    {
        using var handler = ProbeClient.CreateHandler();
        Assert.False(handler.AllowAutoRedirect); Assert.False(handler.UseCookies);
        Assert.Null(handler.ServerCertificateCustomValidationCallback);
        using var client = ProbeClient.CreateClient(); Assert.Equal(TimeSpan.FromSeconds(15), client.Timeout);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task OperatorClientRejectsEveryRedirectBeforeReadingOrForwardingCredentials(int status)
    {
        using var handler = new RecordingHandler(_ => new((HttpStatusCode)status)
        { Headers = { Location = new Uri("https://PRIVATE_OTHER_ORIGIN.example/probe") } });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => ProbeClient.Send(Configuration(), null, client));
        Assert.Equal(1, handler.Calls);
        Assert.Equal("https://synthetic-probe.example/probe", handler.Url);
    }

    [Fact]
    public async Task ClientSendsReceiptOnlyInBoundedPostBodyAndPreservesReadinessFalse()
    {
        const string receipt = "synthetic-opaque-receipt";
        using var handler = new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(new string('a', 64), request.Headers.Authorization?.Parameter);
            Assert.Equal("/probe/verify", request.RequestUri?.AbsolutePath);
            Assert.Empty(request.RequestUri!.Query);
            Assert.Equal("text/plain", request.Content?.Headers.ContentType?.ToString());
            return Json("{\"schemaVersion\":1,\"deploymentReady\":false,\"retainedReceiptValid\":true}");
        });
        using var client = new HttpClient(handler);
        var result = await ProbeClient.Send(Configuration(), receipt, client);
        Assert.Contains("\"deploymentReady\":false", result);
        Assert.Equal(receipt, handler.Body);
    }

    [Theory]
    [InlineData("http://synthetic-probe.example")]
    [InlineData("https://user:PRIVATE_SECRET@synthetic-probe.example")]
    [InlineData("https://synthetic-probe.example?PRIVATE_TOKEN=value")]
    [InlineData("https://synthetic-probe.example/path")]
    public async Task InvalidClientOriginCannotSendCredential(string origin)
    {
        using var handler = new RecordingHandler(_ => Json("{}")); using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => ProbeClient.Send(Configuration(origin), null, client));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("malformed")]
    [InlineData("ready")]
    [InlineData("wrong-schema")]
    public async Task ClientRejectsUnboundedOrInvalidEvidence(string fault)
    {
        using var handler = new RecordingHandler(_ => Json(fault switch
        {
            "oversized" => new string('a', 32 * 1024 + 1),
            "malformed" => "PRIVATE_MALFORMED_RESPONSE",
            "ready" => "{\"schemaVersion\":1,\"deploymentReady\":true}",
            _ => "{\"schemaVersion\":2,\"deploymentReady\":false}"
        }));
        using var client = new HttpClient(handler);
        if (fault == "malformed") await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => ProbeClient.Send(Configuration(), null, client));
        else await Assert.ThrowsAsync<HttpRequestException>(() => ProbeClient.Send(Configuration(), null, client));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task NormalTlsClientRejectsUntrustedProbeCertificateWithoutExposingDetails()
    {
        await using var probe = new ReviewProbeProcess();
        var tlsPath = Path.Combine(probe.Root, "synthetic-tls.pfx");
        await File.WriteAllBytesAsync(tlsPath, probe.Certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx));
        probe.Configuration["urls"] = probe.Backend.AbsoluteUri.Replace("http:", "https:", StringComparison.Ordinal);
        probe.Configuration["Probe:PublicOrigin"] = probe.Configuration["urls"];
        probe.Configuration["Kestrel:Certificates:Default:Path"] = tlsPath;
        Assert.Equal(0, (await probe.Command("init-keys")).ExitCode);
        // Wait for the HTTPS listener using a test-only certificate pin. The real
        // operator client below still uses its unmodified TLS validation.
        using var process = probe.Launch("serve");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var pinned = new HttpClient(new HttpClientHandler
        { ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate?.Thumbprint == probe.Certificate.Thumbprint });
        try
        {
            var listening = false;
            for (var attempt = 0; attempt < 200 && !process.HasExited; attempt++)
            {
                try { using var response = await pinned.GetAsync(probe.Configuration["urls"] + "probe"); listening = response.StatusCode == HttpStatusCode.Unauthorized; if (listening) break; }
                catch (HttpRequestException) { }
                await Task.Delay(50);
            }
            Assert.True(listening);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(probe.Configuration).Build();
            var evidence = await ProbeClient.Send(configuration, null, pinned);
            using var json = System.Text.Json.JsonDocument.Parse(evidence);
            Assert.Equal("https", json.RootElement.GetProperty("observations").GetProperty("originalScheme").GetString());
            var receipt = json.RootElement.GetProperty("receipt").GetString()!;
            var verification = await ProbeClient.Send(configuration, receipt, pinned);
            Assert.Contains("\"retainedReceiptValid\":true", verification);
            var result = await probe.Command("observe");
            Assert.Equal(1, result.ExitCode);
            Assert.DoesNotContain(probe.Root, result.Output);
            Assert.DoesNotContain(probe.Credential, result.Output);
            Assert.DoesNotContain("certificate", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Empty(await output + await error);
    }

    private static IConfiguration Configuration(string origin = "https://synthetic-probe.example") => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Probe:PublicOrigin"] = origin, ["Probe:OperatorCredential"] = new string('a', 64) }).Build();

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Url { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; Url = request.RequestUri?.AbsoluteUri; Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken); return respond(request); }
    }
}
