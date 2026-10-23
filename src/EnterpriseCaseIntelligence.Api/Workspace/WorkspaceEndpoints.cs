using System.Text;

namespace EnterpriseCaseIntelligence.Api.Workspace;

internal static class WorkspaceEndpoints
{
    public static void MapWorkspace(this IEndpointRouteBuilder endpoints)
    {
        MapAsset(endpoints, "/workspace", "workspace.html", "text/html");
        MapAsset(endpoints, "/workspace/app.css", "app.css", "text/css");
        MapAsset(endpoints, "/workspace/app.js", "app.js", "text/javascript");
    }

    private static void MapAsset(IEndpointRouteBuilder endpoints, string path, string resource, string contentType)
    {
        using var stream = typeof(WorkspaceEndpoints).Assembly.GetManifestResourceStream("Workspace." + resource)
            ?? throw new InvalidOperationException("Missing embedded workspace asset.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = reader.ReadToEnd();
        endpoints.MapGet(path, (HttpResponse response) =>
        {
            response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
            response.Headers.XContentTypeOptions = "nosniff";
            response.Headers.XFrameOptions = "DENY";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers.CacheControl = "no-store";
            return Results.Text(content, contentType, Encoding.UTF8);
        });
    }
}
