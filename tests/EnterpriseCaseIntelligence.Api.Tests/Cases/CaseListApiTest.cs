using System.Net;
using System.Text.Json;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public abstract class CaseListApiTest : CaseApiTest
{
    protected static Guid FixtureId(int key) => Guid.ParseExact($"{key:x8}-aaaa-4bbb-8ccc-dddddddddddd", "D");

    protected CaseRepresentation Seed(int key, string status = "New", string title = ValidTitle,
        string description = ValidDescription, TemporaryCaseDatabase? database = null)
    {
        var id = FixtureId(key);
        (database ?? Database).Execute("""
            INSERT INTO cases (id, title, description, status) VALUES ($id, $title, $description, $status);
            INSERT INTO case_status_history VALUES ($id, 1, 'Baseline', NULL, $status, '2026-10-20T12:00:00.0000000Z');
            """,
            ("$id", id.ToString("D")), ("$title", title), ("$description", description), ("$status", status));
        return new(id, title, description, status);
    }

    protected CaseRepresentation[] SeedMany(int count, bool mixed = true)
    {
        var result = new List<CaseRepresentation>();
        // Insertion order deliberately differs from canonical ID order.
        for (var key = count; key >= 1; key--)
        {
            var status = mixed ? new[] { "New", "InReview", "Closed" }[(key - 1) % 3] : "New";
            result.Add(Seed(key, status, $"Synthetic case {key}", $"Synthetic description {key}"));
        }

        return result.OrderBy(record => record.Id.ToString("D"), StringComparer.Ordinal).ToArray();
    }

    protected async Task<ListPage> ListAsync(string path = "/cases", HttpClient? client = null)
    {
        using var response = await (client ?? Client).GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Headers.Location);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = document.RootElement;
        Assert.Equal(new[] { "items", "nextAfter" }, body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        var items = body.GetProperty("items").EnumerateArray().Select(ReadCase).ToArray();
        var boundary = body.GetProperty("nextAfter");
        Guid? nextAfter = null;
        if (boundary.ValueKind != JsonValueKind.Null)
        {
            nextAfter = boundary.GetGuid();
            Assert.NotEqual(Guid.Empty, nextAfter);
            Assert.Equal(nextAfter.Value.ToString("D"), boundary.GetString());
        }

        return new(items, nextAfter);
    }

    protected sealed record ListPage(CaseRepresentation[] Items, Guid? NextAfter);
}
