using System.Net;
using System.Net.Http.Json;
using EnterpriseCaseIntelligence.Api.Cases;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class CaseHistoryFailureTests : CaseApiTest
{
    [Theory]
    [InlineData("DELETE FROM case_status_history")]
    [InlineData("DELETE FROM case_status_history WHERE sequence=2")]
    [InlineData("UPDATE case_status_history SET sequence=0 WHERE sequence=1")]
    [InlineData("UPDATE case_status_history SET kind='Created' WHERE sequence=2")]
    [InlineData("UPDATE case_status_history SET kind='SYNTHETIC_SENSITIVE_MARKER' WHERE sequence=1")]
    [InlineData("UPDATE case_status_history SET from_status='New' WHERE sequence=1")]
    [InlineData("UPDATE case_status_history SET to_status='InReview' WHERE sequence=1")]
    [InlineData("UPDATE case_status_history SET from_status='New' WHERE sequence=3")]
    [InlineData("UPDATE case_status_history SET to_status='New' WHERE sequence=2")]
    [InlineData("UPDATE case_status_history SET to_status='closed' WHERE sequence=3")]
    [InlineData("UPDATE case_status_history SET recorded_at_utc='2026-02-30T12:00:00.0000000Z' WHERE sequence=2")]
    [InlineData("UPDATE case_status_history SET recorded_at_utc='2026-10-20T12:00:00Z' WHERE sequence=2")]
    [InlineData("UPDATE case_status_history SET recorded_at_utc='2026-10-20T12:00:00.0000000+00:00' WHERE sequence=2")]
    [InlineData("UPDATE case_status_history SET recorded_at_utc='SYNTHETIC_SENSITIVE_MARKER' WHERE sequence=2")]
    [InlineData("UPDATE cases SET status='New'")]
    [InlineData("INSERT INTO case_status_history SELECT case_id,4,'StatusChanged','InReview','Closed',recorded_at_utc FROM case_status_history WHERE sequence=3")]
    public async Task InvalidChainsFailReadsAndWritesWithoutRepairOrPartialSuccess(string corruption)
    {
        var created = await CreateAsync();
        await SetStatusAsync(created.Id, "InReview");
        await SetStatusAsync(created.Id, "Closed");
        Database.Execute("PRAGMA ignore_check_constraints=ON; " + corruption);
        var before = File.ReadAllBytes(Database.Path);
        using var read = await Client.GetAsync(PathFor(created.Id) + "/history");
        await AssertProblemAsync(read, HttpStatusCode.InternalServerError, "internal_error");
        Assert.DoesNotContain("\"items\"", await read.Content.ReadAsStringAsync());
        foreach (var requested in new[] { "New", "Closed" })
        {
            using var write = await Client.PutAsJsonAsync(PathFor(created.Id) + "/status", new { status = requested });
            await AssertProblemAsync(write, HttpStatusCode.InternalServerError, "internal_error");
        }

        using var invalid = await Client.PutAsJsonAsync(PathFor(created.Id) + "/status", new { status = "bad" });
        await AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "validation_failed", "status");
        Assert.Equal(before, File.ReadAllBytes(Database.Path));
    }

    [Theory]
    [InlineData(true, "case", "ABORT")]
    [InlineData(false, "case", "ABORT")]
    [InlineData(true, "history", "ABORT")]
    [InlineData(false, "history", "ABORT")]
    [InlineData(true, "history", "IGNORE")]
    [InlineData(false, "history", "IGNORE")]
    public async Task PairFailuresRollbackBothStateAndHistory(bool creating, string side, string failure)
    {
        var existing = await CreateAsync();
        var history = StoredHistory(existing.Id);
        var table = side == "case" ? "cases" : "case_status_history";
        var operation = side == "case" && !creating ? "UPDATE" : "INSERT";
        var timing = side == "case" ? "AFTER" : "BEFORE";
        var raise = failure == "IGNORE" ? "RAISE(IGNORE)" : "RAISE(ABORT, 'SYNTHETIC_SENSITIVE_MARKER')";
        Database.Execute($"CREATE TRIGGER fail_pair {timing} {operation} ON {table} BEGIN SELECT {raise}; END");
        using var response = creating
            ? await Client.PostAsJsonAsync("/cases", new { title = ValidTitle, description = ValidDescription })
            : await Client.PutAsJsonAsync(PathFor(existing.Id) + "/status", new { status = "InReview" });
        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
        Database.Execute("DROP TRIGGER fail_pair");
        Factory.Dispose();
        Database.Database.ValidateStartup();
        var reopened = new SqliteCaseStore(Database.Database);
        Assert.Equal(existing.Status, reopened.Find(existing.Id)!.Status.ToString());
        Assert.Equal(history, reopened.History(existing.Id));
        Assert.Equal(1L, Database.Count());
        Assert.Equal(1L, Database.Scalar("SELECT count(*) FROM case_status_history"));
    }
}
