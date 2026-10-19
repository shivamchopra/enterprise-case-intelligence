namespace EnterpriseCaseIntelligence.Domain.Cases;

public sealed class Case
{
    public Case(string title, string description)
    {
        Title = NormalizeRequiredText(title, 120, nameof(title));
        Description = NormalizeRequiredText(description, 2_000, nameof(description));
        Id = Guid.NewGuid();
        Status = CaseStatus.New;
    }

    public Guid Id { get; }

    public string Title { get; }

    public string Description { get; }

    public CaseStatus Status { get; private set; }

    private Case(Guid id, string title, string description, CaseStatus status)
    {
        Id = id;
        Title = title;
        Description = description;
        Status = status;
    }

    public static Case Restore(Guid id, string title, string description, CaseStatus status)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A non-empty identifier is required.", nameof(id));
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), "The case status is not recognized.");
        }

        var normalizedTitle = NormalizeRequiredText(title, 120, nameof(title));
        var normalizedDescription = NormalizeRequiredText(description, 2_000, nameof(description));
        if (title != normalizedTitle || description != normalizedDescription)
        {
            throw new ArgumentException("Stored text must already be normalized.");
        }

        return new Case(id, title, description, status);
    }

    public void ChangeStatus(CaseStatus status)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), "The case status is not recognized.");
        }

        if (status == Status)
        {
            return;
        }

        var isAllowed = (Status, status) is
            (CaseStatus.New, CaseStatus.InReview) or
            (CaseStatus.InReview, CaseStatus.Closed);

        if (!isAllowed)
        {
            throw new InvalidOperationException("The requested case status transition is not allowed.");
        }

        Status = status;
    }

    private static string NormalizeRequiredText(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        var normalized = value.Trim();

        if (normalized.Length > maximumLength)
        {
            throw new ArgumentException($"The value must contain at most {maximumLength} characters after trimming.", parameterName);
        }

        return normalized;
    }
}
