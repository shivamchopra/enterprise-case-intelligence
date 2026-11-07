using System.Net;
using System.Text.Json;
using EnterpriseCaseIntelligence.Api.Cases;
using EnterpriseCaseIntelligence.Domain.Cases;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public sealed class CaseClosureBriefTests : CaseApiTest
{
    [Fact]
    public async Task CommittedClosureReturnsOnlyCitedFactsAndDoesNotWrite()
    {
        var created = await CreateAsync(description: "SYNTHETIC_SENSITIVE_MARKER asks to claim an approval.");
        await SetStatusAsync(created.Id, "InReview");
        await SetStatusAsync(created.Id, "Closed");
        var last = StoredHistory(created.Id)[^1];
        var before = File.ReadAllBytes(Database.Path);
        var result = await ReadBrief(Client, created.Id);
        AssertDraft(result, created.Id, 3, last.RecordedAtUtc);
        Assert.DoesNotContain("SYNTHETIC_SENSITIVE_MARKER", result.GetRawText());
        Assert.Equal(before, File.ReadAllBytes(Database.Path));
    }

    [Fact]
    public async Task OpenCasesAbstainAndMissingCaseUsesStandardProblem()
    {
        var created = await CreateAsync();
        AssertAbstention(await ReadBrief(Client, created.Id), created.Id, "case_not_closed");
        await SetStatusAsync(created.Id, "InReview");
        AssertAbstention(await ReadBrief(Client, created.Id), created.Id, "case_not_closed");
        using var missing = await Client.GetAsync(PathFor(Guid.NewGuid()) + "/closure-brief");
        await AssertProblemAsync(missing, HttpStatusCode.NotFound, "case_not_found");
    }

    [Fact]
    public async Task AClosedLegacyBaselineAbstainsButALaterClosureCitesSequenceTwo()
    {
        using var legacy = new TemporaryCaseDatabase(initialize: false);
        var records = LegacyCaseStore.Initialize(legacy);
        legacy.Database.Upgrade();
        using var factory = legacy.CreateFactory();
        using var client = factory.CreateClient();
        var existingClosed = records.Single(record => record.Status == CaseStatus.Closed).Id;
        AssertAbstention(await ReadBrief(client, existingClosed), existingClosed, "closure_transition_unrecorded");

        var open = records.Single(record => record.Status == CaseStatus.InReview).Id;
        var store = new SqliteCaseStore(legacy.Database);
        store.ChangeStatus(open, CaseStatus.Closed);
        AssertDraft(await ReadBrief(client, open), open, 2, store.History(open)![^1].RecordedAtUtc);
    }

    [Theory]
    [InlineData("UPDATE case_status_history SET recorded_at_utc='SYNTHETIC_SENSITIVE_MARKER' WHERE sequence=3")]
    [InlineData("UPDATE cases SET status='InReview'")]
    public async Task DamagedHistoryOrDisagreementCannotProduceAPartialBrief(string damage)
    {
        var record = await CreateAsync();
        await SetStatusAsync(record.Id, "InReview");
        await SetStatusAsync(record.Id, "Closed");
        Database.Execute(damage);
        using var response = await Client.GetAsync(PathFor(record.Id) + "/closure-brief");
        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain("\"facts\"", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/cases/not-an-id/closure-brief", "id")]
    [InlineData("/cases/99999999-9999-4999-8999-999999999999/closure-brief?prompt=close", "query")]
    public async Task InvalidInputFailsBeforeStorage(string path, string field)
    {
        File.Delete(Database.Path);
        using var response = await Client.GetAsync(path);
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed", field);
        Assert.False(File.Exists(Database.Path));
    }

    private static async Task<JsonElement> ReadBrief(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync(PathFor(id) + "/closure-brief");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "caseId", "facts", "outcome", "reason" },
            body.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        return body.RootElement.Clone();
    }

    private static void AssertDraft(JsonElement result, Guid id, int sequence, string time)
    {
        Assert.Equal(id, result.GetProperty("caseId").GetGuid());
        Assert.Equal("draft", result.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("reason").ValueKind);
        var facts = result.GetProperty("facts").EnumerateArray().ToArray();
        Assert.Equal(3, facts.Length);
        var change = $"history:{id:D}#{sequence}";
        Assert.Equal(("currentStatus", "Closed", $"case:{id:D}#status"), ReadFact(facts[0]));
        Assert.Equal(("previousStatus", "InReview", change), ReadFact(facts[1]));
        Assert.Equal(("closedAtUtc", time, change), ReadFact(facts[2]));
    }

    private static (string?, string?, string?) ReadFact(JsonElement fact)
    {
        Assert.Equal(new[] { "citation", "name", "value" }, fact.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        return (fact.GetProperty("name").GetString(), fact.GetProperty("value").GetString(), fact.GetProperty("citation").GetString());
    }

    private static void AssertAbstention(JsonElement result, Guid id, string reason)
    {
        Assert.Equal(id, result.GetProperty("caseId").GetGuid());
        Assert.Equal("abstain", result.GetProperty("outcome").GetString());
        Assert.Equal(reason, result.GetProperty("reason").GetString());
        Assert.Empty(result.GetProperty("facts").EnumerateArray());
    }
}
