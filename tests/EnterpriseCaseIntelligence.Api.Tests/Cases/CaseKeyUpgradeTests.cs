using EnterpriseCaseIntelligence.Api.Cases;
using EnterpriseCaseIntelligence.Domain.Cases;
using Microsoft.Data.Sqlite;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class CaseKeyUpgradeTests
{
    public static TheoryData<int, int, string> Formats
    {
        get
        {
            var data = new TheoryData<int, int, string>();
            foreach (var version in new[] { 1, 2 })
                foreach (var count in new[] { 0, 3, 205 })
                    foreach (var encoding in new[] { "UTF-8", "UTF-16" }) data.Add(version, count, encoding);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void DirectUpgradeRetainsOldCasesAndExactHistoryValuesWithoutInventingBindings(int version, int count, string encoding)
    {
        using var fixture = new TemporaryCaseDatabase(initialize: false);
        var records = version == 1 ? LegacyCaseStore.Initialize(fixture, count, encoding) : VersionTwoCaseStore.Initialize(fixture, count, encoding);
        var history = version == 2 ? VersionTwoCaseStore.HistoryFields(fixture) : [];
        Assert.Equal(version, fixture.Database.Inspect());
        var actualEncoding = Assert.IsType<string>(fixture.Scalar("PRAGMA encoding"));
        Assert.StartsWith(encoding, actualEncoding);
        Assert.Throws<InvalidDataException>(() => fixture.Database.ValidateStartup());
        var database = new CaseDatabase(fixture.Path, new UpgradeClock(version == 2));
        database.Upgrade();
        Assert.Equal(3, database.Inspect());
        database.ValidateStartup();
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM case_creation_keys"));
        var store = new SqliteCaseStore(database);
        foreach (var record in records)
        {
            Assert.Equal(record, store.Find(record.Id));
            if (version == 1) Assert.Equal("Baseline", Assert.Single(store.History(record.Id)!).Kind);
        }
        if (version == 2) AssertHistory(history, VersionTwoCaseStore.HistoryFields(fixture));
        var key = Guid.NewGuid();
        var keyed = store.Create("New keyed", "Synthetic", key).Case!;
        Assert.Equal(keyed, store.Create("New keyed", "Synthetic", key).Case);
        var keyless = store.Create("New keyless", "Synthetic");
        Assert.NotEqual(keyed.Id, keyless.Id);
        Assert.Equal(count + 2, fixture.Count());
        Assert.Equal(1L, fixture.Scalar("SELECT count(*) FROM case_creation_keys"));
        Assert.Throws<InvalidDataException>(() => database.Upgrade());
        Assert.Equal(3, database.Inspect());
        // Both accepted text encodings enforce the hidden-NUL exclusion.
        var error = Assert.Throws<SqliteException>(() => fixture.Execute("INSERT INTO case_creation_keys VALUES ($key,$id)",
            ("$key", Guid.NewGuid().ToString("D") + "\0tail"), ("$id", keyless.Id.ToString("D"))));
        Assert.Equal(19, error.SqliteErrorCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CommitFailureAfterAllDdlAndVersionWritePreservesEitherSourceFormat(int version)
    {
        using var fixture = new TemporaryCaseDatabase(initialize: false);
        var records = version == 1 ? LegacyCaseStore.Initialize(fixture) : VersionTwoCaseStore.Initialize(fixture);
        var history = version == 2 ? VersionTwoCaseStore.HistoryFields(fixture) : [];
        using (var blocker = fixture.Database.Open())
        using (var read = blocker.BeginTransaction(deferred: true))
        {
            using var command = blocker.CreateCommand();
            command.Transaction = read;
            command.CommandText = "SELECT count(*) FROM cases";
            Assert.Equal(3L, command.ExecuteScalar());
            var failure = await Assert.ThrowsAsync<SqliteException>(() => Task.Run(() => fixture.Database.Upgrade()));
            Assert.Equal(5, failure.SqliteErrorCode);
            read.Rollback();
        }
        Assert.Equal(version, fixture.Database.Inspect());
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM sqlite_schema WHERE name='case_creation_keys'"));
        var store = new SqliteCaseStore(fixture.Database);
        Assert.All(records, record => Assert.Equal(record, store.Find(record.Id)));
        if (version == 2) AssertHistory(history, VersionTwoCaseStore.HistoryFields(fixture));
        else Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM sqlite_schema WHERE name='case_status_history'"));
        fixture.Database.Upgrade();
        Assert.Equal(3, fixture.Database.Inspect());
    }

    [Theory]
    [InlineData("DELETE FROM case_status_history")]
    [InlineData("UPDATE case_status_history SET recorded_at_utc='invalid'")]
    [InlineData("UPDATE cases SET description=' corrupt '")]
    [InlineData("CREATE TABLE unexpected (id TEXT)")]
    public async Task InvalidVersionTwoIsRefusedWithoutRepairOrPartialUpgrade(string corruption)
    {
        using var fixture = new TemporaryCaseDatabase(initialize: false);
        VersionTwoCaseStore.Initialize(fixture);
        fixture.Execute(corruption);
        var before = File.ReadAllBytes(fixture.Path);
        var result = await ApiProcess.RunAsync("upgrade-store", "--CaseStorage:DatabasePath", fixture.Path);
        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain(fixture.Path, result.Output);
        Assert.DoesNotContain("Exception", result.Output);
        Assert.DoesNotContain("Now listening", result.Output);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        Assert.Equal(2L, fixture.Scalar("PRAGMA user_version"));
    }

    [Fact]
    public async Task ActualCommandsInspectTwoRefuseHostingAndUpgradeDirectlyToThree()
    {
        using var fixture = new TemporaryCaseDatabase(initialize: false);
        VersionTwoCaseStore.Initialize(fixture);
        var inspected = await ApiProcess.RunAsync("inspect-store", "--CaseStorage:DatabasePath", fixture.Path);
        Assert.Equal(0, inspected.ExitCode);
        Assert.Contains("Case store version: 2.", inspected.Output);
        var refused = await ApiProcess.RunAsync("--CaseStorage:DatabasePath", fixture.Path);
        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("upgrade-store", refused.Output);
        Assert.DoesNotContain("Now listening", refused.Output);
        var upgraded = await ApiProcess.RunAsync("upgrade-store", "--CaseStorage:DatabasePath", fixture.Path);
        Assert.Equal(0, upgraded.ExitCode);
        Assert.Contains("Case store upgraded.", upgraded.Output);
        Assert.Equal(3, fixture.Database.Inspect());
    }

    private static void AssertHistory(string[][] expected, string[][] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++) Assert.Equal(expected[index], actual[index]);
    }

    private sealed class UpgradeClock(bool forbidden) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => forbidden ? throw new InvalidOperationException("Version 2 must not read the clock")
            : new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    }
}
