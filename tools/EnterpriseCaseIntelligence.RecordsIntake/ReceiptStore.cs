using EnterpriseCaseIntelligence.ClosureIntegration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EnterpriseCaseIntelligence.RecordsIntake;

internal sealed class ReceiptStore(string path)
{
    private const string InitialMigration = "20261106051255_InitialReceiptStore";

    internal void Initialize()
    {
        // Reserve the file before SQLite opens it. A failed initialization leaves the reserved path for inspection.
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        using var connection = Connect();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var context = Context(connection, transaction, migrating: true);
        context.Database.Migrate();
        context.Checkpoints.Add(new ReceiptCheckpoint { Slot = 1 });
        Save(context, 1);
        ReadCheckpoint(context, tracking: false);
        transaction.Commit();
    }

    internal (Checkpoint Checkpoint, string? Proof) ReadContinuation()
    {
        using var connection = Connect();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var context = Context(connection, transaction);
        var row = ReadCheckpoint(context, tracking: false);
        string? proof = null;
        if (row.Position > 0)
        {
            var receipt = context.Receipts.AsNoTracking().Single(receipt => receipt.Position == row.Position);
            if (!ClosureFeed.IsCanonicalId(receipt.CaseId) || receipt.HistorySequence is < 2 or > 3 ||
                !ClosureFeed.IsTimestamp(receipt.ClosedAtUtc))
                throw new InvalidDataException("Invalid checkpoint receipt.");
            proof = ClosureCheckpointProof.Create(row.StreamId!, receipt.Position, receipt.CaseId, receipt.HistorySequence, receipt.ClosedAtUtc);
        }
        transaction.Commit();
        return (new Checkpoint(row.StreamId, row.Position), proof);
    }

    internal ReceiptInspection Inspect()
    {
        using var connection = Connect();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var context = Context(connection, transaction);
        var row = ReadCheckpoint(context, tracking: false);
        var count = context.Receipts.LongCount();
        transaction.Commit();
        return new ReceiptInspection(new Checkpoint(row.StreamId, row.Position), count);
    }

    internal ApplyResult Apply(Checkpoint expected, ClosurePage page)
    {
        using var connection = Connect();
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var context = Context(connection, transaction);
        var row = ReadCheckpoint(context, tracking: true);
        var current = new Checkpoint(row.StreamId, row.Position);

        if (current != expected)
        {
            // Only a fully applied nonempty page may succeed against a checkpoint moved by another attempt.
            if (current.StreamId != page.StreamId || page.Items.Count == 0 || page.NextAfter > current.Position ||
                !MatchesReceipts(context, page.Items))
                throw new IntakeFailure("apply", "checkpoint_conflict");
            transaction.Commit();
            return new ApplyResult(0, current);
        }

        if (current.StreamId is { } stream && stream != page.StreamId)
            throw new IntakeFailure("apply", "checkpoint_conflict");
        if (current.StreamId is null || page.Items.Count > 0)
        {
            row.StreamId = page.StreamId;
            row.Position = page.NextAfter;
            context.Receipts.AddRange(page.Items.Select(item => new Receipt
            {
                Position = item.Position,
                CaseId = item.CaseId,
                HistorySequence = item.HistorySequence,
                ClosedAtUtc = item.ClosedAtUtc
            }));
            Save(context, page.Items.Count + 1);
            // SQLite can ignore an insert with a trigger while EF counts the tracked entity.
            // Read the bounded page and checkpoint inside this transaction before reporting success.
            var stored = context.Checkpoints.AsNoTracking().SingleOrDefault(checkpoint => checkpoint.Slot == 1);
            if (stored?.StreamId != page.StreamId || stored.Position != page.NextAfter ||
                (page.Items.Count > 0 && !MatchesReceipts(context, page.Items)))
                throw new InvalidDataException("Incomplete receipt store write.");
        }

        transaction.Commit();
        return new ApplyResult(page.Items.Count, new Checkpoint(row.StreamId, row.Position));
    }

    private static bool MatchesReceipts(ReceiptDbContext context, IReadOnlyList<ClosureNotice> items)
    {
        var positions = items.Select(item => item.Position).ToArray();
        var rows = context.Receipts.AsNoTracking().Where(row => positions.Contains(row.Position)).OrderBy(row => row.Position).ToArray();
        return rows.Length == items.Count && rows.Select(row => new ClosureNotice(row.Position, row.CaseId, row.HistorySequence, row.ClosedAtUtc))
            .SequenceEqual(items);
    }

    private static ReceiptCheckpoint ReadCheckpoint(ReceiptDbContext context, bool tracking)
    {
        if (!context.Database.GetAppliedMigrations().SequenceEqual([InitialMigration], StringComparer.Ordinal))
            throw new InvalidDataException("Unrecognized receipt store migration.");

        var query = tracking ? context.Checkpoints : context.Checkpoints.AsNoTracking();
        if (query.Take(2).ToArray() is not [{ Slot: 1 } row] ||
            (row.StreamId is null ? row.Position != 0 : !ClosureFeed.IsCanonicalId(row.StreamId)) || row.Position < 0 ||
            row.Position != context.Receipts.OrderByDescending(receipt => receipt.Position).Select(receipt => receipt.Position).FirstOrDefault())
            throw new InvalidDataException("Invalid receipt checkpoint.");

        return row;
    }

    private SqliteConnection Connect() => new(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false,
        DefaultTimeout = 5
    }.ToString());

    private static ReceiptDbContext Context(SqliteConnection connection, SqliteTransaction transaction, bool migrating = false)
    {
        var options = new DbContextOptionsBuilder<ReceiptDbContext>().UseSqlite(connection, sqlite => sqlite.CommandTimeout(5));
        // Initialization owns the transaction for both the scaffolded schema and its first checkpoint.
        if (migrating) options.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.MigrationsUserTransactionWarning));
        var context = new ReceiptDbContext(options.Options);
        context.Database.UseTransaction(transaction);
        return context;
    }

    private static void Save(ReceiptDbContext context, int expected)
    {
        if (context.SaveChanges() != expected) throw new InvalidDataException("Incomplete receipt store write.");
    }
}

internal sealed record ReceiptInspection(Checkpoint Checkpoint, long Count);
internal sealed record ApplyResult(int Count, Checkpoint Checkpoint);
