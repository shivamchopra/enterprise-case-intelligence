using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EnterpriseCaseIntelligence.Api.Cases;

internal static class CaseMigrations
{
    internal static string EfVersion => ProductInfo.GetVersion();

    private static (string Id, Migration Migration) Initial(CaseDbContext context)
    {
        var assembly = context.GetService<IMigrationsAssembly>();
        var entries = assembly.Migrations.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToArray();
        if (entries.Length != 2 || entries[0].Key != "20261022194811_InitialCaseStore" ||
            entries[1].Key != "20261104043724_AddCasePriority") throw new InvalidDataException("Unsupported migration chain.");
        return (entries[0].Key, assembly.CreateMigration(entries[0].Value, context.Database.ProviderName!));
    }

    private static (string Id, Migration Migration) Priority(CaseDbContext context)
    {
        _ = Initial(context);
        var assembly = context.GetService<IMigrationsAssembly>();
        var entry = assembly.Migrations.Single(entry => entry.Key == "20261104043724_AddCasePriority");
        return (entry.Key, assembly.CreateMigration(entry.Value, context.Database.ProviderName!));
    }

    internal static Dictionary<string, string> Schemas(CaseDbContext context, long version = 5)
    {
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var migration in version == 4 ? new[] { Initial(context).Migration } : new[] { Initial(context).Migration, Priority(context).Migration })
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
        foreach (var operation in Initial(context).Migration.UpOperations)
        {
            if (operation is not CreateTableOperation table) throw new InvalidDataException("Unsupported adoption migration operation.");
            if (table.Name == "cases" || (source >= 2 && table.Name == "case_status_history") ||
                (source >= 3 && table.Name == "case_creation_keys")) continue;
            foreach (var command in generator.Generate([table], Initial(context).Migration.TargetModel))
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
        context.Database.ExecuteSqlRaw(history.GetInsertScript(new HistoryRow(Initial(context).Id, EfVersion)));
    }

    internal static void AddPriorityTable(CaseDbContext context)
    {
        var migration = Priority(context).Migration;
        foreach (var command in context.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, migration.TargetModel))
        {
            if (command.TransactionSuppressed) throw new InvalidDataException("Priority migration must be transactional.");
            // Only provider-generated migration DDL, in the caller's offline transaction.
            context.Database.ExecuteSqlRaw(command.CommandText);
        }
    }

    internal static void RecordPriority(CaseDbContext context)
        => context.Database.ExecuteSqlRaw(context.GetService<IHistoryRepository>()
            .GetInsertScript(new HistoryRow(Priority(context).Id, EfVersion)));

    internal static void ValidateMetadata(CaseDbContext context, long version)
    {
        var migrations = version == 4 ? new[] { Initial(context) } : new[] { Initial(context), Priority(context) };
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
