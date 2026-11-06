using EnterpriseCaseIntelligence.Api.Cases;
using EnterpriseCaseIntelligence.Domain.Cases;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class CaseClosureLifecycleTests
{
    [Theory]
    [InlineData("fresh", "UTF-8", 205)]
    [InlineData("adopted-1", "UTF-16", 7)]
    [InlineData("adopted-2", "UTF-8", 7)]
    [InlineData("adopted-3", "UTF-16", 205)]
    public void FormatFiveAdoptionPreservesValuesAndStartsAStreamForFutureClosures(string family, string encoding, int count)
    {
        using var fixture = new TemporaryCaseDatabase(false);
        var (cases, bindings) = VersionFiveCaseStore.Initialize(fixture, family, count, encoding);
        var database = fixture.Database;
        var history = VersionTwoCaseStore.HistoryFields(fixture);
        var before = File.ReadAllBytes(fixture.Path);
        Assert.Equal(5, database.Inspect());
        Assert.Throws<InvalidDataException>(() => database.ValidateStartup());
        Assert.Throws<IOException>(() => database.Initialize());
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));

        database.Upgrade();
        Assert.Equal(6, database.Inspect());
        database.ValidateStartup();
        Assert.Equal(3L, fixture.Scalar("SELECT count(*) FROM __EFMigrationsHistory"));
        Assert.Equal("10.0.13", fixture.Scalar("SELECT ProductVersion FROM __EFMigrationsHistory ORDER BY MigrationId LIMIT 1"));
        var actualHistory = VersionTwoCaseStore.HistoryFields(fixture);
        Assert.Equal(history.Length, actualHistory.Length);
        for (var index = 0; index < history.Length; index++) Assert.Equal(history[index], actualHistory[index]);
        var store = new SqliteCaseStore(database);
        for (var index = 0; index < cases.Length; index++)
        {
            var record = cases[index];
            Assert.Equal(record, store.Find(record.Id));
            Assert.Equal(new(record.Id, VersionFiveCaseStore.PriorityAt(index)), store.Priority(record.Id));
        }
        foreach (var (key, id) in bindings)
        {
            var record = cases.Single(row => row.Id == id);
            Assert.Equal(record with { Status = CaseStatus.New }, store.Create(record.Title, record.Description, key).Case);
            Assert.Equal(record, store.Find(id));
        }

        var oldClosed = cases.First(row => row.Status == CaseStatus.Closed);
        store.ChangeStatus(oldClosed.Id, CaseStatus.Closed);
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM case_closure_notices"));
        var open = cases.First(row => row.Status == CaseStatus.InReview);
        store.ChangeStatus(open.Id, CaseStatus.Closed);
        var closure = store.History(open.Id)![^1];
        Assert.Equal(1L, fixture.Scalar("SELECT count(*) FROM case_closure_notices"));
        Assert.Equal(open.Id.ToString("D"), fixture.Scalar("SELECT case_id FROM case_closure_notices"));
        Assert.Equal((long)closure.Sequence, fixture.Scalar("SELECT history_sequence FROM case_closure_notices"));
        Assert.Equal(6, database.Inspect());
        Assert.Throws<InvalidDataException>(() => database.Upgrade());
    }

    [Theory]
    [InlineData("DELETE FROM case_priorities")]
    [InlineData("DELETE FROM __EFMigrationsHistory WHERE MigrationId LIKE '%AddCasePriority'")]
    [InlineData("CREATE TABLE case_closure_stream (slot INTEGER PRIMARY KEY,stream_id TEXT)")]
    public void InvalidFormatFiveIsRefusedWithoutChangingFile(string corruption)
    {
        using var fixture = new TemporaryCaseDatabase(false);
        VersionFiveCaseStore.Initialize(fixture);
        fixture.Execute(corruption);
        var before = File.ReadAllBytes(fixture.Path);
        Assert.Throws<InvalidDataException>(() => fixture.Database.Upgrade());
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
    }

    [Fact]
    public void GeneratedClosureTablesEnforceStrictnessIdentityAndHistoryReference()
    {
        using var context = new CaseDbContextFactory().CreateDbContext([]);
        var assembly = context.GetService<IMigrationsAssembly>();
        var entry = assembly.Migrations.Single(row => row.Key.EndsWith("_AddCaseClosureFeed", StringComparison.Ordinal));
        var migration = assembly.CreateMigration(entry.Value, context.Database.ProviderName!);
        Assert.Equal(2, migration.UpOperations.Count);
        Assert.All(migration.UpOperations, operation => Assert.IsType<CreateTableOperation>(operation));
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, migration.TargetModel);
        Assert.All(commands, command =>
        {
            Assert.False(command.TransactionSuppressed);
            Assert.Contains("STRICT", command.CommandText);
        });

        using var fixture = new TemporaryCaseDatabase();
        Assert.Equal(2L, fixture.Scalar("SELECT count(*) FROM pragma_table_list WHERE name GLOB 'case_closure_*' AND strict=1"));
        Assert.Throws<SqliteException>(() => fixture.Execute("INSERT INTO case_closure_stream VALUES (2,'extra')"));
        Assert.Throws<SqliteException>(() => fixture.Execute("INSERT INTO case_closure_notices (case_id,history_sequence) VALUES ('missing',3)"));
        var store = new SqliteCaseStore(fixture.Database);
        var record = store.Create("Synthetic close", "Synthetic fixture");
        store.ChangeStatus(record.Id, CaseStatus.InReview);
        store.ChangeStatus(record.Id, CaseStatus.Closed);
        Assert.Throws<SqliteException>(() => fixture.Execute("INSERT INTO case_closure_notices (case_id,history_sequence) SELECT case_id,history_sequence FROM case_closure_notices"));
        Assert.Throws<SqliteException>(() => fixture.Execute("DELETE FROM case_status_history WHERE sequence=3"));
        fixture.Execute("UPDATE case_closure_stream SET stream_id='invalid'");
        Assert.Throws<InvalidDataException>(() => fixture.Database.ValidateStartup());
        Assert.Throws<InvalidDataException>(() => fixture.Database.Inspect());
    }
}
