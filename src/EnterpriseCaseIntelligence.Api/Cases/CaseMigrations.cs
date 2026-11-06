using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EnterpriseCaseIntelligence.Api.Cases;

internal static class CaseMigrations
{
    internal static string EfVersion => ProductInfo.GetVersion();

    private static readonly string[] KnownMigrations =
    [
        "20261022194811_InitialCaseStore",
        "20261104043724_AddCasePriority",
        "20261106044657_AddCaseClosureFeed"
    ];

    private static (string Id, Migration Migration)[] Chain(CaseDbContext context)
    {
        var assembly = context.GetService<IMigrationsAssembly>();
        var entries = assembly.Migrations.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToArray();
        if (!entries.Select(entry => entry.Key).SequenceEqual(KnownMigrations, StringComparer.Ordinal))
            throw new InvalidDataException("Unsupported migration chain.");
        return entries.Select(entry => (entry.Key, assembly.CreateMigration(entry.Value, context.Database.ProviderName!))).ToArray();
    }

    internal static Dictionary<string, string> Schemas(CaseDbContext context, long version)
    {
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, migration) in Chain(context).Take((int)version - 3))
            foreach (var operation in migration.UpOperations)
            {
                if (operation is not CreateTableOperation table) throw new InvalidDataException("Unsupported migration operation.");
                var command = generator.Generate([table], migration.TargetModel).Single();
                if (command.TransactionSuppressed) throw new InvalidDataException("Nontransactional schema operation.");
                expected.Add(table.Name, SchemaText(command.CommandText));
            }
        expected.Add("__EFMigrationsHistory", SchemaText(context.GetService<IHistoryRepository>().GetCreateScript()));
        return expected;
    }

    internal static void CreateMissingTables(CaseDbContext context, long source)
    {
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var initial = Chain(context)[0].Migration;
        foreach (var operation in initial.UpOperations)
        {
            if (operation is not CreateTableOperation table) throw new InvalidDataException("Unsupported adoption migration operation.");
            if (table.Name == "cases" || (source >= 2 && table.Name == "case_status_history") ||
                (source >= 3 && table.Name == "case_creation_keys")) continue;
            foreach (var command in generator.Generate([table], initial.TargetModel))
            {
                if (command.TransactionSuppressed) throw new InvalidDataException("Adoption requires transactional migration operations.");
                // Provider-generated migration DDL, not a separately maintained schema script.
                context.Database.ExecuteSqlRaw(command.CommandText);
            }
        }
    }

    internal static void RecordValidatedBaseline(CaseDbContext context)
    {
        var history = context.GetService<IHistoryRepository>();
        // Legacy adoption only: the source schema/data and generated missing tables are validated
        // before recording the initial EF migration; all metadata is in the same caller transaction.
        context.Database.ExecuteSqlRaw(history.GetCreateScript());
        context.Database.ExecuteSqlRaw(history.GetInsertScript(new HistoryRow(KnownMigrations[0], EfVersion)));
    }

    internal static void AddPriorityTable(CaseDbContext context) => ApplyAddedTables(context, Chain(context)[1].Migration);

    internal static void AddClosureTables(CaseDbContext context) => ApplyAddedTables(context, Chain(context)[2].Migration);

    private static void ApplyAddedTables(CaseDbContext context, Migration migration)
    {
        if (migration.UpOperations.Any(operation => operation is not CreateTableOperation))
            throw new InvalidDataException("Unsupported additive migration operation.");
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, migration.TargetModel);
        if (commands.Any(command => command.TransactionSuppressed))
            throw new InvalidDataException("Additive migrations must be transactional.");
        // Execute only provider-generated DDL inside the validated adoption transaction.
        foreach (var command in commands)
            context.Database.ExecuteSqlRaw(command.CommandText);
    }

    internal static void RecordPriority(CaseDbContext context) => RecordMigration(context, KnownMigrations[1]);

    internal static void RecordClosures(CaseDbContext context) => RecordMigration(context, KnownMigrations[2]);

    private static void RecordMigration(CaseDbContext context, string migrationId)
        => context.Database.ExecuteSqlRaw(context.GetService<IHistoryRepository>()
            .GetInsertScript(new HistoryRow(migrationId, EfVersion)));

    internal static void ValidateMetadata(CaseDbContext context, long version)
    {
        var migrations = Chain(context).Take((int)version - 3).ToArray();
        var rows = context.MigrationHistory.AsNoTracking().OrderBy(row => row.MigrationId).Take(migrations.Length + 1).ToArray();
        if (rows.Length != migrations.Length) throw new InvalidDataException("Unsupported EF migration history.");
        for (var index = 0; index < rows.Length; index++)
            if (rows[index].MigrationId != migrations[index].Id || !CompatibleProductVersion(migrations[index].Migration, rows[index].ProductVersion))
                throw new InvalidDataException("Unsupported EF migration history.");
        if (context.Database.HasPendingModelChanges()) throw new InvalidDataException("EF model requires a checked-in migration.");
    }

    private static bool CompatibleProductVersion(Migration migration, string value)
    {
        var first = Version.Parse(migration.TargetModel.GetProductVersion()!);
        var running = Version.Parse(EfVersion);
        return Version.TryParse(value, out var version) && version.ToString() == value && version.Revision == -1 &&
            version.Major == first.Major && version.Minor == first.Minor && version >= first && version <= running;
    }

    private static string SchemaText(string sql) => sql.Trim().TrimEnd(';');
}
