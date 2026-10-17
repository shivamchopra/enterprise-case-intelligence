using EnterpriseCaseIntelligence.Domain.Cases;

namespace EnterpriseCaseIntelligence.Api.Cases;

public sealed class InMemoryCaseStore
{
    private readonly Dictionary<Guid, Case> _cases = [];
    private readonly Lock _gate = new();

    public CaseSnapshot Create(string title, string description)
    {
        lock (_gate)
        {
            var caseRecord = new Case(title, description);
            _cases.Add(caseRecord.Id, caseRecord);
            return Snapshot(caseRecord);
        }
    }

    public CaseSnapshot? Find(Guid id)
    {
        lock (_gate)
        {
            return _cases.TryGetValue(id, out var caseRecord) ? Snapshot(caseRecord) : null;
        }
    }

    public CaseSnapshot? ChangeStatus(Guid id, CaseStatus status)
    {
        lock (_gate)
        {
            if (!_cases.TryGetValue(id, out var caseRecord))
            {
                return null;
            }

            caseRecord.ChangeStatus(status);
            return Snapshot(caseRecord);
        }
    }

    private static CaseSnapshot Snapshot(Case caseRecord) =>
        new(caseRecord.Id, caseRecord.Title, caseRecord.Description, caseRecord.Status);
}
