using System.Net;
using EnterpriseCaseIntelligence.Api.Cases;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class UnexpectedErrorTests : CaseApiTest
{
    [Fact]
    public async Task UnexpectedFailureDoesNotExposeExceptionDetails()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<InMemoryCaseStore>(_ =>
                throw new InvalidOperationException("SYNTHETIC_SENSITIVE_MARKER"))));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(PathFor(Guid.NewGuid()));

        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
    }
}
