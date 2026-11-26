using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EnterpriseCaseIntelligence.Api.Cases;
using EnterpriseCaseIntelligence.Domain.Cases;

namespace EnterpriseCaseIntelligence.Api.Tests.Security;

public sealed class FiveClientReviewTests
{
    private sealed record CaseValue(Guid Id, string Title, string Description, string Status);

    [Fact]
    public async Task FiveIndependentClientsPreserveAccessReplayPriorityAndHistoryUnderContention()
    {
        await using var host = new SecurityHost();
        host.Start();

        using var firstWorker = host.CreateClient(host.Ticket("Caseworker", "worker-one"));
        using var secondWorker = host.CreateClient(host.Ticket("Caseworker", "worker-two"));
        using var thirdWorker = host.CreateClient(host.Ticket("Caseworker", "worker-three"));
        using var firstReader = host.CreateClient(host.Ticket("Reader", "reader-one"));
        using var secondReader = host.CreateClient(host.Ticket("Reader", "reader-two"));
        await Task.WhenAll(
            CaseSecurityTests.Csrf(firstWorker),
            CaseSecurityTests.Csrf(secondWorker),
            CaseSecurityTests.Csrf(thirdWorker),
            CaseSecurityTests.Csrf(firstReader),
            CaseSecurityTests.Csrf(secondReader));

        var payload = new { title = "Synthetic five-client review", description = "Fictional contention rehearsal." };
        var key = '"' + Guid.NewGuid().ToString("D") + '"';
        firstWorker.DefaultRequestHeaders.Add("Idempotency-Key", key);
        using var initial = await firstWorker.PostAsJsonAsync("/cases", payload);
        Assert.Equal(HttpStatusCode.Created, initial.StatusCode);
        var record = await ReadCaseAsync(initial);

        secondWorker.DefaultRequestHeaders.Add("Idempotency-Key", key);
        var creationPhase = await RunTogether(
            () => secondWorker.PostAsJsonAsync("/cases", payload),
            () => thirdWorker.PutAsJsonAsync($"/cases/{record.Id:D}/priority", new { priority = "High" }),
            () => firstReader.GetAsync($"/cases/{record.Id:D}"),
            () => secondReader.GetAsync($"/cases/{record.Id:D}/history"),
            () => secondReader.PostAsJsonAsync("/cases", payload));
        using var replay = creationPhase[0];
        using var priority = creationPhase[1];
        using var details = creationPhase[2];
        using var history = creationPhase[3];
        using var deniedCreate = creationPhase[4];
        Assert.Equal(record, await ReadCaseAsync(replay));
        Assert.Equal(HttpStatusCode.OK, priority.StatusCode);
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        await AssertDeniedAsync(deniedCreate);

        var reviewPhase = await RunTogether(
            () => firstWorker.PutAsJsonAsync($"/cases/{record.Id:D}/status", new { status = "InReview" }),
            () => secondWorker.PutAsJsonAsync($"/cases/{record.Id:D}/status", new { status = "InReview" }),
            () => thirdWorker.GetAsync($"/cases/{record.Id:D}/priority"),
            () => firstReader.GetAsync("/cases?priority=High"),
            () => secondReader.PutAsJsonAsync($"/cases/{record.Id:D}/priority", new { priority = "Low" }));
        using var firstReview = reviewPhase[0];
        using var secondReview = reviewPhase[1];
        using var priorityRead = reviewPhase[2];
        using var filteredRead = reviewPhase[3];
        using var deniedPriority = reviewPhase[4];
        Assert.Equal(HttpStatusCode.OK, firstReview.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondReview.StatusCode);
        Assert.Equal(HttpStatusCode.OK, priorityRead.StatusCode);
        Assert.Equal(HttpStatusCode.OK, filteredRead.StatusCode);
        await AssertDeniedAsync(deniedPriority);

        var closePhase = await RunTogether(
            () => firstWorker.PutAsJsonAsync($"/cases/{record.Id:D}/status", new { status = "Closed" }),
            () => thirdWorker.PutAsJsonAsync($"/cases/{record.Id:D}/status", new { status = "Closed" }),
            () => secondWorker.GetAsync($"/cases/{record.Id:D}"),
            () => firstReader.GetAsync($"/cases/{record.Id:D}/history"),
            () => secondReader.PostAsJsonAsync("/cases", payload));
        using var firstClose = closePhase[0];
        using var secondClose = closePhase[1];
        using var closedRead = closePhase[2];
        using var closedHistory = closePhase[3];
        using var deniedAfterClose = closePhase[4];
        Assert.Equal(HttpStatusCode.OK, firstClose.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondClose.StatusCode);
        Assert.Equal(HttpStatusCode.OK, closedRead.StatusCode);
        Assert.Equal(HttpStatusCode.OK, closedHistory.StatusCode);
        await AssertDeniedAsync(deniedAfterClose);

        using var brief = await firstReader.GetAsync($"/cases/{record.Id:D}/closure-brief");
        Assert.Equal(HttpStatusCode.OK, brief.StatusCode);
        using var briefBody = JsonDocument.Parse(await brief.Content.ReadAsStringAsync());
        Assert.Equal("draft", briefBody.RootElement.GetProperty("outcome").GetString());

        var store = new SqliteCaseStore(host.Database.Database);
        var final = store.Find(record.Id)!;
        Assert.Equal(record.Id, final.Id);
        Assert.Equal(CaseStatus.Closed, final.Status);
        Assert.Equal(new(record.Id, CasePriority.High), store.Priority(record.Id));
        var entries = store.History(record.Id)!;
        Assert.Equal(3, entries.Count);
        Assert.Equal(new[] { "New", "InReview", "Closed" }, entries.Select(entry => entry.ToStatus.ToString()).ToArray());
        Assert.Equal(1L, host.Database.Scalar("SELECT count(*) FROM case_creation_keys"));
        Assert.Equal(1, host.Database.Count());
    }

    private static async Task<HttpResponseMessage[]> RunTogether(params Func<Task<HttpResponseMessage>>[] operations)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> Run(Func<Task<HttpResponseMessage>> operation)
        {
            await start.Task;
            return await operation();
        }

        var tasks = operations.Select(Run).ToArray();
        start.SetResult();
        return await Task.WhenAll(tasks);
    }

    private static async Task<CaseValue> ReadCaseAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new(
            body.RootElement.GetProperty("id").GetGuid(),
            body.RootElement.GetProperty("title").GetString()!,
            body.RootElement.GetProperty("description").GetString()!,
            body.RootElement.GetProperty("status").GetString()!);
    }

    private static async Task AssertDeniedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("access_denied", body.RootElement.GetProperty("code").GetString());
        Assert.Null(response.Headers.Location);
    }
}
