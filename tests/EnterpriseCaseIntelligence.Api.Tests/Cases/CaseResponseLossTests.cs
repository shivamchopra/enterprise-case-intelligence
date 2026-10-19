using System.Net;
using System.Net.Http.Json;
using EnterpriseCaseIntelligence.Api.Cases;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class CaseResponseLossTests : CaseApiTest
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LostSuccessfulResponseDoesNotUndoCommit(bool creating)
    {
        var existing = await CreateAsync();
        Factory.Dispose();
        var filter = new LoseResponseFilter();
        using (var factory = Database.CreateFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(filter))))
        using (var client = factory.CreateClient())
        {
            var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                using var response = creating
                    ? await client.PostAsJsonAsync("/cases", new { title = ValidTitle, description = ValidDescription })
                    : await client.PutAsJsonAsync(PathFor(existing.Id) + "/status", new { status = "InReview" });
            });
            Assert.Contains("SIMULATED_RESPONSE_LOSS", exception.ToString());
            Assert.True(filter.SuppressedSuccess);
        }

        using var reopened = Database.CreateFactory();
        using var reader = reopened.CreateClient();
        if (creating)
        {
            // The fixture can inspect the store; an unacknowledged-create client does not know this ID.
            Assert.Equal(2, Database.Count());
            Assert.Equal(2L, Database.Scalar("SELECT count(DISTINCT id) FROM cases WHERE status='New'"));
        }
        else
        {
            using var response = await reader.GetAsync(PathFor(existing.Id));
            var committed = await ReadCaseAsync(response);
            Assert.Equal(existing with { Status = "InReview" }, committed);
            using var retry = await reader.PutAsJsonAsync(PathFor(existing.Id) + "/status", new { status = "InReview" });
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            Assert.Equal(committed, await ReadCaseAsync(retry));
            Assert.Equal(1, Database.Count());
        }
    }

    private sealed class LoseResponseFilter : IStartupFilter
    {
        public bool SuppressedSuccess { get; private set; }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                var original = context.Response.Body;
                using var buffer = new MemoryStream();
                context.Response.Body = buffer;
                try
                {
                    await continuation();
                    if (context.Response.StatusCode is 200 or 201)
                    {
                        SuppressedSuccess = true;
                        throw new IOException("SIMULATED_RESPONSE_LOSS");
                    }

                    buffer.Position = 0;
                    await buffer.CopyToAsync(original);
                }
                finally
                {
                    context.Response.Body = original;
                }
            });
            next(app);
        };
    }
}
