using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EnterpriseCaseIntelligence.Api.Security;
using Microsoft.AspNetCore.DataProtection;

namespace EnterpriseCaseIntelligence.ReviewProbe;

internal sealed class ProbeKeys : IDisposable
{
    private const string Payload = "ECI independent review-probe receipt v1";
    private readonly X509Certificate2 _certificate;
    private readonly FileStream _lease;
    private readonly IDataProtectionProvider _provider;
    private readonly IDataProtector _protector;

    public ProbeKeys(IConfiguration configuration, ProbeSettings settings, bool initialize, string contentRoot)
    {
        // Validate/import credentials before directory I/O or any key generation.
        if (configuration["Security:CertificateSource"] != "Configuration") throw new ArgumentException();
        using var material = ProtectionCertificate.Read(configuration);
        _certificate = material.Load();
        try
        {
            var directory = settings.KeyDirectory;
            if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory) ||
                Within(directory, AppContext.BaseDirectory) || Within(directory, contentRoot) ||
                new DirectoryInfo(directory).LinkTarget is not null) throw new ArgumentException();
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(directory) &
                (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new ArgumentException();
            // Hold for the entire process lifetime, including framework key rollover.
            // A retained lock file is harmless; only an exclusive open grants ownership.
            _lease = new FileStream(Path.Combine(directory, ".probe.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                var entries = Directory.GetFileSystemEntries(directory).Where(path => Path.GetFileName(path) != ".probe.lock").ToArray();
                if (initialize ? entries.Length != 0 : entries.Length == 0) throw new ArgumentException();
                if (entries.Length > 100 || entries.Any(path => !Path.GetFileName(path).StartsWith("key-", StringComparison.Ordinal) ||
                    Path.GetExtension(path) != ".xml" || !File.Exists(path) || new FileInfo(path).LinkTarget is not null || new FileInfo(path).Length > 64 * 1024)) throw new ArgumentException();
                _provider = RetainedProtection.Create(directory, "ECI-ReviewProbe:" + settings.EnvironmentId, _certificate, entries.Length);
                _protector = _provider.CreateProtector("operator-receipt-v1", settings.Revision);
                if (_protector.Unprotect(_protector.Protect(Payload)) != Payload) throw new CryptographicException();
            }
            catch { (_provider as IDisposable)?.Dispose(); _lease.Dispose(); throw; }
        }
        catch { _certificate.Dispose(); throw; }
    }

    // Keep state outside both the published artifact and the current content root.
    private static bool Within(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative == "." || !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    public string Issue() => _protector.Protect(Payload);
    public bool Verify(string receipt)
    {
        try { return _protector.Unprotect(receipt) == Payload; }
        catch (CryptographicException) { return false; }
    }

    public void Dispose() { (_provider as IDisposable)?.Dispose(); _lease.Dispose(); _certificate.Dispose(); }
}
