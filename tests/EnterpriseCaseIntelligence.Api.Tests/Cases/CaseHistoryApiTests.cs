using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EnterpriseCaseIntelligence.Api.Cases;
using EnterpriseCaseIntelligence.Domain.Cases;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class CaseHistoryApiTests : CaseApiTest
{
    [Fact]
    public async Task CompleteLifecycleHasExactBoundedHistoryAndImmutableEarlierViews()
    {
        var created = await CreateAsync();
        var first = await ReadHistoryAsync(Client, created.Id);
        var initial = Assert.Single(first);
        Assert.Equal((1, "Created", null, "New"), (initial.Sequence, initial.Kind, initial.From, initial.To));
        Assert.Equal(first, await ReadHistoryAsync(Client, created.Id, "?"));
        Assert.Equal(first, await ReadHistoryAsync(Client, created.Id, uppercase: true));
        await SetStatusAsync(created.Id, "New");
        Assert.Equal(first, await ReadHistoryAsync(Client, created.Id));
        await SetStatusAsync(created.Id, "InReview");
        await SetStatusAsync(created.Id, "Closed");
        var complete = await ReadHistoryAsync(Client, created.Id);
        Assert.Equal(3, complete.Length);
        Assert.Equal(initial, complete[0]);
        Assert.Equal((2, "StatusChanged", "New", "InReview"), (complete[1].Sequence, complete[1].Kind, complete[1].From, complete[1].To));
        Assert.Equal((3, "StatusChanged", "InReview", "Closed"), (complete[2].Sequence, complete[2].Kind, complete[2].From, complete[2].To));
        var other = await CreateAsync();
        Assert.Single(await ReadHistoryAsync(Client, other.Id));
        Factory.Dispose();
        using var reopened = Database.CreateFactory();
        using var reader = reopened.CreateClient();
        Assert.Equal(complete, await ReadHistoryAsync(reader, created.Id));
        Assert.Single(first);
        using var isolated = new TemporaryCaseDatabase();
        using var isolatedFactory = isolated.CreateFactory();
        using var isolatedClient = isolatedFactory.CreateClient();
        using var absent = await isolatedClient.GetAsync(PathFor(created.Id) + "/history");
        await AssertProblemAsync(absent, HttpStatusCode.NotFound, "case_not_found");
    }

    [Theory]
    [InlineData("bad", "", "id")]
    [InlineData("00000000-0000-0000-0000-000000000000", "", "id")]
    [InlineData("11111111111141118111111111111111", "", "id")]
    [InlineData("SYNTHETIC_SENSITIVE_MARKER", "", "id")]
    [InlineData("11111111-1111-4111-8111-111111111111", "?limit=1", "query")]
    [InlineData("11111111-1111-4111-8111-111111111111", "?after=", "query")]
    [InlineData("11111111-1111-4111-8111-111111111111", "?status=New", "query")]
    [InlineData("11111111-1111-4111-8111-111111111111", "?%6cimit=1&limit=1", "query")]
    [InlineData("11111111-1111-4111-8111-111111111111", "?SYNTHETIC_SENSITIVE_MARKER=secret", "query")]
    public async Task InvalidHistoryInputIsRejectedBeforeStorageAccess(string id, string query, string field)
    {
        var moved = Database.Path + ".offline";
        File.Move(Database.Path, moved);
        try
        {
            using var response = await Client.GetAsync($"/cases/{id}/history{query}");
            await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed", field);
            Assert.False(File.Exists(Database.Path));
        }
        finally { File.Move(moved, Database.Path); }
    }

    [Fact]
    public async Task MissingCaseAndUnavailableStorageHaveDistinctSafeFailures()
    {
        using var missing = await Client.GetAsync(PathFor(Guid.NewGuid()) + "/history");
        await AssertProblemAsync(missing, HttpStatusCode.NotFound, "case_not_found");
        var created = await CreateAsync();
        var moved = Database.Path + ".offline";
        File.Move(Database.Path, moved);
        try
        {
            using var response = await Client.GetAsync(PathFor(created.Id) + "/history");
            await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
            using var health = await Client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.False(File.Exists(Database.Path));
        }
        finally { File.Move(moved, Database.Path); }
    }

    internal static async Task<HistoryValue[]> ReadHistoryAsync(HttpClient client, Guid id, string query = "", bool uppercase = false)
    {
        var routeId = uppercase ? id.ToString("D").ToUpperInvariant() : id.ToString("D");
        using var response = await client.GetAsync($"/cases/{routeId}/history{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Headers.Location);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "items" }, document.RootElement.EnumerateObject().Select(p => p.Name));
        var result = document.RootElement.GetProperty("items").EnumerateArray().Select(item =>
        {
            Assert.Equal(new[] { "fromStatus", "kind", "recordedAtUtc", "sequence", "toStatus" },
                item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            var time = item.GetProperty("recordedAtUtc").GetString()!;
            Assert.True(DateTimeOffset.TryParseExact(time, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _));
            return new HistoryValue(item.GetProperty("sequence").GetInt32(), item.GetProperty("kind").GetString()!,
                item.GetProperty("fromStatus").GetString(), item.GetProperty("toStatus").GetString()!, time);
        }).ToArray();
        Assert.InRange(result.Length, 1, 3);
        Assert.Equal(Enumerable.Range(1, result.Length), result.Select(e => e.Sequence));
        return result;
    }

    internal sealed record HistoryValue(int Sequence, string Kind, string? From, string To, string Time);
}

public class CaseHistoryClockTests
{
    [Fact]
    public async Task HttpHostUsesTheInjectedClockAndPreservesItsExactUtcValues()
    {
        using var database = new TemporaryCaseDatabase();
        var clock = new SequenceClock();
        using var factory = database.CreateFactory(clock);
        using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/cases", new { title = "Synthetic", description = "Injected clock" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        foreach (var status in new[] { "New", "InReview", "InReview", "Closed", "Closed" })
        {
            using var response = await client.PutAsJsonAsync($"/cases/{id:D}/status", new { status });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var history = await CaseHistoryApiTests.ReadHistoryAsync(client, id);
        Assert.Equal(3, clock.Reads);
        Assert.Equal(new[] { "2026-10-20T12:00:00.0000000Z", "2026-10-20T12:00:00.0000000Z", "2026-10-19T12:00:00.0000000Z" }, history.Select(e => e.Time));
    }

    [Fact]
    public void ControlledClockCanRepeatOrGoBackwardAndNoopsDoNotReadTime()
    {
        using var database = new TemporaryCaseDatabase();
        var clock = new SequenceClock();
        var store = new SqliteCaseStore(database.Database, clock);
        var created = store.Create("Synthetic", "Controlled clock");
        var prior = Assert.Single(store.History(created.Id)!);
        store.ChangeStatus(created.Id, CaseStatus.New);
        store.ChangeStatus(created.Id, CaseStatus.Closed);
        Assert.Equal(1, clock.Reads);
        store.ChangeStatus(created.Id, CaseStatus.InReview);
        store.ChangeStatus(created.Id, CaseStatus.InReview);
        store.ChangeStatus(created.Id, CaseStatus.Closed);
        store.ChangeStatus(created.Id, CaseStatus.Closed);
        Assert.Equal(3, clock.Reads);
        var history = store.History(created.Id)!;
        Assert.Equal(prior.RecordedAtUtc, history[1].RecordedAtUtc);
        Assert.Equal("2026-10-19T12:00:00.0000000Z", history[2].RecordedAtUtc);
        Assert.Equal(prior, history[0]);
        Assert.Equal(new[] { 1, 2, 3 }, history.Select(e => e.Sequence));
    }

    private sealed class SequenceClock : TimeProvider
    {
        public int Reads { get; private set; }
        public override DateTimeOffset GetUtcNow() => ++Reads switch
        {
            1 or 2 => new DateTimeOffset(2026, 10, 20, 18, 0, 0, TimeSpan.FromHours(6)),
            3 => new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero),
            _ => throw new InvalidOperationException("Unexpected clock read.")
        };
    }
}
