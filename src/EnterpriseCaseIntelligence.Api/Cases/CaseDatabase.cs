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

    public CaseDatabase(string? databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !Path.IsPathFullyQualified(databasePath) ||
            string.IsNullOrEmpty(Path.GetFileName(databasePath)))
        {
            throw new ArgumentException("CaseStorage:DatabasePath must be an absolute local file path.");
        }

        _path = Path.GetFullPath(databasePath);
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
        Execute(connection, "PRAGMA user_version=1", transaction);
        transaction.Commit();
    }

    public void ValidateStartup()
    {
        using var connection = Open();
        if (Scalar(connection, "PRAGMA user_version") is not long version || version != 1)
        {
            throw new InvalidDataException("Unsupported case store version.");
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT type, name, sql FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*'";
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetString(0) != "table" || reader.GetString(1) != "cases" ||
                reader.GetString(2) != Schema || reader.Read())
            {
                throw new InvalidDataException("Unsupported case store schema.");
            }
        }

        if (!Equals(Scalar(connection, "PRAGMA integrity_check"), "ok"))
        {
            throw new InvalidDataException("Case store integrity check failed.");
        }

        // Exercise journal creation, then roll back without changing committed metadata.
        using var transaction = connection.BeginTransaction(deferred: false);
        Execute(connection, "PRAGMA user_version=1", transaction);
        transaction.Rollback();
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

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
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
