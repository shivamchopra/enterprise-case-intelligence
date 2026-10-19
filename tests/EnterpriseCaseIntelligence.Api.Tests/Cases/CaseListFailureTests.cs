using System.Net;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class CaseListFailureTests : CaseListApiTest
{
    [Fact]
    public async Task MissingRuntimeStorageIsNotAnEmptyListOrNewStore()
    {
        Seed(1);
        var moved = Database.Path + ".offline";
        File.Move(Database.Path, moved);
        try
        {
            using var response = await Client.GetAsync("/cases");
            await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
            Assert.DoesNotContain(Database.Path, await response.Content.ReadAsStringAsync());
            Assert.False(File.Exists(Database.Path));
            using var health = await Client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }
        finally
        {
            File.Move(moved, Database.Path);
        }
    }

    [Theory]
    [InlineData("title", " corrupted ")]
    [InlineData("description", " ")]
    [InlineData("status", "new")]
    [InlineData("status", "0")]
    [InlineData("status", "SYNTHETIC_SENSITIVE_MARKER")]
    [InlineData("id", "00000000-0000-0000-0000-000000000000")]
    [InlineData("id", "SYNTHETIC_SENSITIVE_MARKER")]
    [InlineData("id", "00000002-AAAA-4BBB-8CCC-DDDDDDDDDDDD")]
    public async Task InvalidRowsFailWithoutPartialSuccessOrRepair(string column, string value)
    {
        SeedMany(3);
        Database.Execute($"PRAGMA ignore_check_constraints=ON; UPDATE cases SET {column}=$value WHERE id=$id",
            ("$value", value), ("$id", FixtureId(2).ToString("D")));
        var original = File.ReadAllBytes(Database.Path);
        using var response = await Client.GetAsync("/cases?limit=100");
        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
        Assert.DoesNotContain("\"items\"", await response.Content.ReadAsStringAsync());
        Assert.Equal(original, File.ReadAllBytes(Database.Path));
    }

    [Fact]
    public async Task MaterializationStopsAfterLookaheadAndValidatesTheLookahead()
    {
        SeedMany(4, mixed: false);
        Database.Execute("UPDATE cases SET title=' corrupt ' WHERE id=$id", ("$id", FixtureId(3).ToString("D")));
        // Corruption beyond the page and lookahead must not be loaded into the result.
        var first = await ListAsync("/cases?limit=1");
        Assert.Equal(FixtureId(1), Assert.Single(first.Items).Id);
        Assert.Equal(FixtureId(1), first.NextAfter);

        // The same row now falls in the lookahead: the whole operation must fail safely.
        using var next = await Client.GetAsync($"/cases?limit=1&after={first.NextAfter:D}");
        await AssertProblemAsync(next, HttpStatusCode.InternalServerError, "internal_error");
        var beyond = await ListAsync($"/cases?limit=1&after={FixtureId(3):D}");
        Assert.Equal(FixtureId(4), Assert.Single(beyond.Items).Id);
        Assert.Null(beyond.NextAfter);
    }
}
