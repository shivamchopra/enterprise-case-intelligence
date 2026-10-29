using System.Security.Cryptography;
using System.Text;

namespace EnterpriseCaseIntelligence.ReviewProbe;

internal sealed class ProbeSettings : IDisposable
{
    public Uri Origin { get; }
    public string EnvironmentId { get; }
    public string Revision { get; }
    public string KeyDirectory { get; }
    private readonly byte[] _authorization;

    public ProbeSettings(IConfiguration configuration)
    {
        string Required(string name, int maximum)
        {
            var value = configuration["Probe:" + name];
            if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl)) throw new ArgumentException();
            return value;
        }
        if (configuration["Probe:Enabled"] != "true") throw new ArgumentException();
        Origin = ReadOrigin(Required("PublicOrigin", 300));
        EnvironmentId = Required("EnvironmentId", 80);
        Revision = Required("Revision", 80);
        if (!Identifier(EnvironmentId) || !Identifier(Revision)) throw new ArgumentException();
        KeyDirectory = Required("KeyDirectory", 1024);
        var credential = ReadCredential(configuration);
        _authorization = Encoding.ASCII.GetBytes("Bearer " + credential);
    }

    internal static Uri ReadOrigin(string? value)
    {
        if (value is null || value.Length > 300 || !Uri.TryCreate(value, UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
            origin.AbsolutePath != "/" || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.Port == 0)
            throw new ArgumentException();
        return origin;
    }

    internal static string ReadCredential(IConfiguration configuration)
    {
        var value = configuration["Probe:OperatorCredential"];
        // A canonical 32-random-byte lowercase hex credential, generated privately.
        // Format validation cannot establish entropy; generation is an operator duty.
        if (value is null || value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) throw new ArgumentException();
        return value;
    }

    public bool Authenticated(HttpRequest request)
    {
        var values = request.Headers.Authorization;
        if (values.Count != 1 || values[0] is not { Length: 71 } value) return false;
        Span<byte> candidate = stackalloc byte[71];
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] > 127) return false;
            candidate[i] = (byte)value[i];
        }
        var equal = CryptographicOperations.FixedTimeEquals(candidate, _authorization);
        CryptographicOperations.ZeroMemory(candidate);
        return equal;
    }

    public bool HostMatches(HostString host)
    {
        try { return string.Equals(host.Host.Trim('[', ']'), Origin.IdnHost.Trim('[', ']'), StringComparison.OrdinalIgnoreCase) && (host.Port ?? 443) == Origin.Port; }
        catch (FormatException) { return false; }
    }

    private static bool Identifier(string value) => value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public void Dispose() => CryptographicOperations.ZeroMemory(_authorization);
}
