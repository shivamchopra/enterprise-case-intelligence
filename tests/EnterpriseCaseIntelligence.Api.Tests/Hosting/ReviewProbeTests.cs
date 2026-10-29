using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using EnterpriseCaseIntelligence.ReviewProbe;
using Microsoft.Extensions.Configuration;

namespace EnterpriseCaseIntelligence.Api.Tests.Hosting;

public sealed class ReviewProbeTests
{
    [Fact]
    public async Task ExplicitInitializationRestartAndReplacementRetainEncryptedKeysAndOldReceipt()
    {
        await using var probe = new ReviewProbeProcess();
        Assert.Equal(1, (await probe.Command("serve")).ExitCode); // Empty is not initialized on startup.
        Assert.Empty(probe.KeyHashes());
        Assert.Equal(0, (await probe.Command("init-keys")).ExitCode);
        var hashes = probe.KeyHashes(); Assert.Single(hashes);
        foreach (var path in Directory.GetFiles(probe.Keys, "key-*.xml"))
        {
            var xml = XDocument.Load(path);
            Assert.Contains(xml.Descendants(), element => element.Name.LocalName == "encryptedSecret");
            Assert.DoesNotContain(xml.Descendants(), element => element.Name.LocalName == "masterKey");
        }
        Assert.Equal(1, (await probe.Command("init-keys")).ExitCode); // No overwrite.
        await probe.Start();
        using var client = probe.Client();
        var receipt = await Receipt(client);
        Assert.True(await Verify(client, receipt));
        var output = await probe.Stop();
        AssertPrivate(output, probe, receipt);
        await probe.Start();
        Assert.True(await Verify(client, receipt));
        await probe.Stop();
        probe.ReplaceArtifact(); // Different release path; same compiled probe/configuration.
        await probe.Start();
        Assert.True(await Verify(client, receipt));
        await probe.Stop();
        Assert.Equal(hashes.OrderBy(pair => pair.Key), probe.KeyHashes().OrderBy(pair => pair.Key));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("certificate")]
    [InlineData("corruption")]
    [InlineData("missing-directory")]
    public async Task IncompatibleRetainedStateFailsWithoutReplacementAndOriginalCanRecover(string fault)
    {
        await using var probe = new ReviewProbeProcess();
        Assert.Equal(0, (await probe.Command("init-keys")).ExitCode);
        await probe.Start();
        using var client = probe.Client(); var receipt = await Receipt(client);
        await probe.Stop();
        var hashes = probe.KeyHashes();
        var changes = new Dictionary<string, string?>();
        byte[]? original = null; string? corruptPath = null;
        if (fault == "password") changes["Security:CertificatePassword"] = "PRIVATE_WRONG_PASSWORD";
        if (fault == "certificate")
        {
            await using var other = new ReviewProbeProcess();
            changes["Security:CertificatePfx"] = other.Configuration["Security:CertificatePfx"];
        }
        if (fault == "missing-directory") changes["Probe:KeyDirectory"] = Path.Combine(probe.Root, "PRIVATE_MISSING_DIRECTORY");
        if (fault == "corruption")
        {
            corruptPath = Directory.GetFiles(probe.Keys, "key-*.xml").Single();
            original = await File.ReadAllBytesAsync(corruptPath);
            await File.WriteAllTextAsync(corruptPath, "<key>PRIVATE_CORRUPT_KEY</key>");
        }
        var beforeFailure = probe.KeyHashes();
        var failed = await probe.Command("serve", changes);
        Assert.Equal(1, failed.ExitCode);
        AssertPrivate(failed.Output, probe, receipt);
        Assert.DoesNotContain("PRIVATE_", failed.Output);
        Assert.Equal(beforeFailure.OrderBy(pair => pair.Key), probe.KeyHashes().OrderBy(pair => pair.Key));
        Assert.False(Directory.Exists(Path.Combine(probe.Root, "PRIVATE_MISSING_DIRECTORY")));
        if (original is not null) await File.WriteAllBytesAsync(corruptPath!, original);
        await probe.Start();
        Assert.True(await Verify(client, receipt));
        await probe.Stop();
        Assert.Equal(hashes.OrderBy(pair => pair.Key), probe.KeyHashes().OrderBy(pair => pair.Key));
    }

    [Theory]
    [InlineData("Probe:Revision")]
    [InlineData("Probe:EnvironmentId")]
    public async Task ReceiptIsIsolatedByRevisionAndEnvironmentWithoutReset(string name)
    {
        await using var probe = new ReviewProbeProcess();
        Assert.Equal(0, (await probe.Command("init-keys")).ExitCode);
        await probe.Start();
        using var client = probe.Client(); var receipt = await Receipt(client);
        await probe.Stop(); var hashes = probe.KeyHashes();
        var original = probe.Configuration[name]; probe.Configuration[name] = "synthetic-isolation-change";
        await probe.Start(); Assert.False(await Verify(client, receipt)); await probe.Stop();
        probe.Configuration[name] = original;
        await probe.Start(); Assert.True(await Verify(client, receipt)); await probe.Stop();
        Assert.Equal(hashes.OrderBy(pair => pair.Key), probe.KeyHashes().OrderBy(pair => pair.Key));
    }

    [Fact]
    public async Task ASecondWriterCannotStartOrInitialize()
    {
        await using var probe = new ReviewProbeProcess();
        Assert.Equal(0, (await probe.Command("init-keys")).ExitCode);
        await probe.Start(); var hashes = probe.KeyHashes();
        Assert.Equal(1, (await probe.Command("serve")).ExitCode);
        Assert.Equal(1, (await probe.Command("init-keys")).ExitCode);
        Assert.Equal(hashes.OrderBy(pair => pair.Key), probe.KeyHashes().OrderBy(pair => pair.Key));
    }

    [Fact]
    public async Task AuthenticationPrecedesEveryRouteAndBodyAndDoesNotAcceptCookiesOrIdentityHeaders()
    {
        await using var probe = new ReviewProbeProcess();
        Assert.Equal(0, (await probe.Command("init-keys")).ExitCode); await probe.Start();
        using var anonymous = probe.Client(authenticated: false);
        anonymous.DefaultRequestHeaders.Add("Cookie", "operator=" + probe.Credential);
        anonymous.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", "PRIVATE_FORGED_ROLE");
        foreach (var route in new[] { "/probe", "/cases", "/auth/login", "/health", "/probe?token=" + probe.Credential, "/" })
        {
            using var denied = await anonymous.GetAsync(route);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.Empty(await denied.Content.ReadAsStringAsync());
            Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());
            Assert.False(denied.Headers.Contains("Set-Cookie")); Assert.Null(denied.Headers.Location);
        }
        foreach (var token in new[] { "Bearer " + new string('0', 64), "Bearer " + new string('a', 72), "Basic " + probe.Credential, "Bearer " + probe.Credential + ",Bearer " + probe.Credential })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/probe/verify") { Content = new StringContent("PRIVATE_INVALID_RECEIPT") };
            request.Headers.TryAddWithoutValidation("Authorization", token);
            using var denied = await anonymous.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode); Assert.Empty(await denied.Content.ReadAsStringAsync());
        }
        using var authorized = probe.Client();
        using var absent = await authorized.GetAsync("/cases"); Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        using var query = await authorized.GetAsync("/probe?anything=private"); Assert.Equal(HttpStatusCode.BadRequest, query.StatusCode);
        authorized.DefaultRequestHeaders.Add("Origin", "https://synthetic-probe.example");
        using var browser = await authorized.GetAsync("/probe"); Assert.Equal(HttpStatusCode.BadRequest, browser.StatusCode);
        Assert.False(browser.Headers.Contains("Access-Control-Allow-Origin"));
        AssertPrivate(await probe.Stop(), probe);
    }

    [Fact]
    public async Task RawTransportAndBoundedHeaderClassificationsCannotBeChangedIntoIdentityOrForwardedOrigin()
    {
        await using var probe = new ReviewProbeProcess();
        Assert.Equal(0, (await probe.Command("init-keys")).ExitCode); await probe.Start();
        using var client = probe.Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/probe");
        request.Headers.Host = "synthetic-probe.example";
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "PRIVATE_CLIENT_ONE,PRIVATE_CLIENT_TWO");
        request.Headers.Add("X-Forwarded-Host", "PRIVATE_FORGED_HOST");
        request.Headers.Add("Forwarded", "for=PRIVATE_CLIENT;proto=https");
        request.Headers.Add("X-MS-CLIENT-PRINCIPAL", "PRIVATE_FORGED_ROLE");
        using var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("PRIVATE_", text);
        AssertPrivate(text, probe);
        using var json = JsonDocument.Parse(text);
        Assert.False(json.RootElement.GetProperty("deploymentReady").GetBoolean());
        var observation = json.RootElement.GetProperty("observations");
        Assert.Equal("http", observation.GetProperty("originalScheme").GetString());
        Assert.Equal("127.0.0.1", observation.GetProperty("originalPeer").GetString());
        Assert.True(observation.GetProperty("hostMatches").GetBoolean());
        var headers = observation.GetProperty("headers");
        Assert.Equal("https", headers.GetProperty("X-Forwarded-Proto").GetProperty("classification").GetString());
        Assert.Equal(2, headers.GetProperty("X-Forwarded-For").GetProperty("cardinality").GetInt32());
        Assert.Equal("absent", headers.GetProperty("X-Forwarded-Prefix").GetProperty("classification").GetString());
        using var conflicting = new HttpRequestMessage(HttpMethod.Get, "/probe");
        conflicting.Headers.Host = "PRIVATE_FORGED_HOST.example";
        conflicting.Headers.Add("X-Forwarded-Proto", ["http", "https"]);
        conflicting.Headers.Add("X-Forwarded-For", new string('a', 1025));
        using var next = await client.SendAsync(conflicting);
        using var nextJson = JsonDocument.Parse(await next.Content.ReadAsStringAsync());
        var nextObservation = nextJson.RootElement.GetProperty("observations");
        Assert.False(nextObservation.GetProperty("hostMatches").GetBoolean());
        Assert.Equal("multiple", nextObservation.GetProperty("headers").GetProperty("X-Forwarded-Proto").GetProperty("classification").GetString());
        Assert.Equal("oversized", nextObservation.GetProperty("headers").GetProperty("X-Forwarded-For").GetProperty("classification").GetString());
    }

    [Fact]
    public async Task ReceiptVerificationRejectsMalformedOversizedEncodedAndUnrelatedValues()
    {
        await using var probe = new ReviewProbeProcess();
        Assert.Equal(0, (await probe.Command("init-keys")).ExitCode); await probe.Start();
        using var client = probe.Client();
        foreach (var value in new[] { "", "PRIVATE invalid receipt", new string('a', 2049) })
        {
            using var content = new StringContent(value); content.Headers.ContentType = new("text/plain");
            using var response = await client.PostAsync("/probe/verify", content);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsStringAsync());
        }
        using var encoded = new StringContent("synthetic"); encoded.Headers.ContentType = new("text/plain"); encoded.Headers.ContentEncoding.Add("gzip");
        using var encodedResponse = await client.PostAsync("/probe/verify", encoded); Assert.Equal(HttpStatusCode.BadRequest, encodedResponse.StatusCode);
        Assert.False(await Verify(client, "synthetic-unrelated-receipt"));
    }

    [Theory]
    [InlineData("Probe:Enabled", "false")]
    [InlineData("Probe:OperatorCredential", "PRIVATE_SHORT_CREDENTIAL")]
    [InlineData("Probe:PublicOrigin", "http://synthetic-probe.example")]
    [InlineData("Probe:PublicOrigin", "https://synthetic-probe.example/path")]
    [InlineData("Probe:Revision", "ambiguous:revision")]
    [InlineData("ASPNETCORE_FORWARDEDHEADERS_ENABLED", "true")]
    [InlineData("ForwardedHeaders_Enabled", "TRUE")]
    [InlineData("DOTNET_FORWARDEDHEADERS_ENABLED", "true")]
    [InlineData("ASPNETCORE_HOSTINGSTARTUPASSEMBLIES", "PRIVATE_STARTUP_ASSEMBLY")]
    [InlineData("Security:CertificateSource", "File")]
    public async Task UnsafeStartupConfigurationFailsBeforeKeyGenerationWithoutDisclosingInputs(string name, string value)
    {
        await using var probe = new ReviewProbeProcess();
        var result = await probe.Command("init-keys", new Dictionary<string, string?> { [name] = value });
        Assert.Equal(1, result.ExitCode); Assert.Empty(probe.KeyHashes());
        AssertPrivate(result.Output, probe); Assert.DoesNotContain("PRIVATE_", result.Output);
    }

    [Fact]
    public async Task RestrictedPrivateJsonConfigurationWorksWithoutCredentialsInArguments()
    {
        await using var probe = new ReviewProbeProcess();
        var path = Path.Combine(probe.Root, "private.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(probe.Configuration));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal(0, (await probe.Command("init-keys", file: path)).ExitCode);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            Assert.Equal(1, (await probe.Command("serve", file: path)).ExitCode);
        }
    }

    internal static async Task<string> Receipt(HttpClient client)
    {
        using var response = await client.GetAsync("/probe"); response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("receipt").GetString()!;
    }

    internal static async Task<bool> Verify(HttpClient client, string receipt)
    {
        using var content = new StringContent(receipt, Encoding.ASCII); content.Headers.ContentType = new("text/plain");
        using var response = await client.PostAsync("/probe/verify", content); response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("retainedReceiptValid").GetBoolean();
    }

    private static void AssertPrivate(string text, ReviewProbeProcess probe, string? receipt = null)
    {
        foreach (var value in new[] { probe.Credential, probe.Configuration["Security:CertificatePfx"]!, probe.Configuration["Security:CertificatePassword"]!, probe.Root })
            Assert.DoesNotContain(value, text);
        if (receipt is not null) Assert.DoesNotContain(receipt, text);
    }
}
