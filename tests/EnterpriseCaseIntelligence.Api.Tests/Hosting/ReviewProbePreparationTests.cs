using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using EnterpriseCaseIntelligence.ReviewProbe;
using Microsoft.Extensions.Configuration;

namespace EnterpriseCaseIntelligence.Api.Tests.Hosting;

public sealed class ReviewProbePreparationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationRunsBeforeKeysOrCertificatesWithoutInitializingOrCreatingAPath(bool missingDirectory)
    {
        await using var probe = new ReviewProbeProcess();
        probe.Configuration.Remove("Security:CertificatePfx"); probe.Configuration.Remove("Security:CertificatePassword");
        if (missingDirectory) Directory.Delete(probe.Keys);
        await probe.Start("prepare"); using var client = probe.Client();
        using var json = await Status(client);
        Assert.Equal(missingDirectory ? "missing_directory" : "not_initialized", json.RootElement.GetProperty("state").GetString());
        Assert.False(json.RootElement.GetProperty("retainedKeysVerified").GetBoolean());
        Assert.False(json.RootElement.GetProperty("deploymentReady").GetBoolean());
        Assert.False(json.RootElement.GetProperty("platformIsolationVerified").GetBoolean());
        Assert.Equal(missingDirectory ? JsonValueKind.Null : OperatingSystem.IsWindows() ? JsonValueKind.Null : JsonValueKind.True,
            json.RootElement.GetProperty("unixOwnerOnly").ValueKind);
        Assert.Equal(!missingDirectory, Directory.Exists(probe.Keys));
        if (!missingDirectory) Assert.Empty(Directory.GetFileSystemEntries(probe.Keys));
        using var normal = await client.GetAsync("/probe"); Assert.Equal(HttpStatusCode.NotFound, normal.StatusCode);
        AssertPrivate(await probe.Stop(), probe);
        Assert.Equal(1, (await probe.Command("serve")).ExitCode);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("content")]
    [InlineData("artifact")]
    [InlineData("alias-content")]
    [InlineData("case-content")]
    public async Task PreparationRefusesPathsThatCouldReadStateFromTheRelease(string kind)
    {
        if (kind == "alias-content" && OperatingSystem.IsWindows()) return;
        if (kind == "case-content" && !OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) return;
        await using var probe = new ReviewProbeProcess();
        var alias = Path.Combine(probe.Root, "content-alias");
        if (kind == "alias-content") Directory.CreateSymbolicLink(alias, probe.WorkingDirectory);
        var path = kind switch { "relative" => "PRIVATE_RELATIVE_PATH", "content" => probe.WorkingDirectory, "alias-content" => Path.Combine(alias, "keys"), "case-content" => Path.Combine(probe.Root, "CONTENT"), _ => Path.GetDirectoryName(probe.Artifact)! };
        var result = await probe.Command("prepare", new Dictionary<string, string?> { ["Probe:KeyDirectory"] = path });
        Assert.Equal(1, result.ExitCode); AssertPrivate(result.Output, probe);
        Assert.DoesNotContain(path, result.Output); Assert.Empty(Directory.GetFileSystemEntries(probe.Keys));
    }

    [Fact]
    public async Task PreparationReportsASymlinkWithoutFollowingItOrInitializingItsTarget()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var probe = new ReviewProbeProcess();
        var target = Path.Combine(probe.Root, "target"); Directory.Move(probe.Keys, target);
        Directory.CreateSymbolicLink(probe.Keys, target);
        await probe.Start("prepare"); using var client = probe.Client();
        using var status = await Status(client);
        Assert.Equal("unsafe_directory", status.RootElement.GetProperty("state").GetString());
        Assert.False(status.RootElement.GetProperty("retainedKeysVerified").GetBoolean());
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public async Task SeparateInitializationWhilePreparingProducesAnOldReceiptValidAfterPromotionAndArtifactReplacement()
    {
        await using var probe = new ReviewProbeProcess();
        await probe.Start("prepare"); using var client = probe.Client();
        using (var before = await Status(client)) Assert.False(before.RootElement.GetProperty("retainedKeysVerified").GetBoolean());
        Assert.Empty(Directory.GetFileSystemEntries(probe.Keys));
        Assert.Equal(0, (await probe.Command("init-keys")).ExitCode);
        var hashes = probe.KeyHashes();
        string receipt; string fingerprint;
        using (var status = await Status(client))
        {
            Assert.Equal("keys_verified", status.RootElement.GetProperty("state").GetString());
            Assert.True(status.RootElement.GetProperty("retainedKeysVerified").GetBoolean());
            Assert.Equal(hashes.Count, status.RootElement.GetProperty("keyCount").GetInt32());
            receipt = status.RootElement.GetProperty("receipt").GetString()!;
            fingerprint = status.RootElement.GetProperty("keyRingFingerprint").GetString()!;
            Assert.Equal(64, fingerprint.Length); AssertPrivate(status.RootElement.GetRawText(), probe);
        }
        for (var i = 0; i < 3; i++)
        {
            using var status = await Status(client);
            Assert.Equal(fingerprint, status.RootElement.GetProperty("keyRingFingerprint").GetString());
        }
        Assert.Equal(1, (await probe.Command("init-keys")).ExitCode); // Retained ring is not overwritten.
        Assert.Equal(hashes.OrderBy(pair => pair.Key), probe.KeyHashes().OrderBy(pair => pair.Key));
        AssertPrivate(await probe.Stop(), probe);
        probe.ReplaceArtifact();
        await probe.Start();
        Assert.True(await ReviewProbeTests.Verify(client, receipt));
        using var preparationRoute = await client.GetAsync("/probe/setup"); Assert.Equal(HttpStatusCode.NotFound, preparationRoute.StatusCode);
        await probe.Stop();
        Assert.Equal(hashes.OrderBy(pair => pair.Key), probe.KeyHashes().OrderBy(pair => pair.Key));
    }

    [Fact]
    public async Task PreparationAuthenticatesBeforeInspectingAndProvidesNoHttpInitializationOrMutation()
    {
        await using var probe = new ReviewProbeProcess();
        await probe.Start("prepare");
        using var anonymous = probe.Client(authenticated: false);
        anonymous.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", "PRIVATE_FORGED_ROLE");
        anonymous.DefaultRequestHeaders.Add("Cookie", "operator=" + probe.Credential);
        foreach (var path in new[] { "/probe/setup", "/probe", "/cases", "/init-keys" })
        {
            using var response = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsStringAsync());
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Null(response.Headers.Location); Assert.False(response.Headers.Contains("Set-Cookie"));
        }
        using var client = probe.Client();
        foreach (var path in new[] { "/probe/setup", "/init-keys", "/probe/verify" })
        {
            using var response = await client.PostAsync(path, new StringContent("PRIVATE_UNTRUSTED_BODY"));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var query = await client.GetAsync("/probe/setup?directory=PRIVATE_OTHER_PATH"); Assert.Equal(HttpStatusCode.BadRequest, query.StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "https://synthetic-probe.example");
        using var browser = await client.GetAsync("/probe/setup"); Assert.Equal(HttpStatusCode.BadRequest, browser.StatusCode);
        Assert.False(browser.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Empty(Directory.GetFileSystemEntries(probe.Keys));
        AssertPrivate(await probe.Stop(), probe);
    }

    [Fact]
    public async Task UnsafeDirectoryIsReportedWithoutReadingKeyStateOrLoadingCertificate()
    {
        if (OperatingSystem.IsWindows()) return; // Unix mode check; Windows ACL review remains external.
        await using var probe = new ReviewProbeProcess();
        File.SetUnixFileMode(probe.Keys, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        await File.WriteAllTextAsync(Path.Combine(probe.Keys, "PRIVATE_UNEXPECTED_FILE"), "PRIVATE_STORAGE_CONTENT");
        probe.Configuration["Security:CertificatePfx"] = "PRIVATE_INVALID_PFX";
        await probe.Start("prepare"); using var client = probe.Client();
        using var status = await Status(client);
        Assert.Equal("unsafe_directory", status.RootElement.GetProperty("state").GetString());
        Assert.False(status.RootElement.GetProperty("unixOwnerOnly").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.RootElement.GetProperty("receipt").ValueKind);
        Assert.DoesNotContain("PRIVATE_", status.RootElement.GetRawText());
        Assert.Single(Directory.GetFileSystemEntries(probe.Keys));
        Assert.Equal("PRIVATE_STORAGE_CONTENT", await File.ReadAllTextAsync(Path.Combine(probe.Keys, "PRIVATE_UNEXPECTED_FILE")));
        AssertPrivate(await probe.Stop(), probe);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("certificate")]
    [InlineData("missing-certificate")]
    [InlineData("corrupt-key")]
    [InlineData("missing-key")]
    [InlineData("missing-lease")]
    public async Task UnavailableRetainedStateNeverBecomesSuccessfulOrGeneratesReplacementKeys(string fault)
    {
        await using var probe = new ReviewProbeProcess();
        Assert.Equal(0, (await probe.Command("init-keys")).ExitCode);
        switch (fault)
        {
            case "password": probe.Configuration["Security:CertificatePassword"] = "PRIVATE_WRONG_PASSWORD"; break;
            case "certificate":
                await using (var other = new ReviewProbeProcess()) probe.Configuration["Security:CertificatePfx"] = other.Configuration["Security:CertificatePfx"];
                break;
            case "missing-certificate": probe.Configuration.Remove("Security:CertificatePfx"); break;
            case "corrupt-key": await File.WriteAllTextAsync(Directory.GetFiles(probe.Keys, "key-*.xml").Single(), "<key>PRIVATE_CORRUPTION</key>"); break;
            case "missing-key": File.Delete(Directory.GetFiles(probe.Keys, "key-*.xml").Single()); break;
            case "missing-lease": File.Delete(Path.Combine(probe.Keys, ".probe.lock")); break;
        }
        var hashes = probe.KeyHashes(); var files = Directory.GetFileSystemEntries(probe.Keys).Order().ToArray();
        await probe.Start("prepare"); using var client = probe.Client();
        using var status = await Status(client);
        Assert.False(status.RootElement.GetProperty("retainedKeysVerified").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.RootElement.GetProperty("receipt").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.RootElement.GetProperty("keyRingFingerprint").ValueKind);
        AssertPrivate(status.RootElement.GetRawText(), probe); Assert.DoesNotContain("PRIVATE_", status.RootElement.GetRawText());
        Assert.Equal(files, Directory.GetFileSystemEntries(probe.Keys).Order().ToArray());
        Assert.Equal(hashes.OrderBy(pair => pair.Key), probe.KeyHashes().OrderBy(pair => pair.Key));
        AssertPrivate(await probe.Stop(), probe);
    }

    [Fact]
    public async Task HeldWriterLeasePreventsAnInspectionSuccessAndIsNotBypassed()
    {
        await using var probe = new ReviewProbeProcess();
        Assert.Equal(0, (await probe.Command("init-keys")).ExitCode);
        await probe.Start("prepare"); using var client = probe.Client();
        using (var lease = File.Open(Path.Combine(probe.Keys, ".probe.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var status = await Status(client);
            Assert.False(status.RootElement.GetProperty("retainedKeysVerified").GetBoolean());
            Assert.Equal("unavailable", status.RootElement.GetProperty("state").GetString());
        }
        using var recovered = await Status(client);
        Assert.True(recovered.RootElement.GetProperty("retainedKeysVerified").GetBoolean());
    }

    [Fact]
    public async Task InspectionCannotRollOverAKeyWhoseActivationIsInTheFuture()
    {
        await using var probe = new ReviewProbeProcess();
        Assert.Equal(0, (await probe.Command("init-keys")).ExitCode);
        var path = Directory.GetFiles(probe.Keys, "key-*.xml").Single();
        var xml = XDocument.Load(path);
        xml.Root!.Element("activationDate")!.Value = DateTimeOffset.UtcNow.AddDays(2).ToString("O");
        xml.Root.Element("expirationDate")!.Value = DateTimeOffset.UtcNow.AddDays(90).ToString("O");
        xml.Save(path);
        var hashes = probe.KeyHashes();
        await probe.Start("prepare"); using var client = probe.Client();
        using var status = await Status(client);
        Assert.False(status.RootElement.GetProperty("deploymentReady").GetBoolean());
        Assert.Equal(hashes.OrderBy(pair => pair.Key), probe.KeyHashes().OrderBy(pair => pair.Key));
    }

    [Fact]
    public async Task SetupClientUsesOnlyTheFixedHttpsPreparationRoute()
    {
        using var handler = new SetupHandler(); using var client = new HttpClient(handler);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Probe:PublicOrigin"] = "https://synthetic-probe.example", ["Probe:OperatorCredential"] = new string('a', 64) }).Build();
        var result = await ProbeClient.Send(configuration, null, client, setupStatus: true);
        Assert.Contains("\"deploymentReady\":false", result);
        await Assert.ThrowsAsync<ArgumentException>(() => ProbeClient.Send(configuration, "synthetic-receipt", client, setupStatus: true));
        Assert.Equal(1, handler.Calls);
    }

    private static async Task<JsonDocument> Status(HttpClient client)
    {
        using var response = await client.GetAsync("/probe/setup"); response.EnsureSuccessStatusCode();
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static void AssertPrivate(string text, ReviewProbeProcess probe)
    {
        foreach (var value in new[] { probe.Root, probe.Credential, probe.Configuration.GetValueOrDefault("Security:CertificatePfx"), probe.Configuration.GetValueOrDefault("Security:CertificatePassword") })
            if (value is not null) Assert.DoesNotContain(value, text);
    }

    private sealed class SetupHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://synthetic-probe.example/probe/setup", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"schemaVersion\":1,\"deploymentReady\":false,\"state\":\"not_initialized\"}", Encoding.UTF8, "application/json") });
        }
    }
}
