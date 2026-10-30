using System.Text;
using Microsoft.Extensions.Primitives;

namespace EnterpriseCaseIntelligence.ReviewProbe;

internal static class ProbeEndpoint
{
    private static readonly string[] ObservedHeaders = ["Forwarded", "X-Forwarded-Proto", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Prefix", "X-Original-Proto", "X-Original-Host", "X-MS-CLIENT-PRINCIPAL", "X-MS-CLIENT-PRINCIPAL-ID", "X-MS-CLIENT-PRINCIPAL-NAME"];

    public static async Task Handle(HttpContext context, ProbeSettings settings, ProbeKeys keys)
    {
        if (!Authorize(context, settings)) return;
        try
        {
            // No cookies, browser origins, URL credentials, CORS, identities or cases.
            if (context.Request.Method == "GET" && context.Request.Path == "/probe")
            {
                var peer = context.Connection.RemoteIpAddress;
                if (peer?.IsIPv4MappedToIPv6 == true) peer = peer.MapToIPv4();
                await Results.Json(new
                {
                    schemaVersion = 1,
                    deploymentReady = false,
                    observations = new
                    {
                        originalScheme = context.Request.Scheme is "http" or "https" ? context.Request.Scheme : "other",
                        originalPeer = peer?.ToString(),
                        hostMatches = settings.HostMatches(context.Request.Host),
                        headers = ObservedHeaders.ToDictionary(name => name, name => Classify(context.Request.Headers[name], name == "X-Forwarded-Proto"))
                    },
                    receipt = keys.Issue()
                }).ExecuteAsync(context);
                return;
            }
            if (context.Request.Method == "POST" && context.Request.Path == "/probe/verify")
            {
                if (context.Request.ContentType != "text/plain" || context.Request.ContentLength > 2048 || context.Request.Headers.ContainsKey("Content-Encoding"))
                { Deny(context, 400); return; }
                using var reader = new StreamReader(context.Request.Body, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                var receipt = await ProbeProgram.ReadBounded(reader, 2048);
                if (!ReceiptSyntax(receipt)) { Deny(context, 400); return; }
                await Results.Json(new { schemaVersion = 1, deploymentReady = false, retainedReceiptValid = keys.Verify(receipt) }).ExecuteAsync(context);
                return;
            }
            Deny(context, 404);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            if (!context.Response.HasStarted) { Deny(context, exception is ArgumentException or BadHttpRequestException or DecoderFallbackException ? 400 : 500); }
            else context.Abort();
        }
    }

    internal static bool Authorize(HttpContext context, ProbeSettings settings)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
        // Gate before storage inspection, route selection or body handling in both phases.
        if (!settings.Authenticated(context.Request)) { Deny(context, 401); return false; }
        if (context.Request.QueryString.HasValue || context.Request.Headers.ContainsKey("Origin")) { Deny(context, 400); return false; }
        return true;
    }

    internal static void Deny(HttpContext context, int status)
    {
        context.Response.StatusCode = status;
        // An unread/rejected HTTP/1 body must not race connection reuse when the
        // server's finite drain limit closes it after sending the denial.
        if (context.Request.Protocol == "HTTP/1.1" && (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")))
            context.Response.Headers.Connection = "close";
    }

    internal static bool ReceiptSyntax(string receipt) => receipt.Length is > 0 and <= 2048 && receipt.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static object Classify(StringValues values, bool scheme)
    {
        var count = 0;
        var oversized = values.Count > 8;
        foreach (var value in values.Take(8))
        {
            if (value is null) continue;
            if (value.Length > 1024) { oversized = true; break; }
            count = Math.Min(9, count + 1 + value.Count(c => c == ','));
        }
        return new
        {
            cardinality = Math.Min(9, count), // 9 means nine or more, never raw input.
            classification = oversized ? "oversized" : count == 0 ? "absent" : count != 1 ? "multiple" :
                scheme && values[0] == "https" ? "https" : scheme && values[0] == "http" ? "http" : "other"
        };
    }
}
