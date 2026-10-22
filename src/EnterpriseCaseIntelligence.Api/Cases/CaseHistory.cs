using System.Globalization;
using EnterpriseCaseIntelligence.Domain.Cases;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseCaseIntelligence.Api.Cases;

public sealed record CaseHistoryEntry(int Sequence, string Kind, CaseStatus? FromStatus, CaseStatus ToStatus, string RecordedAtUtc);

internal static class CaseHistory
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    internal static CaseHistoryEntry Record(int sequence, string kind, CaseStatus? from, CaseStatus to, TimeProvider clock)
        => new(sequence, kind, from, to, clock.GetUtcNow().ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture));

    internal static void Insert(CaseDbContext context, Guid id, CaseHistoryEntry entry)
    {
        context.History.Add(new StoredHistory
        {
            CaseId = id.ToString("D"),
            Sequence = entry.Sequence,
            Kind = entry.Kind,
            FromStatus = entry.FromStatus?.ToString(),
            ToStatus = entry.ToStatus.ToString(),
            RecordedAtUtc = entry.RecordedAtUtc
        });
        context.SaveSingleChange();
    }

    internal static IReadOnlyList<CaseHistoryEntry> Read(CaseDbContext context, Case record)
    {
        var id = record.Id.ToString("D");
        var rows = context.History.AsNoTracking().Where(row => row.CaseId == id).OrderBy(row => row.Sequence).Take(4).ToArray();
        var entries = new List<CaseHistoryEntry>(4);
        foreach (var row in rows)
        {
            if (row.Sequence is < 1 or > 3) throw InvalidHistory();
            entries.Add(new((int)row.Sequence, row.Kind, row.FromStatus is null ? null : Status(row.FromStatus),
                Status(row.ToStatus), row.RecordedAtUtc));
        }

        Validate(record.Status, entries);
        return entries.AsReadOnly();
    }

    private static void Validate(CaseStatus current, List<CaseHistoryEntry> entries)
    {
        if (entries.Count is < 1 or > 3) throw InvalidHistory();
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry.Sequence != index + 1 ||
                !DateTimeOffset.TryParseExact(entry.RecordedAtUtc, TimestampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _)) throw InvalidHistory();

            if (index == 0)
            {
                if (entry.FromStatus is not null || entry.Kind is not ("Created" or "Baseline") ||
                    (entry.Kind == "Created" && entry.ToStatus != CaseStatus.New)) throw InvalidHistory();
            }
            else
            {
                var previous = entries[index - 1].ToStatus;
                if (entry.Kind != "StatusChanged" || entry.FromStatus != previous ||
                    (previous, entry.ToStatus) is not ((CaseStatus.New, CaseStatus.InReview) or (CaseStatus.InReview, CaseStatus.Closed)))
                    throw InvalidHistory();
            }
        }

        if (entries[^1].ToStatus != current) throw InvalidHistory();
    }

    private static CaseStatus Status(string value) => value switch
    {
        "New" => CaseStatus.New,
        "InReview" => CaseStatus.InReview,
        "Closed" => CaseStatus.Closed,
        _ => throw InvalidHistory()
    };

    private static InvalidDataException InvalidHistory() => new("Invalid stored case history.");
}
