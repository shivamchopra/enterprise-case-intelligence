using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EnterpriseCaseIntelligence.ReviewProbe;

namespace EnterpriseCaseIntelligence.Api.Tests.Hosting;

// Disposable synthetic material and actual child processes, never private review
// identity, local case storage, trusted system certificates or cloud resources.
internal sealed class ReviewProbeProcess : IAsyncDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("eci-probe-tests-").FullName;
    public string Keys => Path.Combine(Root, "keys");
    public string Artifact { get; private set; }
    public string WorkingDirectory { get; }
    public Uri Backend { get; }
    public string Credential { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public Dictionary<string, string?> Configuration { get; }
    public X509Certificate2 Certificate { get; }
    private Process? _process;
    private Task<string>? _stdout;
    private Task<string>? _stderr;

    public ReviewProbeProcess()
    {
        Directory.CreateDirectory(Keys);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Keys, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        WorkingDirectory = Directory.CreateDirectory(Path.Combine(Root, "content")).FullName;
        Artifact = Environment.GetEnvironmentVariable("ECI_TEST_PROBE_ASSEMBLY") ?? typeof(ProbeProgram).Assembly.Location;
        using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start();
        Backend = new($"http://127.0.0.1:{((IPEndPoint)socket.LocalEndpoint).Port}");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        Certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
        Configuration = new()
        {
            ["urls"] = Backend.AbsoluteUri,
            ["Probe:Enabled"] = "true",
            ["Probe:PublicOrigin"] = "https://synthetic-probe.example",
            ["Probe:EnvironmentId"] = "synthetic-probe",
            ["Probe:Revision"] = "revision-1",
            ["Probe:KeyDirectory"] = Keys,
            ["Probe:OperatorCredential"] = Credential,
            ["Security:CertificateSource"] = "Configuration",
            ["Security:CertificatePfx"] = Convert.ToBase64String(Certificate.Export(X509ContentType.Pfx, "synthetic-probe-PFX-password")),
            ["Security:CertificatePassword"] = "synthetic-probe-PFX-password"
        };
    }

    public Process Launch(string command, IReadOnlyDictionary<string, string?>? changes = null, string? configurationFile = null)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Artifact); start.ArgumentList.Add(command);
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("Probe", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Security", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Kestrel", StringComparison.OrdinalIgnoreCase) || name == "ECI_PROBE_CONFIGURATION" || name == "urls").ToArray()) start.Environment.Remove(name);
        if (configurationFile is not null) start.Environment["ECI_PROBE_CONFIGURATION"] = configurationFile;
        else foreach (var pair in Configuration) start.Environment[pair.Key.Replace(":", "__")] = pair.Value;
        if (changes is not null) foreach (var pair in changes) start.Environment[pair.Key.Replace(":", "__")] = pair.Value;
        return Process.Start(start)!;
    }

    public async Task<(int ExitCode, string Output)> Command(string command, IReadOnlyDictionary<string, string?>? changes = null, string? file = null)
    {
        using var process = Launch(command, changes, file);
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        return (process.ExitCode, await output + await error);
    }

    public async Task Start()
    {
        _process = Launch("serve");
        _stdout = _process.StandardOutput.ReadToEndAsync(); _stderr = _process.StandardError.ReadToEndAsync();
        using var client = Client(authenticated: false);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited) throw new InvalidOperationException("Synthetic probe did not start: " + await _stdout + await _stderr);
            try { using var response = await client.GetAsync("/probe"); if (response.StatusCode == HttpStatusCode.Unauthorized) return; }
            catch (HttpRequestException) { }
            await Task.Delay(50);
        }
        throw new TimeoutException("Synthetic probe startup timed out.");
    }

    public HttpClient Client(bool authenticated = true)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = Backend, Timeout = TimeSpan.FromSeconds(5) };
        if (authenticated) client.DefaultRequestHeaders.Authorization = new("Bearer", Credential);
        return client;
    }

    public Dictionary<string, string> KeyHashes() => Directory.GetFiles(Keys, "key-*.xml").ToDictionary(path => Path.GetFileName(path)!, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    public void ReplaceArtifact()
    {
        var destination = Directory.CreateDirectory(Path.Combine(Root, "replacement-release")).FullName;
        var source = Path.GetDirectoryName(Artifact)!;
        foreach (var name in new[] { "EnterpriseCaseIntelligence.ReviewProbe.dll", "EnterpriseCaseIntelligence.ReviewProbe.deps.json", "EnterpriseCaseIntelligence.ReviewProbe.runtimeconfig.json" })
            File.Copy(Path.Combine(source, name), Path.Combine(destination, name));
        // The SDK can resolve PKCS from the installed shared framework and omit
        // its app-local copy. Preserve exactly the source artifact's layout.
        var pkcs = Path.Combine(source, "System.Security.Cryptography.Pkcs.dll");
        if (File.Exists(pkcs)) File.Copy(pkcs, Path.Combine(destination, Path.GetFileName(pkcs)));
        var runtimes = Path.Combine(source, "runtimes");
        if (Directory.Exists(runtimes)) foreach (var file in Directory.GetFiles(runtimes, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, "runtimes", Path.GetRelativePath(runtimes, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        Artifact = Path.Combine(destination, "EnterpriseCaseIntelligence.ReviewProbe.dll");
    }

    public async Task<string> Stop()
    {
        if (_process is null) return "";
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var output = await _stdout! + await _stderr!;
        _process.Dispose(); _process = null;
        return output;
    }

    public async ValueTask DisposeAsync() { await Stop(); Certificate.Dispose(); Directory.Delete(Root, recursive: true); }
}
