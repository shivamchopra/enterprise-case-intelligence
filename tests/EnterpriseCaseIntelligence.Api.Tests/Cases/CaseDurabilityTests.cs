using System.Net;
using System.Net.Http.Json;
using EnterpriseCaseIntelligence.Api.Cases;
using EnterpriseCaseIntelligence.Domain.Cases;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class CaseDurabilityTests : CaseApiTest
{
    [Theory]
    [InlineData("Synthetic '); DROP TABLE cases; --", "Quotes are synthetic input, not SQL.")]
    [InlineData("Synthetic 😀 title", "Synthetic\0description\twith internal whitespace")]
    public async Task ParameterizedTextRoundTripsExactlyAfterRestart(string title, string description)
    {
        var created = await CreateAsync(title, description);
        Factory.Dispose();
        using var reopened = Database.CreateFactory();
        using var client = reopened.CreateClient();
        using var response = await client.GetAsync(PathFor(created.Id));
        Assert.Equal(created, await ReadCaseAsync(response));
        Assert.Equal(title, created.Title);
        Assert.Equal(description, created.Description);
        Assert.Equal(1, Database.Count());
    }

    [Fact]
    public async Task RestartPreservesEveryStatusIdentityAndTextAndAllowsFurtherCreation()
    {
        var first = await CreateAsync("  Synthetic new  ", ValidDescription);
        var second = await CreateAsync();
        var third = await CreateAsync();
        second = await SetStatusAsync(second.Id, "InReview");
        await SetStatusAsync(third.Id, "InReview");
        third = await SetStatusAsync(third.Id, "Closed");
        Factory.Dispose();

        using var factory = Database.CreateFactory();
        using var client = factory.CreateClient();
        foreach (var expected in new[] { first, second, third })
        {
            using var response = await client.GetAsync(PathFor(expected.Id));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(expected, await ReadCaseAsync(response));
            using var retry = await client.PutAsJsonAsync(PathFor(expected.Id) + "/status", new { status = expected.Status });
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            Assert.Equal(expected, await ReadCaseAsync(retry));
        }

        using var create = await client.PostAsJsonAsync("/cases", new { title = ValidTitle, description = ValidDescription });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var fourth = await ReadCaseAsync(create);
        Assert.DoesNotContain(fourth.Id, new[] { first.Id, second.Id, third.Id });
        Assert.Equal(4, Database.Count());
    }

    [Fact]
    public void SnapshotsAndFailedDomainOperationsDoNotChangeCommittedState()
    {
        var store = new SqliteCaseStore(Database.Database);
        var created = store.Create(ValidTitle, ValidDescription);
        var read = store.Find(created.Id)!;
        Assert.True(store.ChangeStatus(created.Id, CaseStatus.Closed).IsConflict);
        Assert.ThrowsAny<ArgumentException>(() => store.Create(" ", ValidDescription));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ChangeStatus(created.Id, (CaseStatus)42));
        Assert.Equal(created, new SqliteCaseStore(Database.Database).Find(created.Id));

        var updated = store.ChangeStatus(created.Id, CaseStatus.InReview).Case;
        var copy = read with { Status = CaseStatus.Closed, Title = "Caller copy" };
        Assert.Equal(CaseStatus.Closed, copy.Status);
        Assert.Equal(CaseStatus.New, read.Status);
        Assert.Equal(CaseStatus.New, created.Status);
        Assert.Equal(updated, new SqliteCaseStore(Database.Database).Find(created.Id));
        Assert.Equal(1, Database.Count());
    }

    [Fact]
    public async Task ConcurrentRequestsAndRejectedWritesHaveValidDurableOrder()
    {
        var first = await CreateAsync();
        var duplicate = await CreateAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> Update(string status)
        {
            await start.Task;
            return await Client.PutAsJsonAsync(PathFor(first.Id) + "/status", new { status });
        }

        var review = Update("InReview");
        var close = Update("Closed");
        start.SetResult();
        using var reviewResponse = await review;
        using var closeResponse = await close;
        Assert.Equal(HttpStatusCode.OK, reviewResponse.StatusCode);
        Assert.True(closeResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict);
        var expectedStatus = closeResponse.IsSuccessStatusCode ? CaseStatus.Closed : CaseStatus.InReview;
        Factory.Dispose();
        var reopened = new SqliteCaseStore(Database.Database);
        Assert.Equal(expectedStatus, reopened.Find(first.Id)!.Status);
        Assert.Equal(expectedStatus, reopened.History(first.Id)![^1].ToStatus);
        Assert.Equal(expectedStatus == CaseStatus.Closed ? 3 : 2, reopened.History(first.Id)!.Count);
        Assert.Single(reopened.History(duplicate.Id)!);
        Assert.Equal(CaseStatus.New, reopened.Find(duplicate.Id)!.Status);
        Assert.Equal(first.Title, reopened.Find(first.Id)!.Title);
        Assert.Equal(2, Database.Count());
    }

    [Theory]
    [InlineData("New", "Closed")]
    [InlineData("InReview", "New")]
    [InlineData("Closed", "InReview")]
    public async Task RejectedTransitionRemainsUnchangedAfterReopening(string initial, string requested)
    {
        var created = await CreateAsync();
        if (initial is "InReview" or "Closed") await SetStatusAsync(created.Id, "InReview");
        if (initial == "Closed") await SetStatusAsync(created.Id, "Closed");
        using var rejected = await Client.PutAsJsonAsync(PathFor(created.Id) + "/status", new { status = requested });
        await AssertProblemAsync(rejected, HttpStatusCode.Conflict, "invalid_transition");
        Assert.Equal(initial, new SqliteCaseStore(Database.Database).Find(created.Id)!.Status.ToString());
    }
}
