using EnterpriseCaseIntelligence.Api.Cases;
using EnterpriseCaseIntelligence.Domain.Cases;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class CaseEfAdoptionTests
{
    [Fact]
    public void FreshStoreHasScaffoldedMigrationStrictTablesAndNoPersistentMigrationLock()
    {
        using var fixture = new TemporaryCaseDatabase();
        using var context = new CaseDbContextFactory().CreateDbContext([]);
        Assert.False(context.Database.HasPendingModelChanges());
        var migration = Assert.Single(context.Database.GetMigrations());
        Assert.Equal(migration, fixture.Scalar("SELECT MigrationId FROM __EFMigrationsHistory"));
        Assert.Equal("10.0.13", fixture.Scalar("SELECT ProductVersion FROM __EFMigrationsHistory"));
        Assert.Equal(3L, fixture.Scalar("SELECT count(*) FROM pragma_table_list WHERE name IN ('cases','case_status_history','case_creation_keys') AND strict=1"));
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM sqlite_schema WHERE name='__EFMigrationsLock'"));
        Assert.Equal(4, fixture.Database.Inspect());
        fixture.Database.ValidateStartup();
        var error = Assert.Throws<SqliteException>(() => fixture.Execute("INSERT INTO cases VALUES ($id,$title,'Synthetic','New')",
            ("$id", Guid.NewGuid().ToString("D")), ("$title", new byte[] { 1, 2, 3 })));
        Assert.Equal(19, error.SqliteErrorCode);
    }

    [Theory]
    [InlineData(0, "UTF-8")]
    [InlineData(7, "UTF-8")]
    [InlineData(205, "UTF-8")]
    [InlineData(0, "UTF-16")]
    [InlineData(7, "UTF-16")]
    [InlineData(205, "UTF-16")]
    public void VersionThreeAdoptionPreservesExactCasesHistoryAndEveryRetainedKey(int count, string encoding)
    {
        using var fixture = new TemporaryCaseDatabase(initialize: false);
        var (cases, bindings) = VersionThreeCaseStore.Initialize(fixture, count, encoding);
        var history = VersionTwoCaseStore.HistoryFields(fixture);
        var originalSchemas = Schemas(fixture);
        Assert.Equal(3, fixture.Database.Inspect());
        Assert.Throws<InvalidDataException>(() => fixture.Database.ValidateStartup());
        var database = new CaseDatabase(fixture.Path, new ForbiddenClock());
        database.Upgrade();
        Assert.Equal(4, database.Inspect());
        database.ValidateStartup();
        AssertHistory(history, VersionTwoCaseStore.HistoryFields(fixture));
        foreach (var (name, schema) in originalSchemas) Assert.Equal(schema, Schemas(fixture)[name]);
        Assert.Equal(count, fixture.Count());
        Assert.Equal((long)bindings.Count, fixture.Scalar("SELECT count(*) FROM case_creation_keys"));
        var store = new SqliteCaseStore(database, new ForbiddenClock());
        foreach (var record in cases) Assert.Equal(record, store.Find(record.Id));
        foreach (var (key, id) in bindings)
        {
            var original = Assert.Single(cases, row => row.Id == id);
            var replay = store.Create(original.Title, original.Description, key);
            Assert.False(replay.IsKeyReused);
            Assert.Equal(original with { Status = CaseStatus.New }, replay.Case);
            Assert.True(store.Create("Different", "Different", key).IsKeyReused);
            Assert.Equal(original, store.Find(id));
        }
        AssertHistory(history, VersionTwoCaseStore.HistoryFields(fixture));
        var current = new SqliteCaseStore(database);
        Assert.NotNull(current.Create("After adoption", "Synthetic", Guid.NewGuid()).Case);
        Assert.NotNull(current.Create("Independent", "Synthetic"));
        Assert.Throws<InvalidDataException>(() => database.Upgrade());
        Assert.Equal(4, database.Inspect());
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM sqlite_schema WHERE name='__EFMigrationsLock'"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task FailedAdoptionCommitRollsBackEfHistoryMetadataAndRetainsSource(int version)
    {
        using var fixture = new TemporaryCaseDatabase(initialize: false);
        if (version == 1) LegacyCaseStore.Initialize(fixture, 7);
        else if (version == 2) VersionTwoCaseStore.Initialize(fixture, 7);
        else VersionThreeCaseStore.Initialize(fixture);
        var schemas = Schemas(fixture);
        var history = version >= 2 ? VersionTwoCaseStore.HistoryFields(fixture) : [];
        using (var blocker = fixture.Database.Open())
        using (var read = blocker.BeginTransaction(deferred: true))
        {
            using var command = blocker.CreateCommand();
            command.Transaction = read;
            command.CommandText = "SELECT count(*) FROM cases";
            Assert.Equal(7L, command.ExecuteScalar());
            var error = await Assert.ThrowsAsync<SqliteException>(() => Task.Run(() => fixture.Database.Upgrade()));
            Assert.Equal(5, error.SqliteErrorCode);
            read.Rollback();
        }
        Assert.Equal(version, fixture.Database.Inspect());
        Assert.Equal(schemas, Schemas(fixture));
        if (version >= 2) AssertHistory(history, VersionTwoCaseStore.HistoryFields(fixture));
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM sqlite_schema WHERE name IN ('__EFMigrationsHistory','__EFMigrationsLock')"));
        fixture.Database.Upgrade();
        Assert.Equal(4, fixture.Database.Inspect());
    }

    [Theory]
    [InlineData("DELETE FROM __EFMigrationsHistory")]
    [InlineData("UPDATE __EFMigrationsHistory SET MigrationId='SYNTHETIC_SENSITIVE_MARKER'")]
    [InlineData("UPDATE __EFMigrationsHistory SET ProductVersion='invalid'")]
    [InlineData("UPDATE __EFMigrationsHistory SET ProductVersion='10.0.12'")]
    [InlineData("UPDATE __EFMigrationsHistory SET ProductVersion='11.0.0'")]
    [InlineData("UPDATE __EFMigrationsHistory SET ProductVersion='10.0.13-preview'")]
    [InlineData("INSERT INTO __EFMigrationsHistory VALUES ('unexpected','10.0.13')")]
    [InlineData("CREATE TABLE __EFMigrationsLock (Id INTEGER PRIMARY KEY,Timestamp TEXT)")]
    public async Task InvalidOrUnexpectedEfMetadataFailsWithoutRepairOrLeakingDiagnostics(string corruption)
    {
        using var fixture = new TemporaryCaseDatabase();
        fixture.Execute(corruption);
        var before = File.ReadAllBytes(fixture.Path);
        foreach (var mode in new[] { "inspect-store", "upgrade-store", "hosting" })
        {
            var args = mode == "hosting" ? new[] { "--CaseStorage:DatabasePath", fixture.Path }
                : new[] { mode, "--CaseStorage:DatabasePath", fixture.Path };
            var result = await ApiProcess.RunAsync(args);
            Assert.Equal(1, result.ExitCode);
            Assert.DoesNotContain("Now listening", result.Output);
            Assert.DoesNotContain(fixture.Path, result.Output);
            Assert.DoesNotContain("SYNTHETIC_SENSITIVE_MARKER", result.Output);
            Assert.DoesNotContain("Exception", result.Output);
        }
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
    }

    [Theory]
    [InlineData("DELETE FROM case_status_history")]
    [InlineData("UPDATE case_status_history SET kind='Baseline' WHERE kind='Created'")]
    [InlineData("UPDATE cases SET title=' corrupt '")]
    [InlineData("CREATE TABLE __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY,ProductVersion TEXT)")]
    public void InvalidVersionThreeIsNotAssignedAnEfBaseline(string corruption)
    {
        using var fixture = new TemporaryCaseDatabase(initialize: false);
        VersionThreeCaseStore.Initialize(fixture);
        fixture.Execute(corruption);
        var before = File.ReadAllBytes(fixture.Path);
        Assert.Throws<InvalidDataException>(() => fixture.Database.Upgrade());
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        Assert.Equal(3L, fixture.Scalar("PRAGMA user_version"));
    }

    [Fact]
    public async Task CommandsAdoptVersionThreeWithoutHostingAndThenRefuseRepeatedUpgrade()
    {
        using var fixture = new TemporaryCaseDatabase(initialize: false);
        VersionThreeCaseStore.Initialize(fixture);
        var inspected = await ApiProcess.RunAsync("inspect-store", "--CaseStorage:DatabasePath", fixture.Path);
        Assert.Equal(0, inspected.ExitCode);
        Assert.Contains("Case store version: 3.", inspected.Output);
        var refused = await ApiProcess.RunAsync("--CaseStorage:DatabasePath", fixture.Path);
        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("upgrade-store", refused.Output);
        var upgraded = await ApiProcess.RunAsync("upgrade-store", "--CaseStorage:DatabasePath", fixture.Path);
        Assert.Equal(0, upgraded.ExitCode);
        Assert.DoesNotContain("Now listening", upgraded.Output);
        Assert.Equal(4, fixture.Database.Inspect());
        var repeated = await ApiProcess.RunAsync("upgrade-store", "--CaseStorage:DatabasePath", fixture.Path);
        Assert.Equal(1, repeated.ExitCode);
    }

    private static Dictionary<string, string> Schemas(TemporaryCaseDatabase fixture)
    {
        using var connection = fixture.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name,sql FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*' ORDER BY name";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string>();
        while (reader.Read()) result.Add(reader.GetString(0), reader.GetString(1));
        return result;
    }

    private static void AssertHistory(string[][] expected, string[][] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++) Assert.Equal(expected[index], actual[index]);
    }

    private sealed class ForbiddenClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("Adoption/replay must not read the clock");
    }
}
