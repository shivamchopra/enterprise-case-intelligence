using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace EnterpriseCaseIntelligence.Api.Security;

internal sealed class SecuritySettings : IDisposable
{
    public bool LocalDemo { get; private init; }
    public string Authority { get; private init; } = "";
    public string Issuer { get; private init; } = "";
    public string ClientId { get; private init; } = "";
    public string ClientSecret { get; private init; } = "";
    public string RoleClaim { get; private init; } = "";
    public string ReaderRole { get; private init; } = "";
    public string WorkerRole { get; private init; } = "";
    public string EnvironmentId { get; private init; } = "";
    public string Revision { get; private init; } = "";
    public Uri Origin { get; private init; } = null!;
    public string KeyDirectory { get; private init; } = "";
    public X509Certificate2? Certificate { get; private init; }

    public static SecuritySettings Read(IConfiguration configuration)
    {
        string Required(string name)
        {
            var value = configuration["Security:" + name];
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)) throw new ArgumentException("Invalid security configuration.");
            return value;
        }
        var mode = Required("Mode");
        if (mode == "LocalDemo")
        {
            if (configuration.GetSection("Security").GetChildren().Any(child => child.Key != "Mode") ||
                configuration.GetSection("Kestrel:Endpoints").GetChildren().Any()) throw new ArgumentException("Invalid local security configuration.");
            var urls = configuration["urls"]?.Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (urls is not { Length: > 0 } || urls.Any(url => !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/" || !IsLoopbackHost(uri.Host))) throw new ArgumentException("Explicit loopback URLs required.");
            return new() { LocalDemo = true };
        }
        if (mode != "Oidc") throw new ArgumentException("Unknown security mode.");
        var authority = Https(Required("Authority"));
        var issuer = Required("Issuer");
        _ = Https(issuer);
        var origin = Https(Required("PublicOrigin"));
        if (origin.AbsolutePath != "/") throw new ArgumentException("Origin must have no path.");
        var environment = Required("EnvironmentId");
        var revision = Required("SessionRevision");
        if (environment.Length > 80 || revision.Length > 80) throw new ArgumentException("Invalid isolation settings.");
        var keyDirectory = Required("KeyDirectory");
        var certificatePath = Required("CertificatePath");
        if (!Path.IsPathFullyQualified(keyDirectory) || !Directory.Exists(keyDirectory) ||
            !Path.IsPathFullyQualified(certificatePath)) throw new ArgumentException("Invalid security paths.");
        var reader = Required("ReaderRole");
        var worker = Required("CaseworkerRole");
        var roleClaim = Required("RoleClaim");
        if (reader == worker || roleClaim is "sub" or "iss" or "aud" or "nonce" || roleClaim.StartsWith("eci_", StringComparison.Ordinal))
            throw new ArgumentException("Invalid role mapping.");
        var clientId = Required("ClientId");
        var clientSecret = Required("ClientSecret");
        // macOS does not support EphemeralKeySet; default loading creates a
        // temporary native key that is released when the certificate is disposed.
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, configuration["Security:CertificatePassword"], OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
        if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow)
        { certificate.Dispose(); throw new ArgumentException("Invalid protection certificate."); }
        return new()
        {
            Authority = authority.AbsoluteUri.TrimEnd('/'),
            Issuer = issuer,
            Origin = origin,
            ClientId = clientId,
            ClientSecret = clientSecret,
            RoleClaim = roleClaim,
            ReaderRole = reader,
            WorkerRole = worker,
            EnvironmentId = environment,
            Revision = revision,
            KeyDirectory = keyDirectory,
            Certificate = certificate
        };
    }

    private static Uri Https(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) throw new ArgumentException("HTTPS configuration required.");
        return uri;
    }

    public static bool IsLoopbackHost(string host) => host == "localhost" || IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    public void Dispose() => Certificate?.Dispose();
}
