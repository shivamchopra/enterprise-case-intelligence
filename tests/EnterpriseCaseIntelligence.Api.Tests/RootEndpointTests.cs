using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EnterpriseCaseIntelligence.Api.Tests;

public class RootEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task GetRootReturnsOnlyFoundationMetadata()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, body.RootElement.EnumerateObject().Count());
        Assert.Equal("Enterprise Case Intelligence", body.RootElement.GetProperty("service").GetString());
        Assert.Equal("foundation", body.RootElement.GetProperty("status").GetString());
    }
}
