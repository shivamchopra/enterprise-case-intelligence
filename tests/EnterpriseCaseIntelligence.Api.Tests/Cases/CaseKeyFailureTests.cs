using System.Net;
using System.Text.Json;
using EnterpriseCaseIntelligence.Api.Cases;
using Microsoft.Data.Sqlite;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class CaseKeyFailureTests : CaseApiTest
{
    private readonly Guid _key = Guid.NewGuid();
    private static readonly string Body = JsonSerializer.Serialize(new { title = ValidTitle, description = ValidDescription });
    private Task<HttpResponseMessage> Send(string? body = null) => CaseKeyApiTests.SendAsync(Client, body ?? Body, [$"\"{_key:D}\""]);

    [Theory]
    [InlineData("cases", "BEFORE", "IGNORE")]
    [InlineData("cases", "AFTER", "ABORT")]
    [InlineData("case_status_history", "BEFORE", "IGNORE")]
    [InlineData("case_status_history", "AFTER", "ABORT")]
    [InlineData("case_creation_keys", "BEFORE", "IGNORE")]
    [InlineData("case_creation_keys", "AFTER", "ABORT")]
    public async Task EveryWriteBoundaryRollsBackAllThreeRecordsAndRetryCanBind(string table, string timing, string failure)
    {
        var raise = failure == "IGNORE" ? "RAISE(IGNORE)" : "RAISE(ABORT, 'SYNTHETIC_SENSITIVE_MARKER')";
        Database.Execute($"CREATE TRIGGER fail_key {timing} INSERT ON {table} BEGIN SELECT {raise}; END");
        using var failed = await Send();
        await AssertProblemAsync(failed, HttpStatusCode.InternalServerError, "internal_error");
        Database.Execute("DROP TRIGGER fail_key");
        Database.Database.ValidateStartup();
        // Independent reopened connections, rather than an in-memory success/failure result.
        foreach (var name in new[] { "cases", "case_status_history", "case_creation_keys" })
            Assert.Equal(0L, Database.Scalar($"SELECT count(*) FROM {name}"));
        using var retry = await Send();
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        var created = await ReadCaseAsync(retry);
        Assert.Single(new SqliteCaseStore(new CaseDatabase(Database.Path)).History(created.Id)!);
        Assert.Equal(4, new CaseDatabase(Database.Path).Inspect());
        foreach (var name in new[] { "cases", "case_status_history", "case_creation_keys" })
            Assert.Equal(1L, Database.Scalar($"SELECT count(*) FROM {name}"));
    }

    [Fact]
    public async Task CommitLockFailureRollsBackAllWritesAndDoesNotCacheError()
    {
        using (var blocker = Database.Database.Open())
        using (var read = blocker.BeginTransaction(deferred: true))
        {
            using var command = blocker.CreateCommand();
            command.Transaction = read;
            command.CommandText = "SELECT count(*) FROM cases";
            Assert.Equal(0L, command.ExecuteScalar());
            using var response = await Send();
            await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
            read.Rollback();
        }
        Assert.Equal(0, Database.Count());
        Assert.Equal(0L, Database.Scalar("SELECT count(*) FROM case_creation_keys"));
        using var retry = await Send();
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(1, Database.Count());
    }

    [Fact]
    public async Task ProviderUniqueConstraintIs500RatherThanFalsePayloadMismatch()
    {
        using var first = await Send();
        var original = await ReadCaseAsync(first);
        Database.Execute($"""
            CREATE TRIGGER duplicate_target BEFORE INSERT ON case_creation_keys BEGIN
                INSERT INTO case_creation_keys VALUES (NEW.request_key,'{original.Id:D}');
            END
            """);
        using var failed = await CaseKeyApiTests.SendAsync(Client, Body, [$"\"{Guid.NewGuid():D}\""]);
        await AssertProblemAsync(failed, HttpStatusCode.InternalServerError, "internal_error");
        Database.Execute("DROP TRIGGER duplicate_target");
        Assert.Equal(1, Database.Count());
        Assert.Equal(1L, Database.Scalar("SELECT count(*) FROM case_creation_keys"));
    }

    [Theory]
    [InlineData("UPDATE case_creation_keys SET case_id='SYNTHETIC_SENSITIVE_MARKER'")]
    [InlineData("UPDATE case_creation_keys SET case_id='00000000-0000-0000-0000-000000000000'")]
    [InlineData("UPDATE case_creation_keys SET case_id='11111111-1111-4111-8111-111111111111'")]
    [InlineData("DELETE FROM cases")]
    [InlineData("UPDATE cases SET title=' corrupt '")]
    [InlineData("DELETE FROM case_status_history")]
    [InlineData("UPDATE case_status_history SET kind='Baseline'")]
    [InlineData("UPDATE case_status_history SET recorded_at_utc='SYNTHETIC_SENSITIVE_MARKER'")]
    [InlineData("UPDATE cases SET status='Closed'")]
    public async Task SelectedCorruptionFailsBeforeReplayOrMismatchWithoutReplacement(string sql)
    {
        using var first = await Send();
        // The test alone bypasses enforcement after hosting; production connections keep it enabled.
        Database.Execute("PRAGMA foreign_keys=OFF; PRAGMA ignore_check_constraints=ON; " + sql);
        var before = File.ReadAllBytes(Database.Path);
        foreach (var body in new[] { Body, JsonSerializer.Serialize(new { title = "Different", description = ValidDescription }) })
        {
            using var response = await Send(body);
            await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
        }
        using var invalid = await Send("{}");
        await AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(before, File.ReadAllBytes(Database.Path));
        Assert.Throws<InvalidDataException>(() => new CaseDatabase(Database.Path).Inspect());
        Assert.Equal(1L, Database.Scalar("SELECT count(*) FROM case_creation_keys"));
    }

    [Fact]
    public async Task LookupIsBoundedWhileInspectionRejectsAnUnrelatedDamagedBinding()
    {
        using var first = await Send();
        var original = await ReadCaseAsync(first);
        var store = new SqliteCaseStore(Database.Database);
        var unrelatedKey = Guid.NewGuid();
        var unrelated = store.Create("Other", "Other", unrelatedKey).Case!;
        Database.Execute("UPDATE case_status_history SET kind='Baseline' WHERE case_id=$id", ("$id", unrelated.Id.ToString("D")));
        using var replay = await Send();
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(original, await ReadCaseAsync(replay));
        Assert.Throws<InvalidDataException>(() => Database.Database.Inspect());
        Database.Execute("PRAGMA ignore_check_constraints=ON; UPDATE case_creation_keys SET request_key='bad' WHERE case_id=$id",
            ("$id", unrelated.Id.ToString("D")));
        Assert.Throws<InvalidDataException>(() => Database.Database.Inspect());
    }

    [Fact]
    public async Task DuplicateSelectedRowsAreAStorageFailureEvenWithMatchingText()
    {
        using var first = await Send();
        Database.Execute("""
            ALTER TABLE case_creation_keys RENAME TO old_keys;
            CREATE TABLE case_creation_keys (request_key TEXT, case_id TEXT);
            INSERT INTO case_creation_keys SELECT * FROM old_keys;
            INSERT INTO case_creation_keys SELECT * FROM old_keys;
            """);
        using var replay = await Send();
        await AssertProblemAsync(replay, HttpStatusCode.InternalServerError, "internal_error");
        Assert.Equal(1, Database.Count());
    }

    [Fact]
    public void OfflineInspectionValidatesBindingsBeyondTwoBatches()
    {
        var store = new SqliteCaseStore(Database.Database);
        CaseSnapshot? last = null;
        for (var index = 0; index < 205; index++)
        {
            var key = Guid.Parse($"{index + 1:x8}-aaaa-4bbb-8ccc-dddddddddddd");
            last = store.Create("Synthetic batch", "Synthetic inspection", key).Case;
        }
        Assert.Equal(4, Database.Database.Inspect());
        // This remains a valid case/history chain; only its binding provenance is invalid.
        Database.Execute("UPDATE case_status_history SET kind='Baseline' WHERE case_id=$id", ("$id", last!.Id.ToString("D")));
        Assert.Throws<InvalidDataException>(() => Database.Database.Inspect());
        Assert.Equal(205L, Database.Scalar("SELECT count(*) FROM case_creation_keys"));
    }

    [Fact]
    public void SchemaEnforcesUniqueKeyUniqueTargetForeignKeyAndCanonicalKeys()
    {
        var store = new SqliteCaseStore(Database.Database);
        var first = store.Create("One", "One", _key).Case!;
        var second = store.Create("Two", "Two");
        foreach (var key in new[] { "", Guid.Empty.ToString("D"), "B6AB3CD4-87CF-4BDD-AE55-103EA9978018",
                     _key.ToString("D") + "\0tail", _key.ToString("N"), "\"" + _key.ToString("D") + "\"", "é" + _key.ToString("D")[1..] })
        {
            var error = Assert.Throws<SqliteException>(() => Database.Execute("INSERT INTO case_creation_keys VALUES ($key,$id)",
                ("$key", key), ("$id", second.Id.ToString("D"))));
            Assert.Equal(19, error.SqliteErrorCode);
        }
        foreach (var pair in new[] { (_key, second.Id), (Guid.NewGuid(), first.Id), (Guid.NewGuid(), Guid.NewGuid()) })
        {
            var error = Assert.Throws<SqliteException>(() => Database.Execute("INSERT INTO case_creation_keys VALUES ($key,$id)",
                ("$key", pair.Item1.ToString("D")), ("$id", pair.Item2.ToString("D"))));
            Assert.Equal(19, error.SqliteErrorCode);
        }
    }
}
