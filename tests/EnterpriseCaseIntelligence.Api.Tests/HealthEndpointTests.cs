using System.Net;
using EnterpriseCaseIntelligence.Api.Tests.Cases;

namespace EnterpriseCaseIntelligence.Api.Tests;

public class HealthEndpointTests : CaseApiTest
{
    [Fact]
    public async Task GetHealthReturnsOk()
    {
        using var response = await Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
