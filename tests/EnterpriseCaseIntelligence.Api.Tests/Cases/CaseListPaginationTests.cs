namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class CaseListPaginationTests : CaseListApiTest
{
    [Theory]
    [InlineData("/cases")]
    [InlineData("/cases?")]
    [InlineData("/cases?status=New")]
    [InlineData("/cases?status=InReview")]
    [InlineData("/cases?status=Closed")]
    public async Task EmptyStoresReturnAnEmptyPage(string path)
    {
        var page = await ListAsync(path);
        Assert.Empty(page.Items);
        Assert.Null(page.NextAfter);
    }

    [Fact]
    public async Task DefaultsReturnTwentyInCanonicalOrderAndBareQueryUsesSameDefaults()
    {
        var expected = SeedMany(25);
        var page = await ListAsync();
        Assert.Equal(expected.Take(20), page.Items);
        Assert.Equal(expected[19].Id, page.NextAfter);
        var bare = await ListAsync("/cases?");
        Assert.Equal(page.Items, bare.Items);
        Assert.Equal(page.NextAfter, bare.NextAfter);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("?status=New", "New")]
    [InlineData("?status=InReview", "InReview")]
    [InlineData("?status=Closed", "Closed")]
    [InlineData("?status=%4Eew", "New")]
    [InlineData("?%73tatus=New", "New")]
    public async Task FiltersMatchExactlyAndDoNotMutateData(string query, string? status)
    {
        var records = SeedMany(12);
        var before = File.ReadAllBytes(Database.Path);
        var page = await ListAsync("/cases" + query);
        Assert.Equal(records.Where(record => status is null || record.Status == status), page.Items);
        Assert.Null(page.NextAfter);
        Assert.Equal(before, File.ReadAllBytes(Database.Path));
        Assert.Equal(2L, Database.Scalar("PRAGMA user_version"));
        Database.Database.ValidateStartup();
    }

    [Fact]
    public async Task FilterWithoutMatchesReturnsEmptyAndDecodedLimitIsAccepted()
    {
        Seed(1, "Closed");
        var empty = await ListAsync("/cases?status=New");
        Assert.Empty(empty.Items);
        Assert.Null(empty.NextAfter);
        Seed(2, "Closed");
        var page = await ListAsync("/cases?status=Closed&limit=%31");
        Assert.Single(page.Items);
        Assert.Equal(FixtureId(1), page.NextAfter);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(20, 19)]
    [InlineData(20, 20)]
    [InlineData(20, 21)]
    [InlineData(100, 99)]
    [InlineData(100, 100)]
    [InlineData(100, 101)]
    public async Task LimitsAndExactFinalPagesHaveCorrectContinuation(int limit, int count)
    {
        var records = SeedMany(count, mixed: false);
        var page = await ListAsync($"/cases?limit={limit}");
        Assert.Equal(records.Take(limit), page.Items);
        Assert.Equal(count > limit ? FixtureId(limit) : (Guid?)null, page.NextAfter);
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(7, null)]
    [InlineData(20, "New")]
    [InlineData(100, null)]
    [InlineData(1, "Closed")]
    [InlineData(4, "InReview")]
    public async Task TraversalReturnsEveryMatchingCaseExactlyOnceAndTerminates(int limit, string? status)
    {
        var expected = SeedMany(107).Where(record => status is null || record.Status == status).ToArray();
        var seen = new List<CaseRepresentation>();
        Guid? after = null;
        var finished = false;
        for (var attempt = 0; attempt <= expected.Length; attempt++)
        {
            var query = $"/cases?limit={limit}" + (status is null ? "" : "&status=" + status) +
                (after is null ? "" : $"&after={after:D}");
            var page = await ListAsync(query);
            Assert.True(page.Items.Length <= limit);
            if (after is not null)
            {
                Assert.All(page.Items, item => Assert.True(string.CompareOrdinal(item.Id.ToString("D"), after.Value.ToString("D")) > 0));
            }

            seen.AddRange(page.Items);
            if (page.NextAfter is null)
            {
                finished = true;
                break;
            }

            Assert.NotEmpty(page.Items);
            Assert.Equal(page.Items[^1].Id, page.NextAfter);
            after = page.NextAfter;
        }

        Assert.True(finished);
        Assert.Equal(expected, seen);
        Assert.Equal(seen.Count, seen.Select(record => record.Id).Distinct().Count());
    }

    [Theory]
    [InlineData(5, null, new[] { 10, 20, 30, 40 })]
    [InlineData(10, null, new[] { 20, 30, 40 })]
    [InlineData(15, null, new[] { 20, 30, 40 })]
    [InlineData(20, "New", new[] { 40 })]
    [InlineData(30, "New", new[] { 40 })]
    [InlineData(40, null, new int[] { })]
    [InlineData(999, null, new int[] { })]
    public async Task BoundaryIsExclusiveAndDoesNotRequireALookup(int boundary, string? status, int[] expected)
    {
        Seed(40);
        Seed(20, "Closed");
        Seed(10);
        Seed(30, "InReview");
        var page = await ListAsync($"/cases?after={FixtureId(boundary):D}" + (status is null ? "" : "&status=" + status));
        Assert.Equal(expected.Select(FixtureId), page.Items.Select(record => record.Id));
        Assert.Null(page.NextAfter);
    }

    [Fact]
    public async Task UppercaseBoundaryIsNormalizedAndPageSizeCanChange()
    {
        SeedMany(3, mixed: false);
        var first = await ListAsync("/cases?limit=1");
        var next = await ListAsync($"/cases?limit=2&after={first.NextAfter!.Value.ToString("D").ToUpperInvariant()}");
        Assert.Equal(new[] { FixtureId(2), FixtureId(3) }, next.Items.Select(record => record.Id));
        Assert.Null(next.NextAfter);
    }

    [Fact]
    public async Task FilterAndBoundaryAreAppliedBeforePageSize()
    {
        SeedMany(10);
        var page = await ListAsync($"/cases?status=New&limit=2&after={FixtureId(2):D}");
        Assert.Equal(new[] { FixtureId(4), FixtureId(7) }, page.Items.Select(record => record.Id));
        Assert.Equal(FixtureId(7), page.NextAfter);
    }
}
