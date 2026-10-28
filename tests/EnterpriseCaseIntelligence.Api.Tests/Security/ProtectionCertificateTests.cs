using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace EnterpriseCaseIntelligence.Api.Tests.Security;

public sealed class ProtectionCertificateTests
{
    [Theory]
    [InlineData("unknown-source")]
    [InlineData("implicit-source-with-pfx")]
    [InlineData("file-with-pfx")]
    [InlineData("configuration-with-path")]
    [InlineData("configuration-with-empty-path")]
    [InlineData("missing-pfx")]
    [InlineData("missing-password")]
    [InlineData("empty-password")]
    [InlineData("whitespace-password")]
    [InlineData("oversized-password")]
    [InlineData("invalid-base64")]
    [InlineData("whitespace-base64")]
    [InlineData("oversized-encoding")]
    [InlineData("oversized-decoding")]
    [InlineData("not-pfx")]
    [InlineData("wrong-password")]
    [InlineData("expired")]
    [InlineData("not-yet-valid")]
    [InlineData("public-only")]
    [InlineData("non-rsa")]
    [InlineData("unencrypted-key")]
    [InlineData("no-mac")]
    [InlineData("multiple-keys")]
    [InlineData("trailing-data")]
    [InlineData("tampered-mac")]
    public async Task InvalidConfigurationFailsWithSafeDiagnosticBeforeKeyOrStoreChanges(string fault)
    {
        await using var host = new SecurityHost();
        var settings = host.ConfigurationCertificate();
        const string password = "synthetic-PFX-password";
        var path = Path.Combine(host.Database.DirectoryPath, "test.pfx");
        switch (fault)
        {
            case "unknown-source": settings["Security:CertificateSource"] = "PRIVATE_UNKNOWN_SOURCE"; break;
            case "implicit-source-with-pfx": settings["Security:CertificateSource"] = null; settings["Security:CertificatePath"] = path; break;
            case "file-with-pfx": settings["Security:CertificateSource"] = "File"; settings["Security:CertificatePath"] = path; break;
            case "configuration-with-path": settings["Security:CertificatePath"] = path; break;
            case "configuration-with-empty-path": settings["Security:CertificatePath"] = ""; break;
            case "missing-pfx": settings["Security:CertificatePfx"] = null; break;
            case "missing-password": settings["Security:CertificatePassword"] = null; break;
            case "empty-password": settings["Security:CertificatePassword"] = ""; break;
            case "whitespace-password": settings["Security:CertificatePassword"] = "   "; break;
            case "oversized-password": settings["Security:CertificatePassword"] = new string('p', 1025); break;
            case "invalid-base64": settings["Security:CertificatePfx"] = "PRIVATE_PFX_MARKER"; break;
            case "whitespace-base64": settings["Security:CertificatePfx"] += "\n"; break;
            case "oversized-encoding": settings["Security:CertificatePfx"] = new string('A', 43693); break;
            case "oversized-decoding": settings["Security:CertificatePfx"] = new string('A', 43692); break;
            case "not-pfx": settings["Security:CertificatePfx"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("PRIVATE_PFX_MARKER")); break;
            case "wrong-password": settings["Security:CertificatePassword"] = "PRIVATE_WRONG_PASSWORD"; break;
            case "expired": settings["Security:CertificatePfx"] = CertificateBytes(password, DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1)); break;
            case "not-yet-valid": settings["Security:CertificatePfx"] = CertificateBytes(password, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(2)); break;
            case "non-rsa": settings["Security:CertificatePfx"] = CertificateBytes(password, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2), elliptic: true); break;
            case "public-only":
                using (var certificate = X509CertificateLoader.LoadCertificate(host.Certificate.RawData))
                    settings["Security:CertificatePfx"] = Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, password));
                break;
            case "unencrypted-key":
            case "no-mac":
            case "multiple-keys": settings["Security:CertificatePfx"] = CustomPfx(host.Certificate, password, fault); break;
            case "trailing-data": settings["Security:CertificatePfx"] = Convert.ToBase64String([.. Convert.FromBase64String(settings["Security:CertificatePfx"]!), 0]); break;
            case "tampered-mac":
                var tampered = Convert.FromBase64String(settings["Security:CertificatePfx"]!);
                tampered[^1] ^= 1;
                Assert.False(Pkcs12Info.Decode(tampered, out _).VerifyMac(password));
                settings["Security:CertificatePfx"] = Convert.ToBase64String(tampered);
                break;
        }
        var before = File.ReadAllBytes(host.Database.Path);
        var result = await RefusedProcess(host, settings);
        Assert.Equal(before, File.ReadAllBytes(host.Database.Path));
        Assert.Empty(Directory.GetFiles(host.Keys));
        Assert.DoesNotContain("PRIVATE_", result.Output);
        Assert.DoesNotContain(password, result.Output);
        Assert.DoesNotContain(path, result.Output);
        Assert.DoesNotContain(host.Database.Path, result.Output);
        if (settings["Security:CertificatePfx"] is { Length: > 20 } encoded) Assert.DoesNotContain(encoded, result.Output);
    }

    [Fact]
    public async Task ConfigurationSourceNeedsNoPfxFileAndDoesNotExposeMaterialThroughSessionBootstrap()
    {
        await using var host = new SecurityHost();
        var settings = host.ConfigurationCertificate();
        File.Delete(Path.Combine(host.Database.DirectoryPath, "test.pfx"));
        var before = File.ReadAllBytes(host.Database.Path);
        host.Start(configurationOverrides: settings);
        using var client = host.CreateClient(host.Ticket("Reader"));
        using var response = await client.GetAsync("/auth/session");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(settings["Security:CertificatePfx"]!, text);
        Assert.DoesNotContain(settings["Security:CertificatePassword"]!, text);
        using var read = await client.GetAsync("/cases");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Empty(Directory.GetFiles(host.Database.DirectoryPath, "*.pfx", SearchOption.AllDirectories));
        Assert.Equal(before, File.ReadAllBytes(host.Database.Path));
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task SourceSwitchRetainsEncryptedKeysCasesHistoryAndReplayWhileRevisionCanRevokeSessions(bool initialConfiguration, bool nextConfiguration, bool revoke)
    {
        await using var host = new SecurityHost();
        host.Start(configurationOverrides: initialConfiguration ? host.ConfigurationCertificate() : null);
        var ticket = host.Ticket("Caseworker");
        using var client = host.CreateClient(ticket);
        await CaseSecurityTests.Csrf(client);
        var key = $"\"{Guid.NewGuid():D}\"";
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);
        var payload = new { title = "Synthetic retained credential test", description = "Fictional case only." };
        using var created = await client.PostAsJsonAsync("/cases", payload);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var original = await created.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(original);
        var id = json.RootElement.GetProperty("id").GetString();
        using var review = await client.PutAsJsonAsync($"/cases/{id}/status", new { status = "InReview" });
        using var closed = await client.PutAsJsonAsync($"/cases/{id}/status", new { status = "Closed" });
        Assert.Equal(HttpStatusCode.OK, review.StatusCode); Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        var before = File.ReadAllBytes(host.Database.Path);
        var keys = KeyFiles(host);
        Assert.All(keys, xml => { Assert.Contains("encryptedSecret", xml); Assert.DoesNotContain("<masterKey", xml); });
        Assert.NotEmpty(keys);
        await host.Factory.DisposeAsync();
        host.Start(revision: revoke ? "revoked-revision" : "test-revision", configurationOverrides: nextConfiguration ? host.ConfigurationCertificate() : null);
        using var old = host.CreateClient(ticket);
        using var read = await old.GetAsync("/cases");
        Assert.Equal(revoke ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, read.StatusCode);
        if (revoke)
        {
            using var deniedWrite = await old.PostAsJsonAsync("/cases", payload);
            Assert.Equal(HttpStatusCode.Unauthorized, deniedWrite.StatusCode);
        }
        using var restored = host.CreateClient(host.Ticket("Caseworker", revision: revoke ? "revoked-revision" : "test-revision"));
        await CaseSecurityTests.Csrf(restored);
        restored.DefaultRequestHeaders.Add("Idempotency-Key", key);
        using var replay = await restored.PostAsJsonAsync("/cases", payload);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(original, await replay.Content.ReadAsStringAsync());
        using var history = await restored.GetAsync($"/cases/{id}/history");
        using var historyJson = JsonDocument.Parse(await history.Content.ReadAsStringAsync());
        Assert.Equal(3, historyJson.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(1, host.Database.Count());
        Assert.Equal(before, File.ReadAllBytes(host.Database.Path));
        Assert.Equal(keys, KeyFiles(host));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidReplacementCannotAddKeysOrTouchTheRetainedStore(bool differentValidCertificate)
    {
        await using var host = new SecurityHost(); host.Start();
        var ticket = host.Ticket("Reader");
        var keys = KeyFiles(host);
        await host.Factory.DisposeAsync();
        var before = File.ReadAllBytes(host.Database.Path);
        var settings = host.ConfigurationCertificate();
        if (differentValidCertificate)
            settings["Security:CertificatePfx"] = CertificateBytes("synthetic-PFX-password", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        else settings["Security:CertificatePassword"] = "PRIVATE_WRONG_PASSWORD";
        await RefusedProcess(host, settings);
        Assert.Equal(keys, KeyFiles(host));
        Assert.Equal(before, File.ReadAllBytes(host.Database.Path));
        host.Start();
        using var recovered = host.CreateClient(ticket);
        using var read = await recovered.GetAsync("/cases");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    private static string[] KeyFiles(SecurityHost host) => Directory.GetFiles(host.Keys, "key-*.xml").Order(StringComparer.Ordinal).Select(File.ReadAllText).ToArray();

    private static async Task<(int Code, string Output)> RefusedProcess(SecurityHost host, Dictionary<string, string?> overrides)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Security:Mode"] = "Oidc",
            ["Security:Authority"] = SecurityHost.Issuer,
            ["Security:Issuer"] = SecurityHost.Issuer,
            ["Security:ClientId"] = SecurityHost.ClientId,
            ["Security:ClientSecret"] = "test-only-secret",
            ["Security:PublicOrigin"] = "https://localhost",
            ["Security:EnvironmentId"] = "synthetic-tests",
            ["Security:SessionRevision"] = "test-revision",
            ["Security:KeyDirectory"] = host.Keys,
            ["Security:RoleClaim"] = "roles",
            ["Security:ReaderRole"] = "Reader",
            ["Security:CaseworkerRole"] = "Caseworker",
            ["CaseStorage:DatabasePath"] = host.Database.Path
        };
        foreach (var setting in overrides) settings[setting.Key] = setting.Value;
        var result = await ApiProcess.RunWithEnvironmentAsync(settings.ToDictionary(setting => setting.Key.Replace(":", "__"), setting => setting.Value), "--urls", "https://127.0.0.1:0");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Security configuration unavailable", result.Output);
        Assert.DoesNotContain("Now listening on:", result.Output);
        Assert.DoesNotContain("Exception", result.Output);
        return (result.ExitCode, result.Output);
    }

    private static string CertificateBytes(string password, DateTimeOffset start, DateTimeOffset end, bool elliptic = false)
    {
        using var rsa = RSA.Create(2048);
        using var ecdsa = ECDsa.Create();
        var request = elliptic ? new CertificateRequest("CN=synthetic-protection", ecdsa, HashAlgorithmName.SHA256)
            : new CertificateRequest("CN=synthetic-protection", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(start, end);
        return Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, password));
    }

    private static string CustomPfx(X509Certificate2 certificate, string password, string fault)
    {
        using var rsa = certificate.GetRSAPrivateKey()!;
        var safe = new Pkcs12SafeContents(); safe.AddCertificate(certificate);
        var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000);
        if (fault == "unencrypted-key") safe.AddKeyUnencrypted(rsa);
        else safe.AddShroudedKey(rsa, password, pbe);
        if (fault == "multiple-keys") safe.AddShroudedKey(rsa, password, pbe);
        var builder = new Pkcs12Builder(); builder.AddSafeContentsUnencrypted(safe);
        if (fault == "no-mac") builder.SealWithoutIntegrity();
        else builder.SealWithMac(password, HashAlgorithmName.SHA256, 1000);
        return Convert.ToBase64String(builder.Encode());
    }
}
