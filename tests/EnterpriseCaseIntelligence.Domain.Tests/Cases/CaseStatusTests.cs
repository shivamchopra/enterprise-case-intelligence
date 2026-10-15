using EnterpriseCaseIntelligence.Domain.Cases;

namespace EnterpriseCaseIntelligence.Domain.Tests.Cases;

public class CaseStatusTests
{
    [Theory]
    [InlineData(CaseStatus.New, CaseStatus.New, true)]
    [InlineData(CaseStatus.New, CaseStatus.InReview, true)]
    [InlineData(CaseStatus.New, CaseStatus.Closed, false)]
    [InlineData(CaseStatus.InReview, CaseStatus.New, false)]
    [InlineData(CaseStatus.InReview, CaseStatus.InReview, true)]
    [InlineData(CaseStatus.InReview, CaseStatus.Closed, true)]
    [InlineData(CaseStatus.Closed, CaseStatus.New, false)]
    [InlineData(CaseStatus.Closed, CaseStatus.InReview, false)]
    [InlineData(CaseStatus.Closed, CaseStatus.Closed, true)]
    public void ChangeStatusEnforcesCompleteTransitionMatrix(CaseStatus current, CaseStatus requested, bool isAllowed)
    {
        var caseRecord = CreateCaseAt(current);
        var original = (caseRecord.Id, caseRecord.Title, caseRecord.Description, caseRecord.Status);

        if (isAllowed)
        {
            caseRecord.ChangeStatus(requested);

            Assert.Equal((original.Id, original.Title, original.Description, requested),
                (caseRecord.Id, caseRecord.Title, caseRecord.Description, caseRecord.Status));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => caseRecord.ChangeStatus(requested));
            Assert.Equal(original, (caseRecord.Id, caseRecord.Title, caseRecord.Description, caseRecord.Status));
        }
    }

    [Theory]
    [InlineData(CaseStatus.New, -1)]
    [InlineData(CaseStatus.InReview, 3)]
    [InlineData(CaseStatus.Closed, int.MaxValue)]
    public void UndefinedStatusIsRejectedWithoutMutation(CaseStatus current, int undefinedStatus)
    {
        var caseRecord = CreateCaseAt(current);
        var original = (caseRecord.Id, caseRecord.Title, caseRecord.Description, caseRecord.Status);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => caseRecord.ChangeStatus((CaseStatus)undefinedStatus));

        Assert.Equal("status", exception.ParamName);
        Assert.Equal(original, (caseRecord.Id, caseRecord.Title, caseRecord.Description, caseRecord.Status));
    }

    [Fact]
    public void ForwardLifecycleAllowsImmediateRetries()
    {
        var caseRecord = new Case("Correct the sample guide", "Update the fictional example section.");
        var original = (caseRecord.Id, caseRecord.Title, caseRecord.Description);

        caseRecord.ChangeStatus(CaseStatus.InReview);
        caseRecord.ChangeStatus(CaseStatus.InReview);
        Assert.Equal(CaseStatus.InReview, caseRecord.Status);

        caseRecord.ChangeStatus(CaseStatus.Closed);
        caseRecord.ChangeStatus(CaseStatus.Closed);

        Assert.Equal(CaseStatus.Closed, caseRecord.Status);
        Assert.Equal(original, (caseRecord.Id, caseRecord.Title, caseRecord.Description));
    }

    private static Case CreateCaseAt(CaseStatus status)
    {
        var caseRecord = new Case("Correct the sample guide", "Update the fictional example section.");

        if (status is CaseStatus.InReview or CaseStatus.Closed)
        {
            caseRecord.ChangeStatus(CaseStatus.InReview);
        }

        if (status == CaseStatus.Closed)
        {
            caseRecord.ChangeStatus(CaseStatus.Closed);
        }

        return caseRecord;
    }
}
