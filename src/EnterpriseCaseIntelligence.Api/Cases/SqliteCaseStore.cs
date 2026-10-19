using EnterpriseCaseIntelligence.Domain.Cases;
using Microsoft.Data.Sqlite;

namespace EnterpriseCaseIntelligence.Api.Cases;

public sealed class SqliteCaseStore(CaseDatabase database)
{
    private readonly Lock _gate = new();

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
        if (!reader.Read())
        {
            return null;
        }

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
