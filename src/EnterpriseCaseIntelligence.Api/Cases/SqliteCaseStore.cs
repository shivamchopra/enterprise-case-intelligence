using EnterpriseCaseIntelligence.Domain.Cases;
using Microsoft.Data.Sqlite;

namespace EnterpriseCaseIntelligence.Api.Cases;

public sealed class SqliteCaseStore(CaseDatabase database, TimeProvider? timeProvider = null)
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public CaseSnapshot Create(string title, string description)
    {
        lock (_gate)
        {
            var record = new Case(title, description);
            using var connection = database.Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO cases (id, title, description, status) VALUES ($id, $title, $description, $status)";
            command.Parameters.AddWithValue("$id", record.Id.ToString("D"));
            command.Parameters.AddWithValue("$title", record.Title);
            command.Parameters.AddWithValue("$description", record.Description);
            command.Parameters.AddWithValue("$status", record.Status.ToString());
            if (command.ExecuteNonQuery() != 1)
            {
                throw new InvalidDataException("Case insert did not affect exactly one record.");
            }

            CaseHistory.Insert(connection, transaction, record.Id, CaseHistory.Record(1, "Created", null, record.Status, _clock));
            transaction.Commit();
            return Snapshot(record);
        }
    }

    public CaseSnapshot? Find(Guid id)
    {
        lock (_gate)
        {
            using var connection = database.Open();
            var record = Read(connection, id);
            return record is null ? null : Snapshot(record);
        }
    }

    public CasePage List(int limit, CaseStatus? status = null, Guid? after = null)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (status is { } requested && !Enum.IsDefined(requested))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (after == Guid.Empty)
        {
            throw new ArgumentException("Use a non-empty identifier boundary.", nameof(after));
        }

        lock (_gate)
        {
            using var connection = database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, title, description, status FROM cases
                WHERE ($status IS NULL OR status=$status)
                    AND ($after IS NULL OR id COLLATE BINARY > $after)
                ORDER BY id COLLATE BINARY
                LIMIT $take
                """;
            command.Parameters.AddWithValue("$status", (object?)status?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$after", (object?)after?.ToString("D") ?? DBNull.Value);
            command.Parameters.AddWithValue("$take", limit + 1);
            using var reader = command.ExecuteReader();
            var items = new List<CaseSnapshot>(limit + 1);
            while (reader.Read())
            {
                // Validate the lookahead too; malformed stored rows cannot become partial success.
                items.Add(Snapshot(Restore(reader)));
            }

            Guid? nextAfter = null;
            if (items.Count > limit)
            {
                items.RemoveAt(items.Count - 1);
                nextAfter = items[^1].Id;
            }

            return new(items.AsReadOnly(), nextAfter);
        }
    }

    public IReadOnlyList<CaseHistoryEntry>? History(Guid id)
    {
        lock (_gate)
        {
            using var connection = database.Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            var record = Read(connection, id, transaction);
            if (record is null) return null;
            var entries = CaseHistory.Read(connection, transaction, record);
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
            var record = Read(connection, id, transaction);
            if (record is null)
            {
                return new(null, false);
            }

            var history = CaseHistory.Read(connection, transaction, record);
            var previous = record.Status;
            try
            {
                record.ChangeStatus(status);
            }
            catch (InvalidOperationException)
            {
                return new(null, true);
            }

            if (record.Status != previous)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE cases SET status=$status WHERE id=$id";
                command.Parameters.AddWithValue("$status", record.Status.ToString());
                command.Parameters.AddWithValue("$id", id.ToString("D"));
                if (command.ExecuteNonQuery() != 1)
                {
                    throw new InvalidDataException("Case update did not affect exactly one record.");
                }

                CaseHistory.Insert(connection, transaction, id,
                    CaseHistory.Record(history.Count + 1, "StatusChanged", previous, record.Status, _clock));
            }

            transaction.Commit();
            return new(Snapshot(record), false);
        }
    }

    private static Case? Read(SqliteConnection connection, Guid id, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, title, description, status FROM cases WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Restore(reader) : null;
    }

    internal static Case Restore(SqliteDataReader reader)
    {
        try
        {
            var rawId = reader.GetString(0);
            var status = reader.GetString(3) switch
            {
                "New" => CaseStatus.New,
                "InReview" => CaseStatus.InReview,
                "Closed" => CaseStatus.Closed,
                _ => throw new InvalidDataException("Invalid stored status.")
            };
            if (!Guid.TryParseExact(rawId, "D", out var storedId) || rawId != storedId.ToString("D"))
            {
                throw new InvalidDataException("Invalid stored identifier.");
            }

            return Case.Restore(storedId, reader.GetString(1), reader.GetString(2), status);
        }
        catch (ArgumentException)
        {
            throw new InvalidDataException("Invalid stored case values.");
        }
    }

    private static CaseSnapshot Snapshot(Case record) => new(record.Id, record.Title, record.Description, record.Status);
}

public sealed record CaseStatusChange(CaseSnapshot? Case, bool IsConflict);
