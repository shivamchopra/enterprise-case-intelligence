using EnterpriseCaseIntelligence.Domain.Cases;

namespace EnterpriseCaseIntelligence.Domain.Tests.Cases;

public class CaseRestorationTests
{
    [Theory]
    [InlineData(CaseStatus.New)]
    [InlineData(CaseStatus.InReview)]
    [InlineData(CaseStatus.Closed)]
    public void RestoringPreservesIdentityTextAndStatus(CaseStatus status)
    {
        var id = Guid.NewGuid();
        var record = Case.Restore(id, "Synthetic  title", "Synthetic description", status);

        Assert.Equal(id, record.Id);
        Assert.Equal("Synthetic  title", record.Title);
        Assert.Equal("Synthetic description", record.Description);
        Assert.Equal(status, record.Status);
        record.ChangeStatus(status);
        Assert.Equal(status, record.Status);
    }

    [Fact]
    public void RestoringRejectsInvalidIdentityAndUndefinedStatus()
    {
        Assert.Throws<ArgumentException>(() => Case.Restore(Guid.Empty, "Title", "Description", CaseStatus.New));
        Assert.Throws<ArgumentOutOfRangeException>(() => Case.Restore(Guid.NewGuid(), "Title", "Description", (CaseStatus)42));
    }

    [Theory]
    [InlineData(null, "Description")]
    [InlineData("Title", null)]
    [InlineData("", "Description")]
    [InlineData("Title", " ")]
    [InlineData(" Title", "Description")]
    [InlineData("Title", "Description\n")]
    public void RestoringRejectsInvalidOrUnnormalizedText(string? title, string? description)
    {
        Assert.ThrowsAny<ArgumentException>(() => Case.Restore(Guid.NewGuid(), title!, description!, CaseStatus.New));
    }

    [Fact]
    public void RestoringEnforcesUtf16LengthBoundaries()
    {
        var title = string.Concat(Enumerable.Repeat("😀", 60));
        var description = new string('d', 2_000);
        var record = Case.Restore(Guid.NewGuid(), title, description, CaseStatus.InReview);
        Assert.Equal(title, record.Title);
        Assert.Equal(description, record.Description);
        Assert.Throws<ArgumentException>(() => Case.Restore(Guid.NewGuid(), title + "x", description, CaseStatus.New));
        Assert.Throws<ArgumentException>(() => Case.Restore(Guid.NewGuid(), title, description + "x", CaseStatus.New));
        record.ChangeStatus(CaseStatus.Closed);
        Assert.Throws<InvalidOperationException>(() => record.ChangeStatus(CaseStatus.New));
    }
}
