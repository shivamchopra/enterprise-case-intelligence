using EnterpriseCaseIntelligence.Domain.Cases;

namespace EnterpriseCaseIntelligence.Domain.Tests.Cases;

public class CaseCreationTests
{
    private const string ValidTitle = "Correct the sample guide";
    private const string ValidDescription = "Update the fictional example section.";

    [Fact]
    public void CreateGeneratesIdentifierAndStartsNew()
    {
        var caseRecord = new Case(ValidTitle, ValidDescription);

        Assert.NotEqual(Guid.Empty, caseRecord.Id);
        Assert.Equal(ValidTitle, caseRecord.Title);
        Assert.Equal(ValidDescription, caseRecord.Description);
        Assert.Equal(CaseStatus.New, caseRecord.Status);
    }

    [Fact]
    public void CreateTrimsSurroundingWhitespaceAndPreservesInternalWhitespace()
    {
        var caseRecord = new Case(" \tExample  request\r\n", "\nExample\tdescription\u00a0");

        Assert.Equal("Example  request", caseRecord.Title);
        Assert.Equal("Example\tdescription", caseRecord.Description);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(120, 2_000)]
    public void CreateAcceptsTextLengthBoundariesAfterTrimming(int titleLength, int descriptionLength)
    {
        var title = new string('t', titleLength);
        var description = new string('d', descriptionLength);

        var caseRecord = new Case($"  {title}  ", $"\n{description}\t");

        Assert.Equal(title, caseRecord.Title);
        Assert.Equal(description, caseRecord.Description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    [InlineData("\u00a0\u2003")]
    public void CreateRejectsMissingOrWhitespaceTitle(string? title)
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() => new Case(title!, ValidDescription));

        Assert.Equal("title", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    [InlineData("\u00a0\u2003")]
    public void CreateRejectsMissingOrWhitespaceDescription(string? description)
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() => new Case(ValidTitle, description!));

        Assert.Equal("description", exception.ParamName);
    }

    [Fact]
    public void CreateRejectsTitleAboveMaximumAfterTrimming()
    {
        var title = $" {new string('t', 121)} ";

        var exception = Assert.Throws<ArgumentException>(() => new Case(title, ValidDescription));

        Assert.Equal("title", exception.ParamName);
    }

    [Fact]
    public void CreateRejectsDescriptionAboveMaximumAfterTrimming()
    {
        var description = $" {new string('d', 2_001)} ";

        var exception = Assert.Throws<ArgumentException>(() => new Case(ValidTitle, description));

        Assert.Equal("description", exception.ParamName);
    }

    [Fact]
    public void TextLimitsUseUtf16StringLength()
    {
        const string supplementaryCharacter = "\U0001F4C4";
        var title = string.Concat(Enumerable.Repeat(supplementaryCharacter, 60));
        var description = string.Concat(Enumerable.Repeat(supplementaryCharacter, 1_000));

        var caseRecord = new Case(title, description);

        Assert.Equal(title, caseRecord.Title);
        Assert.Equal(description, caseRecord.Description);
        Assert.Throws<ArgumentException>(() => new Case(title + supplementaryCharacter, description));
        Assert.Throws<ArgumentException>(() => new Case(title, description + supplementaryCharacter));
    }

    [Fact]
    public void IdenticalTextCreatesDistinctCasesWithIndependentState()
    {
        var first = new Case(ValidTitle, ValidDescription);
        var second = new Case(ValidTitle, ValidDescription);

        first.ChangeStatus(CaseStatus.InReview);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(CaseStatus.InReview, first.Status);
        Assert.Equal(CaseStatus.New, second.Status);
        Assert.Equal(ValidTitle, second.Title);
        Assert.Equal(ValidDescription, second.Description);
    }
}
