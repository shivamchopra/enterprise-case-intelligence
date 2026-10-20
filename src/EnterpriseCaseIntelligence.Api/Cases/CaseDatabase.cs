using Microsoft.Data.Sqlite;

namespace EnterpriseCaseIntelligence.Api.Cases;

public sealed class CaseDatabase
{
    private const string Schema = """
        CREATE TABLE cases (
            id TEXT NOT NULL PRIMARY KEY,
            title TEXT NOT NULL,
            description TEXT NOT NULL,
            status TEXT NOT NULL CHECK (status IN ('New', 'InReview', 'Closed'))
        ) STRICT
        """;

    private readonly string _connectionString;
    private readonly string _path;
    private readonly TimeProvider _clock;

    public CaseDatabase(string? databasePath, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !Path.IsPathFullyQualified(databasePath) ||
            string.IsNullOrEmpty(Path.GetFileName(databasePath)))
        {
            throw new ArgumentException("CaseStorage:DatabasePath must be an absolute local file path.");
        }

        _path = Path.GetFullPath(databasePath);
        _clock = timeProvider ?? TimeProvider.System;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
    }

    public void Initialize()
    {
        // Reserve exclusively: even an empty existing file must never be overwritten.
        using (new FileStream(_path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) { }
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        Execute(connection, Schema, transaction);
        Execute(connection, CaseHistory.Schema, transaction);
        Execute(connection, "PRAGMA user_version=2", transaction);
        transaction.Commit();
    }

    public void ValidateStartup()
    {
        using var connection = Open();
        using (var validation = connection.BeginTransaction(deferred: true))
        {
            if (ValidateFormat(connection, validation) != 2) throw new InvalidDataException("Upgrade the version-1 store explicitly.");
            validation.Commit();
        }

        // Exercise journal creation, then roll back without changing committed metadata.
        using var transaction = connection.BeginTransaction(deferred: false);
        Execute(connection, "PRAGMA user_version=2", transaction);
        transaction.Rollback();
    }

    public long Inspect()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var version = ValidateFormat(connection, transaction);
        ValidateCases(connection, transaction, version == 2);
        transaction.Commit();
        return version;
    }

    public void Upgrade()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (ValidateFormat(connection, transaction) != 1) throw new InvalidDataException("Only version-1 stores can be upgraded.");
        ValidateCases(connection, transaction, history: false);
        Execute(connection, CaseHistory.Schema, transaction);
        Guid? after = null;
        while (true)
        {
            var batch = ReadBatch(connection, transaction, after);
            if (batch.Count == 0) break;
            foreach (var record in batch)
            {
                CaseHistory.Insert(connection, transaction, record.Id,
                    CaseHistory.Record(1, "Baseline", null, record.Status, _clock));
            }

            after = batch[^1].Id;
        }

        Execute(connection, "PRAGMA user_version=2", transaction);
        if (ValidateFormat(connection, transaction) != 2) throw new InvalidDataException("Invalid upgraded store.");
        ValidateCases(connection, transaction, history: true);
        transaction.Commit();
    }

    private static long ValidateFormat(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (Scalar(connection, "PRAGMA user_version", transaction) is not long version || version is not (1 or 2))
            throw new InvalidDataException("Unsupported case store version.");
        var expected = new Dictionary<string, string>(StringComparer.Ordinal) { ["cases"] = Schema };
        if (version == 2) expected.Add("case_status_history", CaseHistory.Schema);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT type, name, sql FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*'";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(0) != "table" || !expected.Remove(reader.GetString(1), out var sql) || reader.GetString(2) != sql)
                    throw new InvalidDataException("Unsupported case store schema.");
            }

            if (expected.Count != 0) throw new InvalidDataException("Unsupported case store schema.");
        }

        if (!Equals(Scalar(connection, "PRAGMA integrity_check", transaction), "ok"))
        {
            throw new InvalidDataException("Case store integrity check failed.");
        }

        using var foreignKeys = connection.CreateCommand();
        foreignKeys.Transaction = transaction;
        foreignKeys.CommandText = "PRAGMA foreign_key_check";
        using var violations = foreignKeys.ExecuteReader();
        if (violations.Read()) throw new InvalidDataException("Case store foreign-key check failed.");
        return version;
    }

    private static void ValidateCases(SqliteConnection connection, SqliteTransaction transaction, bool history)
    {
        Guid? after = null;
        while (true)
        {
            var batch = ReadBatch(connection, transaction, after);
            if (batch.Count == 0) return;
            if (history)
            {
                foreach (var record in batch) CaseHistory.Read(connection, transaction, record);
            }

            after = batch[^1].Id;
        }
    }

    private static List<Domain.Cases.Case> ReadBatch(SqliteConnection connection, SqliteTransaction transaction, Guid? after)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, title, description, status FROM cases
            WHERE $after IS NULL OR id COLLATE BINARY > $after ORDER BY id COLLATE BINARY LIMIT 100
            """;
        command.Parameters.AddWithValue("$after", (object?)after?.ToString("D") ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var batch = new List<Domain.Cases.Case>(100);
        while (reader.Read()) batch.Add(SqliteCaseStore.Restore(reader));
        return batch;
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            if (!Equals(Scalar(connection, "PRAGMA journal_mode"), "delete"))
            {
                throw new InvalidDataException("Unsupported case store journal mode.");
            }

            Execute(connection, "PRAGMA synchronous=FULL");
            Execute(connection, "PRAGMA foreign_keys=ON");
            if (!Equals(Scalar(connection, "PRAGMA foreign_keys"), 1L))
                throw new InvalidDataException("Foreign-key enforcement unavailable.");
            if (!Equals(Scalar(connection, "PRAGMA synchronous"), 2L))
            {
                throw new InvalidDataException("Unsupported case store synchronization mode.");
            }

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static object? Scalar(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
