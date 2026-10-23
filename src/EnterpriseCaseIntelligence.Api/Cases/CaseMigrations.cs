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
        var initial = assembly.Migrations.Single();
        return (initial.Key, assembly.CreateMigration(initial.Value, context.Database.ProviderName!));
    }

    internal static Dictionary<string, string> Schemas(CaseDbContext context)
    {
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var operation in Initial(context).Migration.UpOperations)
        {
            if (operation is not CreateTableOperation table) throw new InvalidDataException("Unsupported initial migration operation.");
            var command = generator.Generate([table], context.Model).Single();
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
            foreach (var command in generator.Generate([table], context.Model))
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

    internal static void ValidateMetadata(CaseDbContext context)
    {
        var rows = context.MigrationHistory.AsNoTracking().Take(2).ToArray();
        if (rows.Length != 1 || rows[0].MigrationId != Initial(context).Id || !CompatibleProductVersion(context, rows[0].ProductVersion))
            throw new InvalidDataException("Unsupported EF migration history.");
        if (context.Database.HasPendingModelChanges()) throw new InvalidDataException("EF model requires a checked-in migration.");
    }

    private static bool CompatibleProductVersion(CaseDbContext context, string value)
    {
        var first = Version.Parse(Initial(context).Migration.TargetModel.GetProductVersion()!);
        var running = Version.Parse(EfVersion);
        return Version.TryParse(value, out var version) && version.ToString() == value && version.Revision == -1 &&
            version.Major == first.Major && version.Minor == first.Minor && version >= first && version <= running;
    }

    private static string SchemaText(string sql) => sql.Trim().TrimEnd(';');
}
