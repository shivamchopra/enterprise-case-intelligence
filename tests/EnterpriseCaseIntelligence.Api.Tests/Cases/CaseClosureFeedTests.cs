using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EnterpriseCaseIntelligence.Api.Cases;
using EnterpriseCaseIntelligence.Domain.Cases;
using Microsoft.Data.Sqlite;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class CaseClosureFeedTests : CaseApiTest
{
    private const string Feed = "/integrations/case-closures";

    [Fact]
    public async Task PagesByCommitPositionAcrossCaseIdAndClockChangesAndRestart()
    {
        var empty = await PageAsync(Client);
        var stream = empty.GetProperty("streamId").GetGuid().ToString("D");
        Assert.Equal("0", empty.GetProperty("nextAfter").GetString());
        Assert.Equal(empty.GetRawText(), (await PageAsync(Client, $"?streamId={stream}&after=0")).GetRawText());

        var clock = new MutableClock();
        var store = new SqliteCaseStore(Database.Database, clock);
        var cases = Enumerable.Range(0, 3).Select(index => store.Create($"Fictional case {index}", ValidDescription))
            .OrderBy(record => record.Id.ToString("D"), StringComparer.Ordinal).ToArray();
        foreach (var record in cases) store.ChangeStatus(record.Id, CaseStatus.InReview);

        store.ChangeStatus(cases[1].Id, CaseStatus.Closed);
        store.ChangeStatus(cases[2].Id, CaseStatus.Closed);
        var first = await PageAsync(Client, "?limit=1");
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        AssertNotice(Assert.Single(first.GetProperty("items").EnumerateArray()), "1", cases[1].Id, store.History(cases[1].Id)![2]);

        clock.Now = clock.Now.AddDays(-1);
        store.ChangeStatus(cases[0].Id, CaseStatus.Closed);
        Factory.Dispose();
        using var reopened = Database.CreateFactory();
        using var reader = reopened.CreateClient();
        var later = await PageAsync(reader, $"?streamId={stream}&after=1&limit=2");
        Assert.Equal(stream, later.GetProperty("streamId").GetString());
        Assert.Equal("3", later.GetProperty("nextAfter").GetString());
        Assert.False(later.GetProperty("hasMore").GetBoolean());
        var notices = later.GetProperty("items");
        AssertNotice(notices[0], "2", cases[2].Id, store.History(cases[2].Id)![2]);
        AssertNotice(notices[1], "3", cases[0].Id, store.History(cases[0].Id)![2]);
        var end = await PageAsync(reader, $"?streamId={stream}&after=3");
        Assert.Empty(end.GetProperty("items").EnumerateArray());
        Assert.Equal("3", end.GetProperty("nextAfter").GetString());
        Assert.False(end.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task DefaultAndMaximumPageSizesUseOneLookaheadAndRetainExactLargePosition()
    {
        var store = new SqliteCaseStore(Database.Database);
        for (var index = 0; index < 101; index++)
        {
            var record = store.Create($"Fictional case {index}", ValidDescription);
            store.ChangeStatus(record.Id, CaseStatus.InReview);
            store.ChangeStatus(record.Id, CaseStatus.Closed);
        }

        var standard = await PageAsync(Client);
        Assert.Equal(20, standard.GetProperty("items").GetArrayLength());
        Assert.Equal("20", standard.GetProperty("nextAfter").GetString());
        Assert.True(standard.GetProperty("hasMore").GetBoolean());

        var maximum = await PageAsync(Client, "?limit=100");
        Assert.Equal(100, maximum.GetProperty("items").GetArrayLength());
        Assert.Equal("100", maximum.GetProperty("nextAfter").GetString());
        Assert.True(maximum.GetProperty("hasMore").GetBoolean());
        Database.Execute("UPDATE case_closure_notices SET position=9223372036854775807 WHERE position=101");
        var stream = maximum.GetProperty("streamId").GetString();
        var last = await PageAsync(Client, $"?streamId={stream}&after=100");
        Assert.Equal("9223372036854775807", Assert.Single(last.GetProperty("items").EnumerateArray()).GetProperty("position").GetString());
        Assert.Equal("9223372036854775807", last.GetProperty("nextAfter").GetString());
        var end = await PageAsync(Client, $"?streamId={stream}&after=9223372036854775807");
        Assert.Empty(end.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task OnlyActualClosureAppendsEvenWithConcurrentRequestsAndCreationReplay()
    {
        var store = new SqliteCaseStore(Database.Database);
        var key = Guid.NewGuid();
        var record = store.Create(ValidTitle, ValidDescription, key).Case!;
        using var rejected = await Client.PutAsJsonAsync(PathFor(record.Id) + "/status", new { status = "Closed" });
        Assert.False(rejected.IsSuccessStatusCode);
        store.ChangePriority(record.Id, CasePriority.High);
        await SetStatusAsync(record.Id, "InReview");
        Assert.Empty((await PageAsync(Client)).GetProperty("items").EnumerateArray());
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => SetStatusAsync(record.Id, "Closed")));
        Assert.All(results, result => Assert.Equal("Closed", result.Status));
        Assert.Equal(record, store.Create(record.Title, record.Description, key).Case);
        AssertNotice(Assert.Single((await PageAsync(Client)).GetProperty("items").EnumerateArray()), "1", record.Id, StoredHistory(record.Id)[2]);
        Assert.Equal(3, StoredHistory(record.Id).Length);
    }

    [Theory]
    [InlineData("?limit=0", "limit")]
    [InlineData("?limit=101", "limit")]
    [InlineData("?limit=01", "limit")]
    [InlineData("?limit=1&limit=2", "limit")]
    [InlineData("?Limit=1", "query")]
    [InlineData("?after=0", "query")]
    [InlineData("?streamId=4b526b84-b472-4991-8575-123db4385758", "query")]
    [InlineData("?streamId=4B526B84-B472-4991-8575-123DB4385758&after=0", "streamId")]
    [InlineData("?streamId=00000000-0000-0000-0000-000000000000&after=0", "streamId")]
    [InlineData("?streamId=4b526b84-b472-4991-8575-123db4385758&after=0&after=1", "after")]
    [InlineData("?streamId=4b526b84-b472-4991-8575-123db4385758&after=00", "after")]
    [InlineData("?streamId=4b526b84-b472-4991-8575-123db4385758&after=-1", "after")]
    [InlineData("?streamId=4b526b84-b472-4991-8575-123db4385758&after=%2B1", "after")]
    [InlineData("?streamId=4b526b84-b472-4991-8575-123db4385758&after=9223372036854775808", "after")]
    public async Task InvalidQueryFailsBeforeOpeningStore(string query, string field)
    {
        File.Delete(Database.Path);
        using var response = await Client.GetAsync(Feed + query);
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed", field);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.False(File.Exists(Database.Path));
    }

    [Fact]
    public async Task SwitchedStreamAndUnknownCheckpointHaveDistinctSafeFailures()
    {
        using var other = new TemporaryCaseDatabase();
        var stream = (await PageAsync(Client)).GetProperty("streamId").GetString();
        var otherStream = (string)other.Scalar("SELECT stream_id FROM case_closure_stream")!;
        using var changed = await Client.GetAsync($"{Feed}?streamId={otherStream}&after=1");
        await AssertProblemAsync(changed, HttpStatusCode.Conflict, "handoff_source_changed");
        using var unknown = await Client.GetAsync($"{Feed}?streamId={stream}&after=1");
        await AssertProblemAsync(unknown, HttpStatusCode.Conflict, "handoff_checkpoint_unknown");
        Assert.True(changed.Headers.CacheControl?.NoStore);
        Assert.True(unknown.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("UPDATE case_closure_notices SET history_sequence=1 WHERE position=2")]
    [InlineData("UPDATE case_closure_notices SET history_sequence=9 WHERE position=2")]
    [InlineData("UPDATE case_status_history SET recorded_at_utc='SYNTHETIC_SENSITIVE_MARKER' WHERE sequence=3 AND case_id=(SELECT case_id FROM case_closure_notices WHERE position=2)")]
    public async Task InvalidRetainedNoticeIncludingLookaheadAndCheckpointFailsSafely(string corruption)
    {
        for (var index = 0; index < 2; index++)
        {
            var record = await CreateAsync();
            await SetStatusAsync(record.Id, "InReview");
            await SetStatusAsync(record.Id, "Closed");
        }
        var stream = (await PageAsync(Client)).GetProperty("streamId").GetString();
        Database.Execute("PRAGMA foreign_keys=OFF; " + corruption);
        using var lookahead = await Client.GetAsync(Feed + "?limit=1");
        await AssertProblemAsync(lookahead, HttpStatusCode.InternalServerError, "internal_error");
        Assert.True(lookahead.Headers.CacheControl?.NoStore);
        using var checkpoint = await Client.GetAsync($"{Feed}?streamId={stream}&after=2");
        await AssertProblemAsync(checkpoint, HttpStatusCode.InternalServerError, "internal_error");
        Assert.Throws<InvalidDataException>(() => Database.Database.Inspect());
    }

    [Theory]
    [InlineData("BEFORE UPDATE ON cases", "RAISE(ABORT, 'SYNTHETIC_SENSITIVE_MARKER')")]
    [InlineData("BEFORE INSERT ON case_status_history", "RAISE(ABORT, 'SYNTHETIC_SENSITIVE_MARKER')")]
    [InlineData("BEFORE INSERT ON case_closure_notices", "RAISE(ABORT, 'SYNTHETIC_SENSITIVE_MARKER')")]
    [InlineData("BEFORE INSERT ON case_closure_notices", "RAISE(IGNORE)")]
    public async Task FailedWriteRollsBackWholeClosureAndCanBeRetried(string boundary, string failure)
    {
        var record = await CreateAsync();
        await SetStatusAsync(record.Id, "InReview");
        var history = StoredHistory(record.Id);
        Database.Execute($"CREATE TRIGGER fail_close {boundary} BEGIN SELECT {failure}; END");
        using var failed = await Client.PutAsJsonAsync(PathFor(record.Id) + "/status", new { status = "Closed" });
        await AssertProblemAsync(failed, HttpStatusCode.InternalServerError, "internal_error");
        Database.Execute("DROP TRIGGER fail_close");
        Assert.Equal("InReview", (await ReadAsync(record.Id)).Status);
        Assert.Equal(history, StoredHistory(record.Id));
        Assert.Empty((await PageAsync(Client)).GetProperty("items").EnumerateArray());
        await SetStatusAsync(record.Id, "Closed");
        Assert.Single((await PageAsync(Client)).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task FailedCommitLeavesPriorStatusHistoryAndFeed()
    {
        var record = await CreateAsync();
        await SetStatusAsync(record.Id, "InReview");
        using (var blocker = Database.Database.Open())
        using (var read = blocker.BeginTransaction(deferred: true))
        {
            using var command = blocker.CreateCommand();
            command.Transaction = read;
            command.CommandText = "SELECT count(*) FROM cases";
            Assert.Equal(1L, command.ExecuteScalar());
            var store = new SqliteCaseStore(Database.Database);
            var error = await Assert.ThrowsAsync<SqliteException>(() => Task.Run(() => store.ChangeStatus(record.Id, CaseStatus.Closed)));
            Assert.Equal(5, error.SqliteErrorCode);
            read.Rollback();
        }
        Assert.Equal("InReview", (await ReadAsync(record.Id)).Status);
        Assert.Equal(2, StoredHistory(record.Id).Length);
        Assert.Empty((await PageAsync(Client)).GetProperty("items").EnumerateArray());
        await SetStatusAsync(record.Id, "Closed");
        Assert.Single((await PageAsync(Client)).GetProperty("items").EnumerateArray());
    }

    private static async Task<JsonElement> PageAsync(HttpClient client, string query = "")
    {
        using var response = await client.GetAsync(Feed + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var page = body.RootElement;
        Assert.Equal(new[] { "hasMore", "items", "nextAfter", "streamId" }, page.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        return page.Clone();
    }

    private static void AssertNotice(JsonElement notice, string position, Guid caseId, CaseHistoryEntry history)
    {
        Assert.Equal(new[] { "caseId", "closedAtUtc", "historySequence", "position", "type" },
            notice.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(position, notice.GetProperty("position").GetString());
        Assert.Equal("case.closed.v1", notice.GetProperty("type").GetString());
        Assert.Equal(caseId.ToString("D"), notice.GetProperty("caseId").GetString());
        Assert.Equal(history.Sequence, notice.GetProperty("historySequence").GetInt32());
        Assert.Equal(history.RecordedAtUtc, notice.GetProperty("closedAtUtc").GetString());
    }

    private sealed class MutableClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-05T18:00:00Z", CultureInfo.InvariantCulture);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
