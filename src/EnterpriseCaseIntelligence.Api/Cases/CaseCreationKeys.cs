using EnterpriseCaseIntelligence.Domain.Cases;
using Microsoft.Data.Sqlite;

namespace EnterpriseCaseIntelligence.Api.Cases;

internal static class CaseCreationKeys
{
    internal const string Schema = """
        CREATE TABLE case_creation_keys (
            request_key TEXT NOT NULL PRIMARY KEY,
            case_id TEXT NOT NULL UNIQUE,
            FOREIGN KEY (case_id) REFERENCES cases(id),
            CHECK (
                length(request_key) = 36
                AND instr(request_key, char(0)) = 0
                AND substr(request_key, 9, 1) = '-'
                AND substr(request_key, 14, 1) = '-'
                AND substr(request_key, 19, 1) = '-'
                AND substr(request_key, 24, 1) = '-'
                AND length(replace(request_key, '-', '')) = 32
                AND replace(request_key, '-', '') NOT GLOB '*[^0-9a-f]*'
                AND request_key <> '00000000-0000-0000-0000-000000000000'
            )
        ) STRICT
        """;

    internal static Case? Find(SqliteConnection connection, SqliteTransaction transaction, Guid key)
    {
        var rows = new List<(string Key, string CaseId)>(2);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT request_key, case_id FROM case_creation_keys WHERE request_key COLLATE BINARY=$key LIMIT 2";
            command.Parameters.AddWithValue("$key", key.ToString("D"));
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        if (rows.Count == 0) return null;
        if (rows.Count != 1) throw InvalidBinding();
        return Validate(connection, transaction, rows[0].Key, rows[0].CaseId);
    }

    internal static Case Validate(SqliteConnection connection, SqliteTransaction transaction, string key, string caseId)
    {
        CanonicalId(key);
        var record = SqliteCaseStore.Read(connection, CanonicalId(caseId), transaction) ?? throw InvalidBinding();
        if (CaseHistory.Read(connection, transaction, record)[0].Kind != "Created") throw InvalidBinding();
        return record;
    }

    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, Guid key, Guid caseId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO case_creation_keys (request_key, case_id) VALUES ($key, $id)";
        command.Parameters.AddWithValue("$key", key.ToString("D"));
        command.Parameters.AddWithValue("$id", caseId.ToString("D"));
        if (command.ExecuteNonQuery() != 1) throw InvalidBinding();
    }

    internal static void Inspect(SqliteConnection connection, SqliteTransaction transaction)
    {
        string? after = null;
        while (true)
        {
            var batch = new List<(string Key, string CaseId)>(100);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT request_key, case_id FROM case_creation_keys
                    WHERE $after IS NULL OR request_key COLLATE BINARY > $after
                    ORDER BY request_key COLLATE BINARY LIMIT 100
                    """;
                command.Parameters.AddWithValue("$after", (object?)after ?? DBNull.Value);
                using var reader = command.ExecuteReader();
                while (reader.Read()) batch.Add((reader.GetString(0), reader.GetString(1)));
            }

            if (batch.Count == 0) return;
            foreach (var row in batch) Validate(connection, transaction, row.Key, row.CaseId);
            after = batch[^1].Key;
        }
    }

    private static Guid CanonicalId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty || value != id.ToString("D")) throw InvalidBinding();
        return id;
    }

    private static InvalidDataException InvalidBinding() => new("Invalid stored creation binding.");
}
