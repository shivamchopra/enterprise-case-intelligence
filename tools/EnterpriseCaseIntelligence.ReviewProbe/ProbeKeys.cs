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
    public int KeyCount { get; }
    public string KeyRingFingerprint { get; }

    public ProbeKeys(IConfiguration configuration, ProbeSettings settings, bool initialize, string contentRoot, bool readOnly = false)
    {
        if (initialize && readOnly) throw new ArgumentException();
        // Validate/import credentials before directory I/O or any key generation.
        if (configuration["Security:CertificateSource"] != "Configuration") throw new ArgumentException();
        using var material = ProtectionCertificate.Read(configuration);
        _certificate = material.Load();
        try
        {
            var directory = settings.KeyDirectory;
            ProbeStorage.ValidateLocation(directory, contentRoot);
            if (!Directory.Exists(directory) || ProbeStorage.IsDirectoryLink(directory) || ProbeStorage.UnixOwnerOnly(directory) == false) throw new ArgumentException();
            // Hold for the entire process lifetime, including framework key rollover.
            // A retained lock file is harmless; only an exclusive open grants ownership.
            _lease = new FileStream(Path.Combine(directory, ".probe.lock"), readOnly ? FileMode.Open : FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                var entries = Directory.GetFileSystemEntries(directory).Where(path => Path.GetFileName(path) != ".probe.lock").ToArray();
                if (initialize ? entries.Length != 0 : entries.Length == 0) throw new ArgumentException();
                if (entries.Length > 100 || entries.Any(path => !Path.GetFileName(path).StartsWith("key-", StringComparison.Ordinal) ||
                    Path.GetExtension(path) != ".xml" || !File.Exists(path) || new FileInfo(path).LinkTarget is not null || new FileInfo(path).Length > 64 * 1024)) throw new ArgumentException();
                _provider = RetainedProtection.Create(directory, "ECI-ReviewProbe:" + settings.EnvironmentId, _certificate, entries.Length, readOnly);
                _protector = _provider.CreateProtector("operator-receipt-v1", settings.Revision);
                if (_protector.Unprotect(_protector.Protect(Payload)) != Payload) throw new CryptographicException();
                // Snapshot under the same exclusive lease, after descriptor validation.
                // Preparation uses this with automatic generation disabled and opens
                // only an existing lease file; it cannot repair or initialize state.
                var files = Directory.GetFiles(directory, "key-*.xml").Order(StringComparer.Ordinal).ToArray();
                KeyCount = files.Length;
                using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach (var file in files)
                {
                    fingerprint.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(file)));
                    fingerprint.AppendData([0]);
                    fingerprint.AppendData(SHA256.HashData(File.ReadAllBytes(file)));
                }
                KeyRingFingerprint = Convert.ToHexString(fingerprint.GetHashAndReset());
            }
            catch { (_provider as IDisposable)?.Dispose(); _lease.Dispose(); throw; }
        }
        catch { _certificate.Dispose(); throw; }
    }

    public string Issue() => _protector.Protect(Payload);
    public bool Verify(string receipt)
    {
        try { return _protector.Unprotect(receipt) == Payload; }
        catch (CryptographicException) { return false; }
    }

    public void Dispose() { (_provider as IDisposable)?.Dispose(); _lease.Dispose(); _certificate.Dispose(); }
}
