using EnterpriseCaseIntelligence.ClosureIntegration;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseCaseIntelligence.Api.Cases;

internal static class CaseClosures
{
    internal const string EventType = "case.closed.v1";

    internal static void Initialize(CaseDbContext context)
    {
        context.ClosureStreams.Add(new StoredClosureStream { Slot = 1, StreamId = Guid.NewGuid().ToString("D") });
        context.SaveSingleChange();
    }

    internal static Guid ReadStream(CaseDbContext context)
    {
        var rows = context.ClosureStreams.AsNoTracking().Take(2).ToArray();
        if (rows is not [{ Slot: 1 } row] || !TryCanonicalId(row.StreamId, out var stream))
            throw new InvalidDataException("Invalid closure stream identity.");
        return stream;
    }

    internal static bool TryCanonicalId(string? value, out Guid id)
        => Guid.TryParseExact(value, "D", out id) && id != Guid.Empty && value == id.ToString("D");

    internal static void Append(CaseDbContext context, Guid caseId, int historySequence)
    {
        ReadStream(context);
        context.ClosureNotices.Add(new StoredClosureNotice { CaseId = caseId.ToString("D"), HistorySequence = historySequence });
        context.SaveSingleChange();
    }

    internal static (ClosurePage? Page, string? Conflict) Read(CaseDbContext context, int limit, Guid? streamId, long after, string? afterProof)
    {
        var stream = ReadStream(context);
        if (streamId is { } expected && expected != stream) return (null, "handoff_source_changed");
        if (after != 0)
        {
            var checkpoint = Query(context, context.ClosureNotices.Where(row => row.Position == after)).SingleOrDefault();
            if (checkpoint is null) return (null, "handoff_checkpoint_unknown");
            var notice = ToNotice(checkpoint);
            if (afterProof is not null && afterProof != ClosureCheckpointProof.Create(stream.ToString("D"), notice.Position,
                    notice.CaseId.ToString("D"), notice.HistorySequence, notice.ClosedAtUtc))
                return (null, "handoff_checkpoint_changed");
        }

        var items = ReadAfter(context, after, limit + 1).Select(ToNotice).ToArray();
        var more = items.Length > limit;
        if (more) items = items[..limit];
        return (new ClosurePage(stream, items, items.Length == 0 ? after : items[^1].Position, more), null);
    }

    internal static void Inspect(CaseDbContext context)
    {
        long after = 0;
        while (true)
        {
            var batch = ReadAfter(context, after, 100);
            if (batch.Length == 0) return;
            foreach (var row in batch) ToNotice(row);
            after = batch[^1].Position;
        }
    }

    private static ClosureRow[] ReadAfter(CaseDbContext context, long after, int limit)
        => Query(context, context.ClosureNotices.Where(row => row.Position > after).OrderBy(row => row.Position).Take(limit)).ToArray();

    // Keep the notice visible even if its history reference was damaged outside the app.
    private static IQueryable<ClosureRow> Query(CaseDbContext context, IQueryable<StoredClosureNotice> notices) =>
        from notice in notices.AsNoTracking()
        join history in context.History.AsNoTracking()
            on new { notice.CaseId, Sequence = notice.HistorySequence } equals new { history.CaseId, history.Sequence } into matches
        from history in matches.DefaultIfEmpty()
        orderby notice.Position
        select new ClosureRow(notice.Position, notice.CaseId, notice.HistorySequence, history);

    private static ClosureNotice ToNotice(ClosureRow row)
    {
        if (row.Position <= 0 || !TryCanonicalId(row.CaseId, out var caseId) || row.HistorySequence is < 2 or > 3 ||
            row.History is not { Kind: "StatusChanged", FromStatus: "InReview", ToStatus: "Closed" } history ||
            !CaseHistory.IsTimestamp(history.RecordedAtUtc))
            throw new InvalidDataException("Invalid retained closure notice.");
        return new ClosureNotice(row.Position, caseId, (int)row.HistorySequence, history.RecordedAtUtc);
    }

    private sealed record ClosureRow(long Position, string CaseId, long HistorySequence, StoredHistory? History);
}

internal sealed record ClosureNotice(long Position, Guid CaseId, int HistorySequence, string ClosedAtUtc);
internal sealed record ClosurePage(Guid StreamId, ClosureNotice[] Items, long NextAfter, bool HasMore);
