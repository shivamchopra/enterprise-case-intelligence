using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace EnterpriseCaseIntelligence.Api.Security;

// Owns the decoded configuration buffer until SecuritySettings has finished
// validation/import. It must be cleared even when later configuration is invalid.
internal sealed class ProtectionCertificate : IDisposable
{
    private const int MaximumBytes = 32 * 1024;
    private const int MaximumEncodedLength = ((MaximumBytes + 2) / 3) * 4;
    private readonly string? _path;
    private readonly string? _password;
    private readonly byte[]? _data;
    private readonly int _length;

    private ProtectionCertificate(string? path, string? password, byte[]? data = null, int length = 0)
    { _path = path; _password = password; _data = data; _length = length; }

    public static ProtectionCertificate Read(IConfiguration configuration)
    {
        var source = configuration["Security:CertificateSource"] ?? "File";
        var path = configuration["Security:CertificatePath"];
        var encoded = configuration["Security:CertificatePfx"];
        var password = configuration["Security:CertificatePassword"];
        if (source == "File")
        {
            if (encoded is not null || string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
                throw Invalid();
            return new(path, password);
        }
        if (source != "Configuration" || path is not null || encoded is null || encoded.Length is < 4 or > MaximumEncodedLength ||
            encoded.Any(char.IsWhiteSpace) || password is null || password.Length is < 1 or > 1024 || string.IsNullOrWhiteSpace(password))
            throw Invalid();
        var data = new byte[MaximumBytes];
        if (!Convert.TryFromBase64String(encoded, data, out var length) || length == 0)
        {
            CryptographicOperations.ZeroMemory(data);
            throw Invalid();
        }
        return new(null, password, data, length);
    }

    public X509Certificate2 Load()
    {
        // macOS needs a temporary native key; disposing the returned certificate
        // releases it. Windows/Linux use ephemeral private-key import.
        var flags = OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
        var certificate = _data is null
            ? X509CertificateLoader.LoadPkcs12FromFile(_path!, _password, flags)
            : X509CertificateLoader.LoadPkcs12(_data.AsSpan(0, _length), _password, flags,
                new Pkcs12LoaderLimits { MaxKeys = 1, MaxCertificates = 10 });
        try
        {
            using var rsa = certificate.GetRSAPrivateKey();
            if (rsa is null || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow)
                throw Invalid();
            if (_data is not null) ValidateEncryptedPfx();
            return certificate;
        }
        catch { certificate.Dispose(); throw; }
    }

    private void ValidateEncryptedPfx()
    {
        // Import first: the framework loader enforces its KDF/MAC work limits
        // before this second inspection/decryption of the bounded PFX envelope.
        var info = Pkcs12Info.Decode(_data!.AsMemory(0, _length), out var consumed, skipCopy: true);
        if (consumed != _length || info.IntegrityMode != Pkcs12IntegrityMode.Password || !info.VerifyMac(_password)) throw Invalid();
        var shroudedKeys = 0;
        foreach (var contents in info.AuthenticatedSafe)
        {
            if (contents.ConfidentialityMode == Pkcs12ConfidentialityMode.Password) contents.Decrypt(_password);
            else if (contents.ConfidentialityMode != Pkcs12ConfidentialityMode.None) throw Invalid();
            foreach (var bag in contents.GetBags())
            {
                // Support an ordinary certificate chain and exactly one encrypted
                // private-key bag. Reject unencrypted keys and nested/opaque bags.
                if (bag is Pkcs12ShroudedKeyBag) shroudedKeys++;
                else if (bag is not Pkcs12CertBag) throw Invalid();
            }
        }
        if (shroudedKeys != 1) throw Invalid();
    }

    private static ArgumentException Invalid() => new("Invalid protection certificate configuration.");
    public void Dispose() { if (_data is not null) CryptographicOperations.ZeroMemory(_data); }
}
