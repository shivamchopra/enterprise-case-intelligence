using EnterpriseCaseIntelligence.Domain.Cases;

namespace EnterpriseCaseIntelligence.Api.Cases;

public sealed record CaseSnapshot(Guid Id, string Title, string Description, CaseStatus Status);
