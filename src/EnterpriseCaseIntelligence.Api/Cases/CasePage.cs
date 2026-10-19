namespace EnterpriseCaseIntelligence.Api.Cases;

public sealed record CasePage(IReadOnlyList<CaseSnapshot> Items, Guid? NextAfter);
