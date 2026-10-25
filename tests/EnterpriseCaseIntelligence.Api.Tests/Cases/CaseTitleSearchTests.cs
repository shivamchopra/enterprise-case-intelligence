using System.Net;
using EnterpriseCaseIntelligence.Api.Cases;
using EnterpriseCaseIntelligence.Domain.Cases;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public sealed class CaseTitleSearchTests : CaseListApiTest
{
    [Theory]
    [InlineData("guide", "Sample guide", true)]
    [InlineData("Guide", "Sample guide", false)]
    [InlineData("guide", "Sample Guide", false)]
    [InlineData("missing", "Sample guide", false)]
    [InlineData("%_", "Literal %_ characters", true)]
    [InlineData("%_", "Literal ab characters", false)]
    [InlineData("\\", "Literal \\ character", true)]
    [InlineData("' OR 1=1--", "Literal ' OR 1=1-- text", true)]
    [InlineData("' OR 1=1--", "A different title", false)]
    [InlineData("Café", "Sample Café 😀", true)]
    [InlineData("CAFÉ", "Sample Café 😀", false)]
    [InlineData("Cafe\u0301", "Sample Café", false)]
    [InlineData("😀", "Sample 😀 guide", true)]
    [InlineData("a  b", "Sample a  b guide", true)]
    [InlineData("a b", "Sample a  b guide", false)]
    [InlineData("after", "Before\0after", true)]
    [InlineData("\0after", "Before\0after", true)]
    [InlineData("\0", "Before\0after", true)]
    public async Task MatchesOnlyLiteralTitleText(string term, string title, bool matches)
    {
        var record = Seed(1, title: title, description: "Description contains " + term);
        Seed(2, title: "Description-only match", description: term);
        var before = File.ReadAllBytes(Database.Path);
        var page = await ListAsync("/cases?title=" + Uri.EscapeDataString(term));
        Assert.Equal(matches ? new[] { record } : [], page.Items);
        Assert.Null(page.NextAfter);
        Assert.Equal(before, File.ReadAllBytes(Database.Path));
        Database.Database.ValidateStartup();
    }

    [Fact]
    public async Task DecodesNamesTrimsOuterWhitespaceAndAcceptsUtf16Boundaries()
    {
        var record = Seed(1, title: new string('t', 118) + "😀");
        var title = record.Title;
        var page = await ListAsync("/cases?%74itle=" + Uri.EscapeDataString(" \t\u0085" + title + "\r\n "));
        Assert.Equal(record, Assert.Single(page.Items));
        Assert.Equal(120, title.Length);
        Assert.Equal(Seed(2, title: "x"), Assert.Single((await ListAsync("/cases?title=x")).Items));
    }

    [Fact]
    public async Task FilterPrecedesBoundedPaginationAndCombinesWithStatusAndBoundary()
    {
        var records = Enumerable.Range(1, 110).Reverse().Select(key => Seed(key,
            key % 3 == 0 ? "Closed" : "New", key % 2 == 0 ? $"Needle {key}" : $"Other {key}")).ToArray();
        var expected = records.Where(record => record.Status == "New" && record.Title.Contains("Needle", StringComparison.Ordinal))
            .OrderBy(record => record.Id.ToString("D"), StringComparer.Ordinal).ToArray();
        var seen = new List<CaseRepresentation>();
        Guid? after = null;
        for (var attempt = 0; attempt <= expected.Length; attempt++)
        {
            var page = await ListAsync("/cases?title=Needle&status=New&limit=7" + (after is null ? "" : $"&after={after.Value.ToString("D").ToUpperInvariant()}"));
            seen.AddRange(page.Items);
            Assert.InRange(page.Items.Length, 0, 7);
            if (page.NextAfter is null) break;
            Assert.Equal(page.Items[^1].Id, page.NextAfter);
            after = page.NextAfter;
        }
        Assert.Equal(expected, seen);
        var unretainedId = FixtureId(5).ToString("D").Replace("-aaaa-", "-ffff-", StringComparison.Ordinal);
        var nonexistentBoundary = await ListAsync($"/cases?title=Needle&status=New&limit=1&after={unretainedId}");
        Assert.Equal(FixtureId(8), Assert.Single(nonexistentBoundary.Items).Id);
        var beyond = await ListAsync($"/cases?title=Needle&after={FixtureId(999):D}");
        Assert.Empty(beyond.Items);
        Assert.Null(beyond.NextAfter);
        var maximum = await ListAsync("/cases?title=Needle&limit=100");
        Assert.Equal(55, maximum.Items.Length);
        Assert.Null(maximum.NextAfter);
    }

    [Theory]
    [InlineData("title=", "title")]
    [InlineData("title", "title")]
    [InlineData("title=%20%09%0A", "title")]
    [InlineData("title=x&title=x", "title")]
    [InlineData("title=x&%74itle=y", "title")]
    [InlineData("Title=x", "query")]
    [InlineData("title=x&offset=1", "query")]
    [InlineData("title=x&status=all", "status")]
    [InlineData("title=x&limit=0", "limit")]
    [InlineData("title=x&after=invalid", "after")]
    public async Task InvalidSearchIsRejectedBeforeStorage(string query, string field)
    {
        await AssertInvalidOffline(query, field);
    }

    [Fact]
    public async Task OverlongSearchIsRejectedWithoutEchoOrStorageAccess()
        => await AssertInvalidOffline("title=" + Uri.EscapeDataString("SYNTHETIC_SENSITIVE_MARKER" + new string('x', 97)), "title");

    private async Task AssertInvalidOffline(string query, string field)
    {
        var moved = Database.Path + ".offline";
        var before = File.ReadAllBytes(Database.Path);
        File.Move(Database.Path, moved);
        try
        {
            using var response = await Client.GetAsync("/cases?" + query);
            await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed", field);
            Assert.False(File.Exists(Database.Path));
        }
        finally { File.Move(moved, Database.Path); }
        Assert.Equal(before, File.ReadAllBytes(Database.Path));
    }

    [Fact]
    public async Task StoreValidatesMalformedTextBeforeIoAndMatchingLookaheadBeforeSuccess()
    {
        var store = new SqliteCaseStore(new CaseDatabase(Database.Path + ".missing"));
        foreach (var invalid in new[] { "", " ", new string('x', 121), "\ud800", "x\udc00" })
        {
            var error = Assert.ThrowsAny<ArgumentException>(() => store.List(1, title: invalid));
            Assert.Equal("title", error.ParamName);
        }
        Seed(1, title: "Needle first");
        Seed(2, title: "Excluded valid title");
        Seed(3, title: "Needle second");
        Seed(4, title: "Needle third");
        Database.Execute("UPDATE cases SET description=' ' WHERE id=$id", ("$id", FixtureId(4).ToString("D")));
        // Invalid nonmatches are not materialized either.
        Database.Execute("UPDATE cases SET description=' ' WHERE id=$id", ("$id", FixtureId(2).ToString("D")));
        var before = File.ReadAllBytes(Database.Path);
        var first = await ListAsync("/cases?title=Needle&limit=1");
        Assert.Equal(FixtureId(1), Assert.Single(first.Items).Id);
        Assert.Equal(FixtureId(1), first.NextAfter);
        using var next = await Client.GetAsync($"/cases?title=Needle&limit=1&after={first.NextAfter:D}");
        await AssertProblemAsync(next, HttpStatusCode.InternalServerError, "internal_error");
        Assert.DoesNotContain("\"items\"", await next.Content.ReadAsStringAsync());
        Assert.Equal(before, File.ReadAllBytes(Database.Path));
        Assert.Throws<InvalidDataException>(() => Database.Database.Inspect());
    }

    [Fact]
    public async Task ValidSearchWithMissingStoreFailsSafelyRatherThanReturningNoMatches()
    {
        var moved = Database.Path + ".offline";
        File.Move(Database.Path, moved);
        try
        {
            using var response = await Client.GetAsync("/cases?title=Needle");
            await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
            Assert.False(File.Exists(Database.Path));
        }
        finally { File.Move(moved, Database.Path); }
    }

    [Fact]
    public async Task SearchViewsChangeBetweenPagesAndRemainIsolatedAfterRestart()
    {
        var first = Seed(10, title: "Needle first");
        Seed(30, title: "Needle third");
        var page = await ListAsync("/cases?title=Needle&status=New&limit=1");
        Assert.Equal(first, Assert.Single(page.Items));
        Seed(5, title: "Needle inserted before boundary");
        var later = Seed(20, title: "Needle inserted after boundary");
        await SetStatusAsync(FixtureId(30), "InReview");
        var continuation = $"/cases?title=Needle&status=New&limit=1&after={page.NextAfter:D}";
        using (var restarted = Database.CreateFactory())
        using (var client = restarted.CreateClient())
        {
            var next = await ListAsync(continuation, client);
            Assert.Equal(later, Assert.Single(next.Items));
            Assert.Null(next.NextAfter);
        }
        using var other = new TemporaryCaseDatabase();
        Seed(20, title: "Other title", database: other);
        using var otherFactory = other.CreateFactory();
        using var otherClient = otherFactory.CreateClient();
        Assert.Empty((await ListAsync(continuation, otherClient)).Items);
        Assert.Equal(first, Assert.Single(page.Items));
    }
}
