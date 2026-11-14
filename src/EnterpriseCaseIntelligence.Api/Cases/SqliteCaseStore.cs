using EnterpriseCaseIntelligence.Domain.Cases;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseCaseIntelligence.Api.Cases;

public sealed class SqliteCaseStore(CaseDatabase database, TimeProvider? timeProvider = null)
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public CaseSnapshot Create(string title, string description) => Create(title, description, null).Case!;

    public CaseCreation Create(string title, string description, Guid? key) => Create(new Case(title, description), key);

    internal CaseCreation Create(Case record, Guid? key)
    {
        if (key == Guid.Empty) throw new ArgumentException("Use a non-empty creation key.", nameof(key));
        lock (_gate)
        {
            using var connection = database.Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            using var context = database.Context(connection, transaction);
            if (key is { } requested && CaseCreationKeys.Find(context, requested) is { } original)
            {
                if (!string.Equals(record.Title, original.Title, StringComparison.Ordinal) ||
                    !string.Equals(record.Description, original.Description, StringComparison.Ordinal)) return new(null, true);
                transaction.Commit();
                return new(new(original.Id, original.Title, original.Description, CaseStatus.New), false);
            }

            context.Cases.Add(new StoredCase { Id = record.Id.ToString("D"), Title = record.Title, Description = record.Description, Status = "New" });
            context.SaveSingleChange();
            context.Priorities.Add(new StoredPriority { CaseId = record.Id.ToString("D"), Priority = record.Priority.ToString() });
            context.SaveSingleChange();
            CaseHistory.Insert(context, record.Id, CaseHistory.Record(1, "Created", null, record.Status, _clock));
            if (key is { } unbound) CaseCreationKeys.Insert(context, unbound, record.Id);
            transaction.Commit();
            return new(Snapshot(record), false);
        }
    }

    public CaseSnapshot? Find(Guid id)
    {
        lock (_gate)
        {
            using var connection = database.Open();
            using var context = database.Context(connection);
            var record = Read(context, id);
            return record is null ? null : Snapshot(record);
        }
    }

    public CasePage List(int limit, CaseStatus? status = null, Guid? after = null, string? title = null, CasePriority? priority = null)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        if (status is { } requested && !Enum.IsDefined(requested)) throw new ArgumentOutOfRangeException(nameof(status));
        if (after == Guid.Empty) throw new ArgumentException("Use a non-empty identifier boundary.", nameof(after));
        if (priority is { } requestedPriority && !Enum.IsDefined(requestedPriority)) throw new ArgumentOutOfRangeException(nameof(priority));
        var priorityText = priority?.ToString();
        var titleText = title is null ? null : CaseTitleFilter.Normalize(title);
        var statusText = status?.ToString();
        var boundary = after?.ToString("D");
        lock (_gate)
        {
            using var connection = database.Open();
            using var context = database.Context(connection);
            var rows = context.Cases.AsNoTracking().Include(row => row.PriorityState)
                .Where(row => (statusText == null || row.Status == statusText) &&
                    (titleText == null || row.Title.Contains(titleText)) &&
                    (priorityText == null || (row.PriorityState != null && row.PriorityState.Priority == priorityText)) &&
                    (boundary == null || string.Compare(EF.Functions.Collate(row.Id, "BINARY"), boundary) > 0))
                .OrderBy(row => EF.Functions.Collate(row.Id, "BINARY")).Take(limit + 1).ToArray();
            // Validate lookahead as well; materialized damaged rows cannot become partial success.
            var items = rows.Select(row => Snapshot(Restore(row))).ToList();
            Guid? nextAfter = null;
            if (items.Count > limit)
            {
                items.RemoveAt(items.Count - 1);
                nextAfter = items[^1].Id;
            }
            return new(items.AsReadOnly(), nextAfter);
        }
    }

    public CaseSummary Summary()
    {
        lock (_gate)
        {
            using var connection = database.Open();
            using var context = database.Context(connection);
            // One database aggregate/read view, never materialize case records.
            // A fourth group must be invalid: there are only three supported statuses.
            var groups = context.Cases.AsNoTracking().GroupBy(row => row.Status)
                .Select(group => new { Status = group.Key, Count = group.LongCount() })
                .OrderBy(group => group.Status).Take(4).ToArray();
            long newCases = 0, inReview = 0, closed = 0;
            foreach (var group in groups)
            {
                if (group.Count <= 0) throw new InvalidDataException("Invalid stored count.");
                switch (group.Status)
                {
                    case "New": newCases = group.Count; break;
                    case "InReview": inReview = group.Count; break;
                    case "Closed": closed = group.Count; break;
                    default: throw new InvalidDataException("Invalid stored status.");
                }
            }
            return new(newCases, inReview, closed);
        }
    }

    public IReadOnlyList<CaseHistoryEntry>? History(Guid id)
    {
        lock (_gate)
        {
            using var connection = database.Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            using var context = database.Context(connection, transaction);
            var record = Read(context, id);
            if (record is null) return null;
            var entries = CaseHistory.Read(context, record);
            transaction.Commit();
            return entries;
        }
    }

    public CaseStatusChange ChangeStatus(Guid id, CaseStatus status)
    {
        lock (_gate)
        {
            using var connection = database.Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            using var context = database.Context(connection, transaction);
            var canonical = id.ToString("D");
            var row = context.Cases.Include(row => row.PriorityState).SingleOrDefault(row => row.Id == canonical);
            if (row is null) return new(null, false);
            var record = Restore(row);
            var history = CaseHistory.Read(context, record);
            var previous = record.Status;
            try { record.ChangeStatus(status); }
            catch (InvalidOperationException) { return new(null, true); }
            if (record.Status != previous)
            {
                row.Status = record.Status.ToString();
                context.SaveSingleChange();
                var entry = CaseHistory.Record(history.Count + 1, "StatusChanged", previous, record.Status, _clock);
                CaseHistory.Insert(context, id, entry);
                if (record.Status == CaseStatus.Closed) CaseClosures.Append(context, id, entry.Sequence);
            }
            transaction.Commit();
            return new(Snapshot(record), false);
        }
    }

    internal (ClosurePage? Page, string? Conflict) ReadClosures(int limit, Guid? streamId, long after, string? afterProof)
    {
        lock (_gate)
        {
            using var connection = database.Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            using var context = database.Context(connection, transaction);
            var result = CaseClosures.Read(context, limit, streamId, after, afterProof);
            transaction.Commit();
            return result;
        }
    }

    public CasePrioritySnapshot? Priority(Guid id)
    {
        lock (_gate)
        {
            using var connection = database.Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            using var context = database.Context(connection, transaction);
            var record = Read(context, id);
            if (record is null) return null;
            CaseHistory.Read(context, record);
            transaction.Commit();
            return new(record.Id, record.Priority);
        }
    }

    public CasePriorityChange ChangePriority(Guid id, CasePriority priority)
    {
        if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        lock (_gate)
        {
            using var connection = database.Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            using var context = database.Context(connection, transaction);
            var canonical = id.ToString("D");
            var row = context.Cases.Include(row => row.PriorityState).SingleOrDefault(row => row.Id == canonical);
            if (row is null) return new(null, false);
            var record = Restore(row);
            CaseHistory.Read(context, record);
            var previous = record.Priority;
            try { record.ChangePriority(priority); }
            catch (InvalidOperationException) { return new(null, true); }
            if (record.Priority != previous)
            {
                row.PriorityState!.Priority = record.Priority.ToString();
                context.SaveSingleChange();
            }
            transaction.Commit();
            return new(new(id, record.Priority), false);
        }
    }

    internal static Case? Read(CaseDbContext context, Guid id, bool priority = true)
    {
        var canonical = id.ToString("D");
        var query = context.Cases.AsNoTracking();
        if (priority) query = query.Include(row => row.PriorityState);
        var row = query.SingleOrDefault(row => row.Id == canonical);
        return row is null ? null : Restore(row, priority);
    }

    internal static Case Restore(StoredCase row, bool priority = true)
    {
        try
        {
            var status = row.Status switch
            {
                "New" => CaseStatus.New,
                "InReview" => CaseStatus.InReview,
                "Closed" => CaseStatus.Closed,
                _ => throw new InvalidDataException("Invalid stored status.")
            };
            if (!Guid.TryParseExact(row.Id, "D", out var id) || row.Id != id.ToString("D"))
                throw new InvalidDataException("Invalid stored identifier.");
            var currentPriority = !priority ? CasePriority.Normal : row.PriorityState?.Priority switch
            {
                "Low" => CasePriority.Low,
                "Normal" => CasePriority.Normal,
                "High" => CasePriority.High,
                _ => throw new InvalidDataException("Invalid stored priority.")
            };
            return Case.Restore(id, row.Title, row.Description, status, currentPriority);
        }
        catch (ArgumentException) { throw new InvalidDataException("Invalid stored case values."); }
    }

    private static CaseSnapshot Snapshot(Case record) => new(record.Id, record.Title, record.Description, record.Status);
}

public sealed record CasePrioritySnapshot(Guid CaseId, CasePriority Priority);
public sealed record CasePriorityChange(CasePrioritySnapshot? Value, bool IsConflict);
public sealed record CaseStatusChange(CaseSnapshot? Case, bool IsConflict);
public sealed record CaseCreation(CaseSnapshot? Case, bool IsKeyReused);
public sealed record CaseSummary(long New, long InReview, long Closed)
{
    public long Total => checked(New + InReview + Closed);
}
