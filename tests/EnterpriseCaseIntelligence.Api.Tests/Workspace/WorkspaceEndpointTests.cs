using System.Net;
using EnterpriseCaseIntelligence.Api.Tests.Cases;

namespace EnterpriseCaseIntelligence.Api.Tests.Workspace;

public sealed class WorkspaceEndpointTests : CaseApiTest
{
    [Theory]
    [InlineData("/workspace", "text/html")]
    [InlineData("/workspace/", "text/html")]
    [InlineData("/workspace/app.css", "text/css")]
    [InlineData("/workspace/app.js", "text/javascript")]
    public async Task OnlyMappedAssetsHaveCorrectMediaTypesAndBrowserRestrictions(string path, string mediaType)
    {
        using var response = await Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'none'", policy);
        Assert.Contains("script-src 'self'", policy);
        Assert.Contains("connect-src 'self'", policy);
        Assert.Contains("form-action 'none'", policy);
        Assert.DoesNotContain("unsafe-inline", policy);
        Assert.NotEmpty(await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/workspace/appsettings.json")]
    [InlineData("/workspace/cases.db")]
    [InlineData("/workspace/Assets/workspace.html")]
    [InlineData("/workspace/not-a-route")]
    public async Task WorkspaceDoesNotExposeAnArbitraryDirectoryOrFallback(string path)
    {
        using var response = await Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
