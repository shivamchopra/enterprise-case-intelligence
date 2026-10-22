using EnterpriseCaseIntelligence.Api.Cases;
using EnterpriseCaseIntelligence.Domain.Cases;
using Microsoft.Data.Sqlite;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class CaseStoreUpgradeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(205)]
    public void UpgradePreservesCasesAndAddsOnlyHonestBaselinesAcrossBatches(int count)
    {
        using var fixture = new TemporaryCaseDatabase(initialize: false);
        var records = LegacyCaseStore.Initialize(fixture, count);
        Assert.Equal(1, fixture.Database.Inspect());
        Assert.Throws<InvalidDataException>(() => fixture.Database.ValidateStartup());
        var database = new CaseDatabase(fixture.Path, new FixedClock());
        database.Upgrade();
        Assert.Equal(3, database.Inspect());
        database.ValidateStartup();
        var store = new SqliteCaseStore(database, new FixedClock());
        foreach (var record in records)
        {
            Assert.Equal(record, store.Find(record.Id));
            var entry = Assert.Single(store.History(record.Id)!);
            Assert.Equal(new CaseHistoryEntry(1, "Baseline", null, record.Status, "2026-10-20T12:00:00.0000000Z"), entry);
            if (record.Status != CaseStatus.Closed)
            {
                var next = record.Status == CaseStatus.New ? CaseStatus.InReview : CaseStatus.Closed;
                Assert.False(store.ChangeStatus(record.Id, next).IsConflict);
                Assert.Equal(2, store.History(record.Id)!.Count);
            }
        }

        Assert.Throws<InvalidDataException>(() => database.Upgrade());
        Assert.Equal(count, fixture.Count());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(101)]
    public void FailureAfterDdlOrPartialBaselinesRollsBackTheWholeUpgrade(int failAt)
    {
        using var fixture = new TemporaryCaseDatabase(initialize: false);
        var original = LegacyCaseStore.Initialize(fixture, 105);
        var database = new CaseDatabase(fixture.Path, new FailingClock(failAt));
        Assert.Throws<InvalidOperationException>(() => database.Upgrade());
        AssertLegacy(fixture, original);
    }

    [Fact]
    public async Task CommitLockFailureAfterVersionWriteLeavesWholeLegacyStoreAfterDisposal()
    {
        using var fixture = new TemporaryCaseDatabase(initialize: false);
        var original = LegacyCaseStore.Initialize(fixture);
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

        AssertLegacy(fixture, original);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("corrupt")]
    [InlineData("unknown-version")]
    [InlineData("extra-schema")]
    [InlineData("invalid-row")]
    [InlineData("version3")]
    public async Task CommandsRefuseInvalidUpgradeInputsWithoutReplacementOrHosting(string condition)
    {
        using var fixture = new TemporaryCaseDatabase(initialize: false);
        switch (condition)
        {
            case "empty": File.WriteAllBytes(fixture.Path, []); break;
            case "corrupt": File.WriteAllText(fixture.Path, "SYNTHETIC_SENSITIVE_MARKER"); break;
            case "unknown-version": LegacyCaseStore.Initialize(fixture); fixture.Execute("PRAGMA user_version=99"); break;
            case "extra-schema": LegacyCaseStore.Initialize(fixture); fixture.Execute("CREATE TABLE extra (id TEXT)"); break;
            case "invalid-row": LegacyCaseStore.Initialize(fixture); fixture.Execute("UPDATE cases SET title=' corrupt '"); break;
            case "version3": fixture.Database.Initialize(); break;
        }

        var before = File.Exists(fixture.Path) ? File.ReadAllBytes(fixture.Path) : null;
        var result = await ApiProcess.RunAsync("upgrade-store", "--CaseStorage:DatabasePath", fixture.Path);
        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("Now listening on:", result.Output);
        Assert.DoesNotContain(fixture.Path, result.Output);
        Assert.DoesNotContain("SYNTHETIC_SENSITIVE_MARKER", result.Output);
        Assert.DoesNotContain("Exception", result.Output);
        var inspected = await ApiProcess.RunAsync("inspect-store", "--CaseStorage:DatabasePath", fixture.Path);
        Assert.Equal(condition == "version3" ? 0 : 1, inspected.ExitCode);
        Assert.DoesNotContain("Now listening on:", inspected.Output);
        Assert.DoesNotContain(fixture.Path, inspected.Output);
        Assert.DoesNotContain("SYNTHETIC_SENSITIVE_MARKER", inspected.Output);
        if (before is null) Assert.False(File.Exists(fixture.Path));
        else Assert.Equal(before, File.ReadAllBytes(fixture.Path));
    }

    [Fact]
    public async Task CommandsExposeSupportedVersionWithoutImplicitUpgradeAndEnforceForeignKeys()
    {
        using var fixture = new TemporaryCaseDatabase(initialize: false);
        var legacy = LegacyCaseStore.Initialize(fixture);
        var inspect = await ApiProcess.RunAsync("inspect-store", "--CaseStorage:DatabasePath", fixture.Path);
        Assert.Equal(0, inspect.ExitCode);
        Assert.Contains("Case store version: 1.", inspect.Output);
        var startup = await ApiProcess.RunAsync("--CaseStorage:DatabasePath", fixture.Path);
        Assert.Equal(1, startup.ExitCode);
        Assert.Contains("upgrade-store", startup.Output);
        Assert.Equal(1, fixture.Database.Inspect());
        var upgraded = await ApiProcess.RunAsync("upgrade-store", "--CaseStorage:DatabasePath", fixture.Path);
        Assert.Equal(0, upgraded.ExitCode);
        Assert.Contains("Case store upgraded.", upgraded.Output);
        var current = await ApiProcess.RunAsync("inspect-store", "--CaseStorage:DatabasePath", fixture.Path);
        Assert.Equal(0, current.ExitCode);
        Assert.Contains("Case store version: 3.", current.Output);
        foreach (var output in new[] { inspect.Output, upgraded.Output, current.Output })
            Assert.DoesNotContain("Now listening on:", output);
        using (var factory = fixture.CreateFactory())
        using (var client = factory.CreateClient())
        {
            foreach (var record in legacy)
            {
                var baseline = Assert.Single(await CaseHistoryApiTests.ReadHistoryAsync(client, record.Id));
                Assert.Equal("Baseline", baseline.Kind);
                Assert.Equal(record.Status.ToString(), baseline.To);
                Assert.Null(baseline.From);
            }
        }

        using var first = fixture.Database.Open();
        using var second = fixture.Database.Open();
        foreach (var connection in new[] { first, second })
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys";
            Assert.Equal(1L, command.ExecuteScalar());
        }

        var error = Assert.Throws<SqliteException>(() => fixture.Execute("""
            INSERT INTO case_status_history VALUES ('11111111-1111-4111-8111-111111111111',1,'Baseline',NULL,'New','2026-10-20T12:00:00.0000000Z')
            """));
        Assert.Equal(19, error.SqliteErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectionValidatesCasesAndHistoryWithoutRepair(bool history)
    {
        using var fixture = new TemporaryCaseDatabase(initialize: false);
        LegacyCaseStore.Initialize(fixture);
        if (history) fixture.Database.Upgrade();
        fixture.Execute(history ? "DELETE FROM case_status_history" : "UPDATE cases SET title=' corrupt '");
        var before = File.ReadAllBytes(fixture.Path);
        var result = await ApiProcess.RunAsync("inspect-store", "--CaseStorage:DatabasePath", fixture.Path);
        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("Case store version:", result.Output);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
    }

    internal static void AssertLegacy(TemporaryCaseDatabase fixture, CaseSnapshot[] expected)
    {
        Assert.Equal(1, fixture.Database.Inspect());
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM sqlite_schema WHERE name='case_status_history'"));
        var store = new SqliteCaseStore(fixture.Database);
        Assert.All(expected, record => Assert.Equal(record, store.Find(record.Id)));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class FailingClock(int failAt) : TimeProvider
    {
        private int _reads;
        public override DateTimeOffset GetUtcNow() => ++_reads == failAt
            ? throw new InvalidOperationException("Controlled recording failure") : DateTimeOffset.UtcNow;
    }
}
