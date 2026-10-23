using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseCaseIntelligence.Api.Cases;

public sealed class CaseDatabase
{
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
        using var context = Context(connection, transaction, migrating: true);
        context.Database.Migrate();
        Execute(connection, "PRAGMA user_version=4", transaction);
        if (ValidateFormat(connection, transaction) != 4) throw new InvalidDataException("Invalid initialized store.");
        transaction.Commit();
    }

    public void ValidateStartup()
    {
        using var connection = Open();
        using (var validation = connection.BeginTransaction(deferred: true))
        {
            if (ValidateFormat(connection, validation) != 4) throw new InvalidDataException("Upgrade the older store explicitly.");
            validation.Commit();
        }

        // Exercise journal creation, then roll back without changing committed metadata.
        using var transaction = connection.BeginTransaction(deferred: false);
        Execute(connection, "PRAGMA user_version=4", transaction);
        transaction.Rollback();
    }

    public long Inspect()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var version = ValidateFormat(connection, transaction);
        using var context = Context(connection, transaction);
        ValidateCases(context, version >= 2);
        if (version >= 3) CaseCreationKeys.Inspect(context);
        transaction.Commit();
        return version;
    }

    public void Upgrade()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var version = ValidateFormat(connection, transaction);
        if (version is not (1 or 2 or 3)) throw new InvalidDataException("Only older stores can be upgraded.");
        using var context = Context(connection, transaction);
        ValidateCases(context, history: version >= 2);
        if (version == 3) CaseCreationKeys.Inspect(context);
        CaseMigrations.CreateMissingTables(context, version);
        if (version == 1)
        {
            string? after = null;
            while (true)
            {
                var batch = ReadBatch(context, after);
                if (batch.Length == 0) break;
                foreach (var record in batch)
                {
                    CaseHistory.Insert(context, record.Id, CaseHistory.Record(1, "Baseline", null, record.Status, _clock));
                    context.ChangeTracker.Clear();
                }
                after = batch[^1].Id.ToString("D");
            }
        }
        // Validate every modeled value before assigning EF ownership to retained tables.
        ValidateCases(context, history: true);
        CaseCreationKeys.Inspect(context);
        CaseMigrations.RecordValidatedBaseline(context);
        Execute(connection, "PRAGMA user_version=4", transaction);
        if (ValidateFormat(connection, transaction) != 4) throw new InvalidDataException("Invalid upgraded store.");
        transaction.Commit();
    }

    private long ValidateFormat(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (Scalar(connection, "PRAGMA user_version", transaction) is not long version || version is not (1 or 2 or 3 or 4))
            throw new InvalidDataException("Unsupported case store version.");
        using var context = Context(connection, transaction);
        var legacy = new Dictionary<string, string>(StringComparer.Ordinal) { ["cases"] = LegacyCaseSchema.Cases };
        if (version >= 2) legacy.Add("case_status_history", LegacyCaseSchema.History);
        if (version >= 3) legacy.Add("case_creation_keys", LegacyCaseSchema.CreationKeys);
        var expected = version == 4 ? CaseMigrations.Schemas(context) : legacy;
        using (var command = connection.CreateCommand())
        {
            // SQLite's schema catalog/integrity PRAGMAs have no EF model API. This diagnostic
            // SQL validates both the exact frozen source and exact EF-emitted table definitions.
            command.Transaction = transaction;
            command.CommandText = "SELECT type, name, sql FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*'";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(1);
                if (reader.GetString(0) != "table" || !expected.Remove(name, out var sql) ||
                    (reader.GetString(2) != sql && !(version == 4 && legacy.TryGetValue(name, out var old) && reader.GetString(2) == old)))
                    throw new InvalidDataException("Unsupported case store schema.");
            }
            if (expected.Count != 0) throw new InvalidDataException("Unsupported case store schema.");
        }
        if (version == 4) CaseMigrations.ValidateMetadata(context);

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

    private static void ValidateCases(CaseDbContext context, bool history)
    {
        string? after = null;
        while (true)
        {
            var batch = ReadBatch(context, after);
            if (batch.Length == 0) return;
            if (history) foreach (var record in batch) CaseHistory.Read(context, record);
            after = batch[^1].Id.ToString("D");
        }
    }

    private static Domain.Cases.Case[] ReadBatch(CaseDbContext context, string? after)
        => context.Cases.AsNoTracking().Where(row => after == null || string.Compare(EF.Functions.Collate(row.Id, "BINARY"), after) > 0)
            .OrderBy(row => EF.Functions.Collate(row.Id, "BINARY")).Take(100).ToArray().Select(SqliteCaseStore.Restore).ToArray();

    internal CaseDbContext Context(SqliteConnection connection, SqliteTransaction? transaction = null, bool migrating = false)
    {
        var context = new CaseDbContext(CaseDbContext.Options(connection, migrating));
        try
        {
            if (transaction is not null) context.Database.UseTransaction(transaction);
            return context;
        }
        catch { context.Dispose(); throw; }
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
