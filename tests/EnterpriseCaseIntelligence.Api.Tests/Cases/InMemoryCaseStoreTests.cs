using EnterpriseCaseIntelligence.Api.Cases;
using EnterpriseCaseIntelligence.Domain.Cases;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class InMemoryCaseStoreTests
{
    [Fact]
    public void SnapshotsRemainIndependentOfLaterMutationsAndCallerCopies()
    {
        var store = new InMemoryCaseStore();
        var created = store.Create("Synthetic request", "Synthetic description");
        var read = store.Find(created.Id)!;

        var updated = store.ChangeStatus(created.Id, CaseStatus.InReview);
        var callerCopy = read with { Title = "Changed copy", Status = CaseStatus.Closed };

        Assert.Equal(CaseStatus.New, created.Status);
        Assert.Equal(CaseStatus.New, read.Status);
        Assert.Equal(CaseStatus.Closed, callerCopy.Status);
        Assert.Equal(updated, store.Find(created.Id));
        Assert.Equal("Synthetic request", store.Find(created.Id)!.Title);
        Assert.Equal(CaseStatus.InReview, store.Find(created.Id)!.Status);
    }

    [Fact]
    public void ValidationAndRejectedTransitionsPreserveStoredCases()
    {
        var store = new InMemoryCaseStore();
        var existing = store.Create("Synthetic request", "Synthetic description");

        Assert.ThrowsAny<ArgumentException>(() => store.Create(" ", "Synthetic description"));
        Assert.Throws<InvalidOperationException>(() => store.ChangeStatus(existing.Id, CaseStatus.Closed));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ChangeStatus(existing.Id, (CaseStatus)42));

        Assert.Equal(existing, store.Find(existing.Id));
    }

    [Fact]
    public void MissingCaseIsNotCreatedByStatusChange()
    {
        var store = new InMemoryCaseStore();
        var id = Guid.NewGuid();

        Assert.Null(store.ChangeStatus(id, CaseStatus.InReview));
        Assert.Null(store.Find(id));
    }
}
