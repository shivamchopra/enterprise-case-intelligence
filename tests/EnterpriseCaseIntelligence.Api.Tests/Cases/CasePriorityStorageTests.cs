using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using EnterpriseCaseIntelligence.Api.Cases;
using EnterpriseCaseIntelligence.Domain.Cases;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class CasePriorityStorageTests : CaseListApiTest
{
    [Theory]
    [InlineData("DELETE FROM case_priorities")]
    [InlineData("UPDATE case_priorities SET priority='SYNTHETIC_SENSITIVE_MARKER'")]
    [InlineData("UPDATE case_priorities SET priority='high'")]
    public async Task MissingOrInvalidPriorityFailsEncounteredReadsWritesAndReplayWithoutRepair(string corruption)
    {
        Client.DefaultRequestHeaders.Add("Idempotency-Key", '"' + Guid.NewGuid().ToString("D") + '"');
        var record = await CreateAsync();
        Database.Execute("PRAGMA ignore_check_constraints=ON; " + corruption);
        var before = File.ReadAllBytes(Database.Path);
        foreach (var path in new[] { PathFor(record.Id), PathFor(record.Id) + "/priority", PathFor(record.Id) + "/history", "/cases" })
        {
            using var response = await Client.GetAsync(path);
            await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
        }
        using (var noop = await Client.PutAsJsonAsync(PathFor(record.Id) + "/priority", new { priority = "Normal" }))
            await AssertProblemAsync(noop, HttpStatusCode.InternalServerError, "internal_error");
        using (var status = await Client.PutAsJsonAsync(PathFor(record.Id) + "/status", new { status = "InReview" }))
            await AssertProblemAsync(status, HttpStatusCode.InternalServerError, "internal_error");
        using (var replay = await Client.PostAsJsonAsync("/cases", new { title = ValidTitle, description = ValidDescription }))
            await AssertProblemAsync(replay, HttpStatusCode.InternalServerError, "internal_error");
        Assert.Throws<InvalidDataException>(() => Database.Database.Inspect());
        Assert.Equal(before, File.ReadAllBytes(Database.Path));
    }

    [Theory]
    [InlineData("BEFORE", "ABORT")]
    [InlineData("AFTER", "ABORT")]
    [InlineData("BEFORE", "IGNORE")]
    public async Task FailedPriorityWritesRollbackAndCannotChangeHistoryOrKeys(string timing, string failure)
    {
        Client.DefaultRequestHeaders.Add("Idempotency-Key", '"' + Guid.NewGuid().ToString("D") + '"');
        var record = await CreateAsync();
        var history = StoredHistory(record.Id);
        var raise = failure == "IGNORE" ? "RAISE(IGNORE)" : "RAISE(ABORT,'SYNTHETIC_SENSITIVE_MARKER')";
        Database.Execute($"CREATE TRIGGER priority_failure {timing} UPDATE ON case_priorities BEGIN SELECT {raise}; END");
        using (var response = await Client.PutAsJsonAsync(PathFor(record.Id) + "/priority", new { priority = "High" }))
            await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
        Database.Execute("DROP TRIGGER priority_failure");
        Assert.Equal(6, Database.Database.Inspect());
        var store = new SqliteCaseStore(Database.Database);
        Assert.Equal(new(record.Id, CasePriority.Normal), store.Priority(record.Id));
        Assert.Equal(history, store.History(record.Id));
        Assert.Equal("New", store.Find(record.Id)!.Status.ToString());
        Assert.Equal(1L, Database.Scalar("SELECT count(*) FROM case_creation_keys"));
    }

    [Fact]
    public async Task FailedCommitDoesNotAcknowledgeOrPersistPriority()
    {
        var record = await CreateAsync();
        using (var reader = Database.Database.Open())
        using (var transaction = reader.BeginTransaction(deferred: true))
        {
            using var command = reader.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT count(*) FROM cases";
            Assert.Equal(1L, command.ExecuteScalar());
            using var response = await Client.PutAsJsonAsync(PathFor(record.Id) + "/priority", new { priority = "High" });
            await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
            transaction.Rollback();
        }
        Assert.Equal(new(record.Id, CasePriority.Normal), new SqliteCaseStore(Database.Database).Priority(record.Id));
        Assert.Single(StoredHistory(record.Id));
    }

    [Fact]
    public async Task SamePriorityIsAValidatedNoopWithoutAnyWritesAndInvalidEnumsPrecedeIo()
    {
        var record = await CreateAsync();
        await SetStatusAsync(record.Id, "InReview");
        await SetStatusAsync(record.Id, "Closed");
        Database.Execute("CREATE TRIGGER no_priority_write BEFORE UPDATE ON case_priorities BEGIN SELECT RAISE(ABORT,'Must not write'); END");
        var before = File.ReadAllBytes(Database.Path);
        using (var response = await Client.PutAsJsonAsync(PathFor(record.Id) + "/priority", new { priority = "Normal" }))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, File.ReadAllBytes(Database.Path));
        var missing = new SqliteCaseStore(new CaseDatabase(Database.Path + ".missing"));
        Assert.Throws<ArgumentOutOfRangeException>(() => missing.ChangePriority(record.Id, (CasePriority)3));
        Assert.Throws<ArgumentOutOfRangeException>(() => missing.List(1, priority: (CasePriority)(-1)));
        Assert.False(File.Exists(Database.Path + ".missing"));
    }

    [Fact]
    public void FilterRunsInOneBoundedJoinedProviderQueryAndValidatesLookahead()
    {
        SeedMany(105, mixed: false);
        Database.Execute("UPDATE case_priorities SET priority='High'");
        using var observer = new CaseCommandObserver(Database.Path);
        var page = new SqliteCaseStore(Database.Database).List(7, CaseStatus.New, FixtureId(3), "Synthetic", CasePriority.High);
        Assert.Equal(7, page.Items.Count);
        var command = Assert.Single(observer.Commands);
        Assert.Contains("LEFT JOIN", command.Sql);
        Assert.Contains("WHERE", command.Sql);
        Assert.Contains("\"priority\"", command.Sql);
        Assert.Contains("instr", command.Sql);
        Assert.Contains("ORDER BY", command.Sql);
        Assert.Contains("LIMIT", command.Sql);
        Assert.Contains(command.Parameters, value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) == "8");
        Assert.Equal(FixtureId(10), page.NextAfter);
        Database.Execute("DELETE FROM case_priorities WHERE case_id=$id", ("$id", FixtureId(8).ToString("D")));
        Assert.Throws<InvalidDataException>(() => new SqliteCaseStore(Database.Database).List(7));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IndependentWritersSerializePriorityAgainstClose(bool priorityFirst)
    {
        var record = await CreateAsync();
        await SetStatusAsync(record.Id, "InReview");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var match = priorityFirst ? "UPDATE \"case_priorities\"" : "UPDATE \"cases\"";
        using var observer = new CaseCommandObserver(Database.Path, sql =>
        {
            if (!sql.StartsWith(match, StringComparison.Ordinal)) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Synthetic writer barrier timed out.");
        });
        var firstStore = new SqliteCaseStore(Database.Database);
        var secondStore = new SqliteCaseStore(Database.Database);
        var first = Task.Run(() =>
        {
            if (priorityFirst) Assert.False(firstStore.ChangePriority(record.Id, CasePriority.High).IsConflict);
            else Assert.False(firstStore.ChangeStatus(record.Id, CaseStatus.Closed).IsConflict);
        });
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            var second = Task.Run(() =>
            {
                secondStarted.SetResult();
                if (priorityFirst) Assert.False(secondStore.ChangeStatus(record.Id, CaseStatus.Closed).IsConflict);
                else Assert.True(secondStore.ChangePriority(record.Id, CasePriority.High).IsConflict);
            });
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(first.IsCompleted);
            release.Set();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.Set(); await first.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.Equal(CaseStatus.Closed, firstStore.Find(record.Id)!.Status);
        Assert.Equal(priorityFirst ? CasePriority.High : CasePriority.Normal, firstStore.Priority(record.Id)!.Priority);
        Assert.Equal(3, firstStore.History(record.Id)!.Count);
    }

    [Fact]
    public async Task PrioritySurvivesRestartWithoutCrossStoreStateOrNewHistory()
    {
        var record = await CreateAsync();
        new SqliteCaseStore(Database.Database).ChangePriority(record.Id, CasePriority.High);
        await Factory.DisposeAsync();
        Database.Database.ValidateStartup();
        var retained = new SqliteCaseStore(new CaseDatabase(Database.Path));
        Assert.Equal(new(record.Id, CasePriority.High), retained.Priority(record.Id));
        using var other = new TemporaryCaseDatabase();
        Assert.Null(new SqliteCaseStore(other.Database).Priority(record.Id));
        Assert.Single(retained.History(record.Id)!);
    }
}

internal sealed class CaseCommandObserver : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly string _path;
    private readonly Action<string>? _executed;
    private readonly List<IDisposable> _subscriptions = [];
    public ConcurrentQueue<(string Sql, object?[] Parameters)> Commands { get; } = new();
    public CaseCommandObserver(string path, Action<string>? executed = null)
    {
        _path = path;
        _executed = executed;
        _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));
    }
    public void OnNext(DiagnosticListener value)
    {
        if (value.Name == "Microsoft.EntityFrameworkCore") _subscriptions.Add(value.Subscribe(this));
    }
    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Key != RelationalEventId.CommandExecuted.Name || value.Value is not CommandExecutedEventData data ||
            data.Command.Connection is not { } connection || new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource != _path) return;
        Commands.Enqueue((data.Command.CommandText, data.Command.Parameters.Cast<System.Data.Common.DbParameter>().Select(p => p.Value).ToArray()));
        _executed?.Invoke(data.Command.CommandText);
    }
    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void Dispose() { foreach (var subscription in _subscriptions) subscription.Dispose(); }
}
