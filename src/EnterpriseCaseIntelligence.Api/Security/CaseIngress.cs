using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace EnterpriseCaseIntelligence.Api.Security;

internal static class CaseIngress
{
    public static void RejectAutomaticForwarding(IConfiguration configuration)
    {
        // The framework's opt-in startup filter clears the proxy allowlist and
        // executes before our pipeline. Never combine it with either mode.
        if (string.Equals(configuration["ForwardedHeaders_Enabled"], "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(configuration["ASPNETCORE_FORWARDEDHEADERS_ENABLED"], "true", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Unrestricted forwarding is unsupported.");
    }

    public static IPAddress? ReadProxyAddress(IConfiguration configuration)
    {
        var ingress = configuration["Security:Ingress"] ?? "DirectHttps";
        if (ingress == "DirectHttps") return null;
        if (ingress != "LoopbackProxy" || configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
            throw new ArgumentException("Invalid ingress configuration.");
        if (!Uri.TryCreate(configuration["urls"], UriKind.Absolute, out var backend) || backend.Scheme != "http" ||
            backend.Port == 0 || backend.AbsolutePath != "/" || backend.Query.Length > 0 || backend.Fragment.Length > 0 || backend.UserInfo.Length > 0 ||
            !IPAddress.TryParse(backend.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(Normalize(address)))
            throw new ArgumentException("One explicit HTTP loopback binding is required.");
        return Normalize(address);
    }

    public static void UseCaseIngress(this WebApplication app, SecuritySettings settings)
    {
        if (settings.ProxyAddress is not { } proxy) return;
        // Check the physical TCP peer and unmodified protocol before the
        // framework consumes headers. Only a same-host, single-hop proxy is supported.
        app.Use(async (context, next) =>
        {
            var peer = context.Connection.RemoteIpAddress;
            var headers = context.Request.Headers;
            if (peer is null || !Normalize(peer).Equals(proxy) || context.Request.Scheme != "http" ||
                headers["X-Forwarded-Proto"].Count != 1 || headers["X-Forwarded-Proto"][0] != "https" ||
                headers.ContainsKey("X-Forwarded-Host") || headers.ContainsKey("X-Forwarded-For") ||
                headers.ContainsKey("X-Forwarded-Prefix") || headers.ContainsKey("Forwarded"))
            { await InvalidOrigin().ExecuteAsync(context); return; }
            await next(context);
        });
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1,
            RequireHeaderSymmetry = true
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Add(proxy);
        app.UseForwardedHeaders(options);
    }

    public static IResult InvalidOrigin() => Results.Problem(statusCode: 400, title: "Bad Request", type: "about:blank",
        extensions: new Dictionary<string, object?> { ["code"] = "invalid_origin" });
    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
