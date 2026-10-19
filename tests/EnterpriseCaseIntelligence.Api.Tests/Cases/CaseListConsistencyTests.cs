using EnterpriseCaseIntelligence.Api.Cases;
using EnterpriseCaseIntelligence.Domain.Cases;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class CaseListConsistencyTests : CaseListApiTest
{
    [Fact]
    public async Task LaterPagesReflectChangesWithoutChangingEarlierSnapshots()
    {
        var first = Seed(1);
        Seed(3);
        Seed(5);
        Seed(7, "InReview");
        var page = await ListAsync("/cases?status=New&limit=1");
        var storePage = Factory.Services.GetRequiredService<SqliteCaseStore>().List(1, CaseStatus.New);

        // Changes happen strictly after the first page, without scheduling assumptions or sleeps.
        await SetStatusAsync(first.Id, "InReview");
        await SetStatusAsync(FixtureId(3), "InReview");
        Seed(0); // A newly matching ID before the boundary is not included later in this traversal.
        Seed(4);

        var next = await ListAsync($"/cases?status=New&after={page.NextAfter:D}");
        Assert.Equal(new[] { FixtureId(4), FixtureId(5) }, next.Items.Select(item => item.Id));
        Assert.Null(next.NextAfter);
        Assert.Equal(first, Assert.Single(page.Items));
        Assert.Equal(CaseStatus.New, Assert.Single(storePage.Items).Status);
        Assert.Equal(first.Id, storePage.NextAfter);

        var restartedTraversal = await ListAsync("/cases?status=New");
        Assert.Equal(new[] { FixtureId(0), FixtureId(4), FixtureId(5) }, restartedTraversal.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task EarlierContinuationDoesNotPromiseLaterMembership()
    {
        Seed(1);
        Seed(2);
        var first = await ListAsync("/cases?status=New&limit=1");
        Assert.NotNull(first.NextAfter);
        await SetStatusAsync(FixtureId(2), "InReview");
        var second = await ListAsync($"/cases?status=New&after={first.NextAfter:D}");
        Assert.Empty(second.Items);
        Assert.Null(second.NextAfter);
    }

    [Fact]
    public async Task RestartReusesBoundariesAndIndependentFilesRemainIsolated()
    {
        var records = SeedMany(7);
        var first = await ListAsync("/cases?status=New&limit=1");
        var second = await ListAsync($"/cases?status=New&limit=1&after={first.NextAfter:D}");
        Factory.Dispose();
        using var reopened = Database.CreateFactory();
        using var client = reopened.CreateClient();
        var sameFirst = await ListAsync("/cases?status=New&limit=1", client);
        var sameSecond = await ListAsync($"/cases?status=New&limit=1&after={first.NextAfter:D}", client);
        Assert.Equal(first.Items, sameFirst.Items);
        Assert.Equal(first.NextAfter, sameFirst.NextAfter);
        Assert.Equal(second.Items, sameSecond.Items);
        Assert.Equal(second.NextAfter, sameSecond.NextAfter);

        using var otherDatabase = new TemporaryCaseDatabase();
        var other = Seed(99, database: otherDatabase);
        using var otherFactory = otherDatabase.CreateFactory();
        using var otherClient = otherFactory.CreateClient();
        var isolated = await ListAsync("/cases", otherClient);
        Assert.Equal(other, Assert.Single(isolated.Items));
        var retained = await ListAsync("/cases", client);
        Assert.Equal(records, retained.Items);
    }

    [Theory]
    [InlineData("Synthetic '); DROP TABLE cases; --", "Synthetic quoted text")]
    [InlineData("Synthetic 😀 title", "Synthetic\0description\twith internal whitespace")]
    public async Task ListPreservesFullTextAndSupportsRetrieveThenStatusWorkflow(string title, string description)
    {
        var created = await CreateAsync(" " + title + " ", "\n" + description + "\t");
        var page = await ListAsync();
        Assert.Equal(created, Assert.Single(page.Items));
        Assert.Equal(title, page.Items[0].Title);
        Assert.Equal(description, page.Items[0].Description);
        Assert.Equal(created, await ReadAsync(page.Items[0].Id));
        var updated = await SetStatusAsync(created.Id, "InReview");
        var filtered = await ListAsync("/cases?status=InReview");
        Assert.Equal(updated, Assert.Single(filtered.Items));
    }
}
