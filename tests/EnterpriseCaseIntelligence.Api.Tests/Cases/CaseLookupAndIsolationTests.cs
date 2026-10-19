using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class CaseLookupAndIsolationTests : CaseApiTest
{
    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("11111111111111111111111111111111")]
    [InlineData("SYNTHETIC_SENSITIVE_MARKER")]
    public async Task InvalidIdentifiersReturnValidationProblemsForBothCaseRoutes(string id)
    {
        using var get = await Client.GetAsync("/cases/" + id);
        await AssertProblemAsync(get, HttpStatusCode.BadRequest, "validation_failed", "id");

        using var put = await Client.PutAsJsonAsync($"/cases/{id}/status", new { status = "InReview" });
        await AssertProblemAsync(put, HttpStatusCode.BadRequest, "validation_failed", "id");
    }

    [Fact]
    public async Task ValidMissingIdentifierReturnsNotFoundForReadAndStatusChange()
    {
        var path = PathFor(Guid.NewGuid());
        using var get = await Client.GetAsync(path);
        await AssertProblemAsync(get, HttpStatusCode.NotFound, "case_not_found");

        using var put = await Client.PutAsJsonAsync(path + "/status", new { status = "Closed" });
        await AssertProblemAsync(put, HttpStatusCode.NotFound, "case_not_found");

        using var after = await Client.GetAsync(path);
        await AssertProblemAsync(after, HttpStatusCode.NotFound, "case_not_found");
    }

    [Fact]
    public async Task InvalidStatusIsValidatedBeforeLookingUpMissingCase()
    {
        using var response = await Client.PutAsJsonAsync(PathFor(Guid.NewGuid()) + "/status", new { status = "SYNTHETIC_SENSITIVE_MARKER" });

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed", "status");
    }

    [Fact]
    public async Task IndependentHostsDoNotShareCases()
    {
        var created = await CreateAsync();
        using var otherDatabase = new TemporaryCaseDatabase();
        using var otherFactory = otherDatabase.CreateFactory();
        using var otherClient = otherFactory.CreateClient();

        var firstRead = Client.GetAsync(PathFor(created.Id));
        var otherRead = otherClient.GetAsync(PathFor(created.Id));
        using var firstResponse = await firstRead;
        using var otherResponse = await otherRead;

        Assert.Equal(created, await ReadCaseAsync(firstResponse));
        await AssertProblemAsync(otherResponse, HttpStatusCode.NotFound, "case_not_found");
        using var otherCreate = await otherClient.PostAsJsonAsync("/cases", new { title = ValidTitle, description = ValidDescription });
        Assert.Equal(HttpStatusCode.Created, otherCreate.StatusCode);
        var other = await ReadCaseAsync(otherCreate);
        using var lookupInFirst = await Client.GetAsync(PathFor(other.Id));
        await AssertProblemAsync(lookupInFirst, HttpStatusCode.NotFound, "case_not_found");
    }

    [Fact]
    public async Task ANewHostReopensCommittedCasesAfterPreviousHostIsDisposed()
    {
        Guid oldId;
        using (var oldFactory = Database.CreateFactory())
        using (var oldClient = oldFactory.CreateClient())
        {
            using var response = await oldClient.PostAsJsonAsync("/cases", new { title = ValidTitle, description = ValidDescription });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            oldId = (await ReadCaseAsync(response)).Id;
        }

        using var newFactory = Database.CreateFactory();
        using var newClient = newFactory.CreateClient();
        using var get = await newClient.GetAsync(PathFor(oldId));

        Assert.Equal(oldId, (await ReadCaseAsync(get)).Id);
    }
}
