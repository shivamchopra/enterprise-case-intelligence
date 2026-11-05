using EnterpriseCaseIntelligence.Api.Cases;
using EnterpriseCaseIntelligence.Domain.Cases;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class CasePriorityLifecycleTests
{
    public static IEnumerable<object[]> Sources =>
        from family in new[] { "fresh", "adopted-1", "adopted-2", "adopted-3" }
        from encoding in new[] { "UTF-8", "UTF-16" }
        from count in new[] { 0, 205 }
        from patch in new[] { "10.0.13", "10.0.14" }
        select new object[] { family, encoding, count, patch };

    [Theory, MemberData(nameof(Sources))]
    public void FrozenFormatFourUpgradesPreserveEveryRetainedValueAndAssignNormalOnce(string family, string encoding, int count, string patch)
    {
        using var fixture = new TemporaryCaseDatabase(false);
        var (cases, bindings) = VersionFourCaseStore.Initialize(fixture, family, count, encoding, patch);
        var oldSchemas = Schemas(fixture);
        var history = VersionTwoCaseStore.HistoryFields(fixture);
        var before = File.ReadAllBytes(fixture.Path);
        Assert.Equal(4, fixture.Database.Inspect());
        Assert.Throws<InvalidDataException>(() => fixture.Database.ValidateStartup());
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        var database = new CaseDatabase(fixture.Path, new ForbiddenClock());
        database.Upgrade();
        Assert.Equal(5, database.Inspect());
        database.ValidateStartup();
        foreach (var (name, schema) in oldSchemas) Assert.Equal(schema, Schemas(fixture)[name]);
        Assert.Equal(patch, fixture.Scalar("SELECT ProductVersion FROM __EFMigrationsHistory ORDER BY MigrationId LIMIT 1"));
        Assert.Equal(2L, fixture.Scalar("SELECT count(*) FROM __EFMigrationsHistory"));
        Assert.Equal((long)count, fixture.Scalar("SELECT count(*) FROM case_priorities WHERE priority='Normal'"));
        AssertHistory(history, VersionTwoCaseStore.HistoryFields(fixture));
        var store = new SqliteCaseStore(database, new ForbiddenClock());
        foreach (var record in cases)
        {
            Assert.Equal(record, store.Find(record.Id));
            Assert.Equal(new(record.Id, CasePriority.Normal), store.Priority(record.Id));
        }
        foreach (var (key, id) in bindings)
        {
            var original = cases.Single(row => row.Id == id);
            Assert.Equal(original with { Status = CaseStatus.New }, store.Create(original.Title, original.Description, key).Case);
            Assert.Equal(original, store.Find(id));
        }
        AssertHistory(history, VersionTwoCaseStore.HistoryFields(fixture));
        Assert.Throws<InvalidDataException>(() => database.Upgrade());
    }

    [Fact]
    public void ScaffoldedPriorityMigrationIsOnlyTransactionalCreateTableWithStrictConstraintAndForeignKey()
    {
        using var context = new CaseDbContextFactory().CreateDbContext([]);
        var assembly = context.GetService<IMigrationsAssembly>();
        Assert.Equal(2, assembly.Migrations.Count);
        var migration = assembly.CreateMigration(assembly.Migrations.Last().Value, context.Database.ProviderName!);
        var table = Assert.IsType<CreateTableOperation>(Assert.Single(migration.UpOperations));
        Assert.Equal("case_priorities", table.Name);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, migration.TargetModel);
        Assert.All(commands, command => Assert.False(command.TransactionSuppressed));
        Assert.Contains("STRICT", Assert.Single(commands).CommandText);
        Assert.False(context.Database.HasPendingModelChanges());
        using var fixture = new TemporaryCaseDatabase();
        Assert.Equal(1L, fixture.Scalar("SELECT strict FROM pragma_table_list WHERE name='case_priorities'"));
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM sqlite_schema WHERE name='__EFMigrationsLock'"));
        Assert.Throws<SqliteException>(() => fixture.Execute("INSERT INTO case_priorities VALUES ($id,'Normal')", ("$id", Guid.NewGuid().ToString("D"))));
        var record = new SqliteCaseStore(fixture.Database).Create("Synthetic", "Fictional");
        Assert.Throws<SqliteException>(() => fixture.Execute("UPDATE case_priorities SET priority='high'"));
        Assert.Throws<SqliteException>(() => fixture.Execute("UPDATE case_priorities SET priority=$bytes", ("$bytes", new byte[] { 1 })));
        Assert.Throws<SqliteException>(() => fixture.Execute("INSERT INTO case_priorities VALUES ($id,'Low')", ("$id", record.Id.ToString("D"))));
    }

    [Theory]
    [InlineData("DELETE FROM case_status_history")]
    [InlineData("UPDATE cases SET title=' corrupt '")]
    [InlineData("DELETE FROM __EFMigrationsHistory")]
    [InlineData("UPDATE __EFMigrationsHistory SET ProductVersion='10.0.15'")]
    [InlineData("INSERT INTO __EFMigrationsHistory VALUES ('unexpected','10.0.14')")]
    [InlineData("CREATE TABLE case_priorities (case_id TEXT PRIMARY KEY,priority TEXT)")]
    public void InvalidFormatFourIsRefusedWithoutRepair(string corruption)
    {
        using var fixture = new TemporaryCaseDatabase(false);
        VersionFourCaseStore.Initialize(fixture);
        fixture.Execute(corruption);
        var before = File.ReadAllBytes(fixture.Path);
        Assert.Throws<InvalidDataException>(() => fixture.Database.Upgrade());
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
    }

    [Theory]
    [InlineData("fresh")]
    [InlineData("adopted-3")]
    public async Task FailedUpgradeCommitRetainsWholeFormatFourAndRetryPreservesReplay(string family)
    {
        using var fixture = new TemporaryCaseDatabase(false);
        VersionFourCaseStore.Initialize(fixture, family);
        var schemas = Schemas(fixture);
        var history = VersionTwoCaseStore.HistoryFields(fixture);
        using (var blocker = fixture.Database.Open())
        using (var read = blocker.BeginTransaction(deferred: true))
        {
            using var command = blocker.CreateCommand();
            command.Transaction = read;
            command.CommandText = "SELECT count(*) FROM cases";
            Assert.Equal(7L, command.ExecuteScalar());
            var failure = await Assert.ThrowsAsync<SqliteException>(() => Task.Run(() => fixture.Database.Upgrade()));
            Assert.Equal(5, failure.SqliteErrorCode);
            read.Rollback();
        }
        Assert.Equal(4, fixture.Database.Inspect());
        Assert.Equal(schemas, Schemas(fixture));
        AssertHistory(history, VersionTwoCaseStore.HistoryFields(fixture));
        fixture.Database.Upgrade();
        Assert.Equal(5, fixture.Database.Inspect());
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
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("Priority adoption/replay cannot invent history.");
    }
}
