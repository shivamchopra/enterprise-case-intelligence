using EnterpriseCaseIntelligence.Domain.Cases;

namespace EnterpriseCaseIntelligence.Domain.Tests.Cases;

public class CasePriorityTests
{
    public static IEnumerable<object[]> Matrix =>
        from status in Enum.GetValues<CaseStatus>()
        from current in Enum.GetValues<CasePriority>()
        from next in Enum.GetValues<CasePriority>()
        select new object[] { status, current, next };

    [Theory, MemberData(nameof(Matrix))]
    public void PriorityChangesFollowStatusRulesWithoutChangingCaseValues(CaseStatus status, CasePriority current, CasePriority next)
    {
        var record = Case.Restore(Guid.NewGuid(), "Synthetic title", "Fictional description", status, current);
        var id = record.Id;
        if (status == CaseStatus.Closed && current != next)
        {
            Assert.Throws<InvalidOperationException>(() => record.ChangePriority(next));
            Assert.Equal(current, record.Priority);
        }
        else
        {
            record.ChangePriority(next);
            Assert.Equal(next, record.Priority);
        }
        Assert.Equal(id, record.Id);
        Assert.Equal("Synthetic title", record.Title);
        Assert.Equal("Fictional description", record.Description);
        Assert.Equal(status, record.Status);
    }

    [Fact]
    public void CreationDefaultsNormalAndStatusProgressionPreservesPriority()
    {
        var record = new Case(" Synthetic title ", " Fictional description ");
        Assert.Equal(CasePriority.Normal, record.Priority);
        record.ChangePriority(CasePriority.High);
        record.ChangeStatus(CaseStatus.InReview);
        record.ChangeStatus(CaseStatus.Closed);
        record.ChangeStatus(CaseStatus.Closed);
        Assert.Equal(CasePriority.High, record.Priority);
        Assert.Equal("Synthetic title", record.Title);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void InvalidPriorityNeverMutatesEvenClosedCases(int value)
    {
        var record = Case.Restore(Guid.NewGuid(), "Synthetic", "Fictional", CaseStatus.Closed);
        Assert.Throws<ArgumentOutOfRangeException>(() => record.ChangePriority((CasePriority)value));
        Assert.Throws<ArgumentOutOfRangeException>(() => Case.Restore(record.Id, record.Title, record.Description, record.Status, (CasePriority)value));
        Assert.Equal(CasePriority.Normal, record.Priority);
    }
}
