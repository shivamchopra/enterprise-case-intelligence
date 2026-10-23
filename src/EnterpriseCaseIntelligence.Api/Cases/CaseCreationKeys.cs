using EnterpriseCaseIntelligence.Domain.Cases;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseCaseIntelligence.Api.Cases;

internal static class CaseCreationKeys
{
    internal static Case? Find(CaseDbContext context, Guid key)
    {
        var canonical = key.ToString("D");
        var rows = context.CreationKeys.AsNoTracking()
            .Where(row => EF.Functions.Collate(row.RequestKey, "BINARY") == canonical).Take(2).ToArray();
        if (rows.Length == 0) return null;
        if (rows.Length != 1) throw InvalidBinding();
        return Validate(context, rows[0].RequestKey, rows[0].CaseId);
    }

    internal static Case Validate(CaseDbContext context, string key, string caseId)
    {
        CanonicalId(key);
        var record = SqliteCaseStore.Read(context, CanonicalId(caseId)) ?? throw InvalidBinding();
        if (CaseHistory.Read(context, record)[0].Kind != "Created") throw InvalidBinding();
        return record;
    }

    internal static void Insert(CaseDbContext context, Guid key, Guid caseId)
    {
        context.CreationKeys.Add(new StoredCreationKey { RequestKey = key.ToString("D"), CaseId = caseId.ToString("D") });
        context.SaveSingleChange();
    }

    internal static void Inspect(CaseDbContext context)
    {
        string? after = null;
        while (true)
        {
            var batch = context.CreationKeys.AsNoTracking()
                .Where(row => after == null || string.Compare(EF.Functions.Collate(row.RequestKey, "BINARY"), after) > 0)
                .OrderBy(row => EF.Functions.Collate(row.RequestKey, "BINARY")).Take(100).ToArray();
            if (batch.Length == 0) return;
            foreach (var row in batch) Validate(context, row.RequestKey, row.CaseId);
            after = batch[^1].RequestKey;
        }
    }

    private static Guid CanonicalId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty || value != id.ToString("D")) throw InvalidBinding();
        return id;
    }

    private static InvalidDataException InvalidBinding() => new("Invalid stored creation binding.");
}
