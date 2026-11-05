using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EnterpriseCaseIntelligence.Api.Cases;
using EnterpriseCaseIntelligence.Domain.Cases;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class CasePriorityApiTests : CaseListApiTest
{
    private static async Task<string> ReadPriority(HttpResponseMessage response, Guid id)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Headers.Location);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "caseId", "priority" }, json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(id.ToString("D"), json.RootElement.GetProperty("caseId").GetString());
        return json.RootElement.GetProperty("priority").GetString()!;
    }

    [Fact]
    public async Task PriorityResourcesPreserveExactCreationReplayStatusHistoryAndSummaryContracts()
    {
        Client.DefaultRequestHeaders.Add("Idempotency-Key", '"' + Guid.NewGuid().ToString("D") + '"');
        var created = await CreateAsync();
        using (var read = await Client.GetAsync(PathFor(created.Id).ToUpperInvariant().Replace("/CASES/", "/cases/", StringComparison.Ordinal) + "/priority?"))
            Assert.Equal("Normal", await ReadPriority(read, created.Id));
        foreach (var value in new[] { "High", "High", "Low", "Normal", "High" })
        {
            using var change = await Client.PutAsJsonAsync(PathFor(created.Id) + "/priority", new { priority = value });
            Assert.Equal(value, await ReadPriority(change, created.Id));
            Assert.Equal(created, await ReadAsync(created.Id));
            Assert.Single(StoredHistory(created.Id));
        }
        await SetStatusAsync(created.Id, "InReview");
        await SetStatusAsync(created.Id, "Closed");
        using (var noop = await Client.PutAsJsonAsync(PathFor(created.Id) + "/priority", new { priority = "High" }))
            Assert.Equal("High", await ReadPriority(noop, created.Id));
        using (var conflict = await Client.PutAsJsonAsync(PathFor(created.Id) + "/priority", new { priority = "Normal" }))
            await AssertProblemAsync(conflict, HttpStatusCode.Conflict, "case_closed");
        using (var replay = await Client.PostAsJsonAsync("/cases", new { title = ValidTitle, description = ValidDescription }))
        {
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(created, await ReadCaseAsync(replay));
        }
        using (var priority = await Client.GetAsync(PathFor(created.Id) + "/priority"))
            Assert.Equal("High", await ReadPriority(priority, created.Id));
        Assert.Equal("Closed", (await ReadAsync(created.Id)).Status);
        Assert.Equal(3, StoredHistory(created.Id).Length);
        Assert.Equal(1L, Database.Scalar("SELECT count(*) FROM case_creation_keys"));
        using var summary = await Client.GetAsync("/cases/summary");
        using var json = JsonDocument.Parse(await summary.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "closed", "inReview", "new", "total" }, json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(1, json.RootElement.GetProperty("closed").GetInt32());
    }

    [Theory]
    [InlineData("{}", "priority")]
    [InlineData("{\"priority\":null}", "priority")]
    [InlineData("{\"priority\":2}", "priority")]
    [InlineData("{\"priority\":\"2\"}", "priority")]
    [InlineData("{\"priority\":\"high\"}", "priority")]
    [InlineData("{\"priority\":\" High\"}", "priority")]
    [InlineData("{\"priority\":\"High \"}", "priority")]
    [InlineData("{\"priority\":\"High,Low\"}", "priority")]
    [InlineData("{\"priority\":\"\"}", "priority")]
    [InlineData("{\"Priority\":\"High\"}", "priority")]
    [InlineData("{\"priority\":\"High\",\"priority\":\"Low\"}", "priority")]
    [InlineData("{\"priority\":\"High\",\"status\":\"New\"}", "body")]
    [InlineData("{", "body")]
    [InlineData("[]", "body")]
    public async Task MalformedPriorityBodiesFailBeforeStorage(string body, string field)
    {
        File.Delete(Database.Path);
        using var response = await SendJsonAsync(HttpMethod.Put, PathFor(Guid.NewGuid()) + "/priority", body);
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed", field);
        Assert.False(File.Exists(Database.Path));
    }

    [Theory]
    [InlineData("GET", "bad", "", "id")]
    [InlineData("PUT", "00000000-0000-0000-0000-000000000000", "", "id")]
    [InlineData("GET", "00000001-aaaa-4bbb-8ccc-dddddddddddd", "?priority=High", "query")]
    [InlineData("PUT", "00000001-aaaa-4bbb-8ccc-dddddddddddd", "?x=1", "query")]
    public async Task InvalidRoutesFailBeforeStorage(string method, string id, string query, string field)
    {
        File.Delete(Database.Path);
        using var response = await SendJsonAsync(new(method), $"/cases/{id}/priority{query}", "{\"priority\":\"High\"}");
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed", field);
        Assert.False(File.Exists(Database.Path));
    }

    [Theory]
    [InlineData("priority=", "priority")]
    [InlineData("priority=high", "priority")]
    [InlineData("priority=%20High", "priority")]
    [InlineData("priority=1", "priority")]
    [InlineData("priority=High&priority=High", "priority")]
    [InlineData("priority=High&%70riority=Low", "priority")]
    [InlineData("Priority=High", "query")]
    public async Task InvalidFilterFailsBeforeStorage(string query, string field)
    {
        File.Delete(Database.Path);
        using var response = await Client.GetAsync("/cases?" + query);
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed", field);
        Assert.False(File.Exists(Database.Path));
    }

    [Fact]
    public async Task MissingCasesAndStorageAndUnsupportedCreationFieldAreSafe()
    {
        var id = Guid.NewGuid();
        using (var missing = await Client.GetAsync(PathFor(id) + "/priority"))
            await AssertProblemAsync(missing, HttpStatusCode.NotFound, "case_not_found");
        using (var missing = await Client.PutAsJsonAsync(PathFor(id) + "/priority", new { priority = "High" }))
            await AssertProblemAsync(missing, HttpStatusCode.NotFound, "case_not_found");
        using (var extra = await Client.PostAsJsonAsync("/cases", new { title = ValidTitle, description = ValidDescription, priority = "High" }))
            await AssertProblemAsync(extra, HttpStatusCode.BadRequest, "validation_failed", "body");
        File.Delete(Database.Path);
        using (var media = await SendJsonAsync(HttpMethod.Put, PathFor(id) + "/priority", "{}", "text/plain"))
            await AssertProblemAsync(media, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put })
        {
            using var unavailable = await SendJsonAsync(method, PathFor(id) + "/priority", "{\"priority\":\"High\"}");
            await AssertProblemAsync(unavailable, HttpStatusCode.InternalServerError, "internal_error");
        }
        Assert.False(File.Exists(Database.Path));
    }

    [Fact]
    public async Task FilterCombinesBeforePagingReflectsLaterChangesAndKeepsEarlierResults()
    {
        var store = new SqliteCaseStore(Database.Database);
        foreach (var key in Enumerable.Range(1, 125).Reverse())
        {
            Seed(key, title: key % 2 == 0 ? $"Needle {key}" : $"Other {key}");
            store.ChangePriority(FixtureId(key), key % 3 == 0 ? CasePriority.High : CasePriority.Low);
            if (key % 5 == 0) store.ChangeStatus(FixtureId(key), CaseStatus.InReview);
        }
        var expected = Enumerable.Range(1, 125).Where(k => k % 2 == 0 && k % 3 == 0 && k % 5 != 0).Select(FixtureId).ToArray();
        var seen = new List<Guid>();
        Guid? after = null;
        do
        {
            var page = await ListAsync("/cases?priority=High&title=Needle&status=New&limit=3" + (after is null ? "" : $"&after={after:D}"));
            seen.AddRange(page.Items.Select(row => row.Id));
            after = page.NextAfter;
        } while (after is not null);
        Assert.Equal(expected, seen);
        Assert.Equal(125, (await ListAsync("/cases?limit=100")).Items.Length + (await ListAsync($"/cases?limit=100&after={FixtureId(100):D}")).Items.Length);
        var first = await ListAsync("/cases?priority=High&limit=1");
        store.ChangePriority(FixtureId(6), CasePriority.Low);
        store.ChangePriority(FixtureId(4), CasePriority.High);
        using var restarted = Database.CreateFactory();
        using var client = restarted.CreateClient();
        var next = await ListAsync($"/cases?priority=High&limit=1&after={first.NextAfter:D}", client);
        Assert.Equal(FixtureId(4), Assert.Single(next.Items).Id);
        Assert.Equal(FixtureId(3), Assert.Single(first.Items).Id);
    }
}
