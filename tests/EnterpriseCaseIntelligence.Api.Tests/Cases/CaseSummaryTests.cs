using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EnterpriseCaseIntelligence.Api.Cases;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public sealed class CaseSummaryTests : CaseListApiTest
{
    private async Task<CaseSummary> Summary(HttpClient? client = null, string path = "/cases/summary")
    {
        using var response = await (client ?? Client).GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(response.Headers.Location);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "closed", "inReview", "new", "total" }, json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var result = new CaseSummary(json.RootElement.GetProperty("new").GetInt64(), json.RootElement.GetProperty("inReview").GetInt64(), json.RootElement.GetProperty("closed").GetInt64());
        Assert.Equal(result.Total, json.RootElement.GetProperty("total").GetInt64());
        return result;
    }

    [Fact]
    public async Task EmptyAndMixedStoresReturnExactCountsBeyondAListPageWithoutWriting()
    {
        Assert.Equal(new(0, 0, 0), await Summary(path: "/cases/summary?"));
        SeedMany(123);
        var before = File.ReadAllBytes(Database.Path);
        Assert.Equal(new(41, 41, 41), await Summary());
        Assert.Equal(before, File.ReadAllBytes(Database.Path));
        var page = await ListAsync("/cases?title=Synthetic&status=New&limit=1");
        Assert.Single(page.Items);
        Assert.Equal(new(41, 41, 41), await Summary());
    }

    [Fact]
    public async Task CountsReflectCurrentStatusesButNotNoopsOrCreationKeyReplay()
    {
        var key = Guid.NewGuid();
        Client.DefaultRequestHeaders.Add("Idempotency-Key", '"' + key.ToString("D") + '"');
        var created = await CreateAsync();
        var earlier = await Summary();
        Assert.Equal(new(1, 0, 0), earlier);
        await SetStatusAsync(created.Id, "InReview");
        Assert.Equal(new(0, 1, 0), await Summary());
        await SetStatusAsync(created.Id, "InReview");
        await SetStatusAsync(created.Id, "Closed");
        using var replay = await Client.PostAsJsonAsync("/cases", new { title = ValidTitle, description = ValidDescription });
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(new(0, 0, 1), await Summary());
        Assert.Equal(new(1, 0, 0), earlier);
        Assert.Equal(1L, Database.Scalar("SELECT count(*) FROM case_creation_keys"));
        Assert.Equal(3L, Database.Scalar("SELECT count(*) FROM case_status_history"));
    }

    [Theory]
    [InlineData("?status=New")]
    [InlineData("?title=SYNTHETIC_SENSITIVE_MARKER")]
    [InlineData("?limit=1")]
    [InlineData("?after=00000001-aaaa-4bbb-8ccc-dddddddddddd")]
    [InlineData("?x=1&x=2")]
    [InlineData("?STATUS=New")]
    [InlineData("?=value")]
    public async Task EveryQueryIsRejectedBeforeMissingStorageIsOpened(string query)
    {
        var moved = Database.Path + ".offline";
        File.Move(Database.Path, moved);
        try
        {
            using var response = await Client.GetAsync("/cases/summary" + query);
            await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed", "query");
            Assert.False(File.Exists(Database.Path));
        }
        finally { File.Move(moved, Database.Path); }
    }

    [Fact]
    public async Task MissingStorageFailsWithoutInventingZeroCountsOrCreatingAFile()
    {
        Seed(1);
        var moved = Database.Path + ".offline";
        File.Move(Database.Path, moved);
        try
        {
            using var response = await Client.GetAsync("/cases/summary");
            await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
            Assert.DoesNotContain(Database.Path, await response.Content.ReadAsStringAsync());
            Assert.False(File.Exists(Database.Path));
            using var health = await Client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }
        finally { File.Move(moved, Database.Path); }
    }

    [Theory]
    [InlineData("new")]
    [InlineData("0")]
    [InlineData("SYNTHETIC_SENSITIVE_MARKER")]
    public async Task UnrecognizedStoredStatusFailsWithoutPartialCountsOrRepair(string status)
    {
        SeedMany(9);
        Database.Execute("PRAGMA ignore_check_constraints=ON; UPDATE cases SET status=$status WHERE id=$id", ("$status", status), ("$id", FixtureId(9).ToString("D")));
        var before = File.ReadAllBytes(Database.Path);
        using var response = await Client.GetAsync("/cases/summary");
        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
        Assert.Equal(before, File.ReadAllBytes(Database.Path));
    }

    [Fact]
    public async Task RetainedRestartAndIndependentStoresHaveNoSharedCounts()
    {
        SeedMany(8);
        var expected = await Summary();
        await Factory.DisposeAsync();
        using var restarted = Database.CreateFactory();
        using var client = restarted.CreateClient();
        Assert.Equal(expected, await Summary(client));
        using var other = new TemporaryCaseDatabase();
        Seed(1, "Closed", database: other);
        using var otherHost = other.CreateFactory();
        using var otherClient = otherHost.CreateClient();
        Assert.Equal(new(0, 0, 1), await Summary(otherClient));
        Assert.Equal(expected, await Summary(client));
    }

    [Fact]
    public void RealProviderAggregatesInOneBoundedQueryWithoutLoadingCaseText()
    {
        SeedMany(105);
        using var capture = new QueryCapture(Database.Path);
        Assert.Equal(new(35, 35, 35), new SqliteCaseStore(Database.Database).Summary());
        var command = Assert.Single(capture.Commands);
        Assert.Contains("GROUP BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT(*)", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(4L, command.Parameters);
        Assert.DoesNotContain("\"title\"", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"description\"", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"id\"", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class QueryCapture : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly string _path;
        private readonly List<IDisposable> _subscriptions = [];
        public List<(string Sql, long[] Parameters)> Commands { get; } = [];
        public QueryCapture(string path) { _path = path; _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this)); }
        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == "Microsoft.EntityFrameworkCore") _subscriptions.Add(value.Subscribe(this));
        }
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key == RelationalEventId.CommandExecuted.Name && value.Value is CommandExecutedEventData data && data.Command.Connection is { } connection && new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connection.ConnectionString).DataSource == _path)
                Commands.Add((data.Command.CommandText, data.Command.Parameters.Cast<System.Data.Common.DbParameter>().Select(p => Convert.ToInt64(p.Value)).ToArray()));
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose() { foreach (var subscription in _subscriptions) subscription.Dispose(); }
    }
}
