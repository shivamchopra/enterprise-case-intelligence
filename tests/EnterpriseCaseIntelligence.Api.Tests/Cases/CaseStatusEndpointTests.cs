using System.Net;
using System.Net.Http.Json;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class CaseStatusEndpointTests : CaseApiTest
{
    [Theory]
    [InlineData("New", "New", true)]
    [InlineData("New", "InReview", true)]
    [InlineData("New", "Closed", false)]
    [InlineData("InReview", "New", false)]
    [InlineData("InReview", "InReview", true)]
    [InlineData("InReview", "Closed", true)]
    [InlineData("Closed", "New", false)]
    [InlineData("Closed", "InReview", false)]
    [InlineData("Closed", "Closed", true)]
    public async Task StatusOperationEnforcesCompleteTransitionMatrix(string initial, string requested, bool isAllowed)
    {
        var created = await CreateAsync();
        if (initial is "InReview" or "Closed")
        {
            await SetStatusAsync(created.Id, "InReview");
        }

        if (initial == "Closed")
        {
            await SetStatusAsync(created.Id, "Closed");
        }

        var before = await ReadAsync(created.Id);
        using var response = await Client.PutAsJsonAsync(PathFor(created.Id) + "/status", new { status = requested });

        if (isAllowed)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var expected = before with { Status = requested };
            Assert.Equal(expected, await ReadCaseAsync(response));
            Assert.Equal(expected, await ReadAsync(created.Id));
        }
        else
        {
            await AssertProblemAsync(response, HttpStatusCode.Conflict, "invalid_transition");
            Assert.Equal(before, await ReadAsync(created.Id));
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"status":null}""")]
    [InlineData("""{"status":0}""")]
    [InlineData("""{"status":true}""")]
    [InlineData("""{"status":[]}""")]
    [InlineData("""{"status":"0"}""")]
    [InlineData("""{"status":"new"}""")]
    [InlineData("""{"status":"inreview"}""")]
    [InlineData("""{"status":" New "}""")]
    [InlineData("""{"status":""}""")]
    [InlineData("""{"status":"SYNTHETIC_SENSITIVE_MARKER"}""")]
    [InlineData("""{"Status":"InReview"}""")]
    public async Task InvalidStatusDoesNotMutateCase(string json)
    {
        var created = await CreateAsync();

        using var response = await SendJsonAsync(HttpMethod.Put, PathFor(created.Id) + "/status", json);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed", "status");
        Assert.Equal(created, await ReadAsync(created.Id));
    }

    [Theory]
    [InlineData("""{"status":"InReview","title":"SYNTHETIC_SENSITIVE_MARKER"}""", "body")]
    [InlineData("""{"status":"New","status":"InReview"}""", "status")]
    public async Task ExtraAndDuplicateFieldsAreRejectedWithoutMutation(string json, string field)
    {
        var created = await CreateAsync();

        using var response = await SendJsonAsync(HttpMethod.Put, PathFor(created.Id) + "/status", json);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed", field);
        Assert.Equal(created, await ReadAsync(created.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task InvalidJsonDoesNotMutateCase(string json)
    {
        var created = await CreateAsync();

        using var response = await SendJsonAsync(HttpMethod.Put, PathFor(created.Id) + "/status", json);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed", "body");
        Assert.Equal(created, await ReadAsync(created.Id));
    }

    [Fact]
    public async Task UnsupportedMediaTypeDoesNotMutateCase()
    {
        var created = await CreateAsync();

        using var response = await SendJsonAsync(HttpMethod.Put, PathFor(created.Id) + "/status", "not-json", "text/plain");

        await AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        Assert.Equal(created, await ReadAsync(created.Id));
    }

    [Fact]
    public async Task ForwardLifecycleCanBeImmediatelyRetried()
    {
        var created = await CreateAsync();

        var reviewing = await SetStatusAsync(created.Id, "InReview");
        Assert.Equal(reviewing, await SetStatusAsync(created.Id, "InReview"));
        var closed = await SetStatusAsync(created.Id, "Closed");
        Assert.Equal(closed, await SetStatusAsync(created.Id, "Closed"));
        Assert.Equal(closed, await ReadAsync(created.Id));
    }

    [Fact]
    public async Task SimultaneousSameStatusRequestsBothSucceed()
    {
        var created = await CreateAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<CaseRepresentation> UpdateAsync()
        {
            await start.Task;
            return await SetStatusAsync(created.Id, "InReview");
        }

        var first = UpdateAsync();
        var second = UpdateAsync();
        start.SetResult();
        var results = await Task.WhenAll(first, second);

        var expected = created with { Status = "InReview" };
        Assert.All(results, result => Assert.Equal(expected, result));
        Assert.Equal(expected, await ReadAsync(created.Id));
    }
}
