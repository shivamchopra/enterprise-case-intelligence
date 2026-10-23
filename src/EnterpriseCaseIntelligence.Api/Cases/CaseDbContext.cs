using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EnterpriseCaseIntelligence.Api.Cases;

public sealed class CaseDbContext(DbContextOptions<CaseDbContext> options) : DbContext(options)
{
    public DbSet<StoredCase> Cases => Set<StoredCase>();
    public DbSet<StoredHistory> History => Set<StoredHistory>();
    public DbSet<StoredCreationKey> CreationKeys => Set<StoredCreationKey>();
    public DbSet<StoredMigration> MigrationHistory => Set<StoredMigration>();

    internal static DbContextOptions<CaseDbContext> Options(SqliteConnection connection, bool migrating = false)
    {
        var builder = Configure(new DbContextOptionsBuilder<CaseDbContext>().UseSqlite(connection, sqlite => sqlite.CommandTimeout(5)));
        // Offline commands already own a non-deferred transaction and require all store users stopped.
        // Keep EF's migration metadata inside that transaction; no independent persistent lock is acquired.
        if (migrating) builder.ConfigureWarnings(w => w.Ignore(RelationalEventId.MigrationsUserTransactionWarning));
        return builder.Options;
    }

    internal static DbContextOptionsBuilder<CaseDbContext> Configure(DbContextOptionsBuilder<CaseDbContext> builder)
        => builder.ReplaceService<IMigrationsSqlGenerator, StrictSqliteMigrationsSqlGenerator>();

    internal void SaveSingleChange()
    {
        // EF's entity count does not detect a SQLite INSERT ignored by a trigger when no
        // generated value is returned. changes() reports the actual outer statement count.
        // Each store stage saves exactly one entity; never batch writes through this helper.
        if (ChangeTracker.Entries().Count(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted) != 1 ||
            SaveChanges() != 1 || Database.SqlQueryRaw<long>("SELECT changes() AS Value").Single() != 1)
            throw new InvalidDataException("A store write did not affect exactly one record.");
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        var cases = model.Entity<StoredCase>();
        cases.ToTable("cases", table => table.HasCheckConstraint("CK_cases_status", "status IN ('New', 'InReview', 'Closed')"));
        cases.HasKey(row => row.Id);
        cases.Property(row => row.Id).HasColumnName("id").HasColumnOrder(0).ValueGeneratedNever();
        cases.Property(row => row.Title).HasColumnName("title").HasColumnOrder(1).IsRequired();
        cases.Property(row => row.Description).HasColumnName("description").HasColumnOrder(2).IsRequired();
        cases.Property(row => row.Status).HasColumnName("status").HasColumnOrder(3).IsRequired();

        var history = model.Entity<StoredHistory>();
        history.ToTable("case_status_history", table =>
        {
            table.HasCheckConstraint("CK_history_sequence", "sequence BETWEEN 1 AND 3");
            table.HasCheckConstraint("CK_history_kind", "kind IN ('Created', 'Baseline', 'StatusChanged')");
            table.HasCheckConstraint("CK_history_from", "from_status IS NULL OR from_status IN ('New', 'InReview', 'Closed')");
            table.HasCheckConstraint("CK_history_to", "to_status IN ('New', 'InReview', 'Closed')");
            table.HasCheckConstraint("CK_history_transition", """
                (sequence = 1 AND kind = 'Created' AND from_status IS NULL AND to_status = 'New')
                OR (sequence = 1 AND kind = 'Baseline' AND from_status IS NULL)
                OR (sequence BETWEEN 2 AND 3 AND kind = 'StatusChanged' AND from_status IS NOT NULL
                    AND ((from_status = 'New' AND to_status = 'InReview')
                        OR (from_status = 'InReview' AND to_status = 'Closed')))
                """);
        });
        history.HasKey(row => new { row.CaseId, row.Sequence });
        history.HasOne<StoredCase>().WithMany().HasForeignKey(row => row.CaseId).OnDelete(DeleteBehavior.Restrict);
        history.Property(row => row.CaseId).HasColumnName("case_id").HasColumnOrder(0);
        history.Property(row => row.Sequence).HasColumnName("sequence").HasColumnOrder(1).ValueGeneratedNever();
        history.Property(row => row.Kind).HasColumnName("kind").HasColumnOrder(2).IsRequired();
        history.Property(row => row.FromStatus).HasColumnName("from_status").HasColumnOrder(3);
        history.Property(row => row.ToStatus).HasColumnName("to_status").HasColumnOrder(4).IsRequired();
        history.Property(row => row.RecordedAtUtc).HasColumnName("recorded_at_utc").HasColumnOrder(5).IsRequired();

        var keys = model.Entity<StoredCreationKey>();
        keys.ToTable("case_creation_keys", table => table.HasCheckConstraint("CK_creation_key_uuid", """
            length(request_key) = 36
            AND instr(request_key, char(0)) = 0
            AND substr(request_key, 9, 1) = '-'
            AND substr(request_key, 14, 1) = '-'
            AND substr(request_key, 19, 1) = '-'
            AND substr(request_key, 24, 1) = '-'
            AND length(replace(request_key, '-', '')) = 32
            AND replace(request_key, '-', '') NOT GLOB '*[^0-9a-f]*'
            AND request_key <> '00000000-0000-0000-0000-000000000000'
            """));
        keys.HasKey(row => row.RequestKey);
        keys.HasAlternateKey(row => row.CaseId);
        keys.HasOne<StoredCase>().WithMany().HasForeignKey(row => row.CaseId).OnDelete(DeleteBehavior.Restrict);
        keys.Property(row => row.RequestKey).HasColumnName("request_key").HasColumnOrder(0).ValueGeneratedNever();
        keys.Property(row => row.CaseId).HasColumnName("case_id").HasColumnOrder(1);

        // Read EF-owned metadata with bounded LINQ; its DDL remains owned by IHistoryRepository.
        var migrations = model.Entity<StoredMigration>();
        migrations.ToTable("__EFMigrationsHistory", table => table.ExcludeFromMigrations());
        migrations.HasKey(row => row.MigrationId);
        migrations.Property(row => row.MigrationId).HasMaxLength(150).ValueGeneratedNever();
        migrations.Property(row => row.ProductVersion).HasMaxLength(32).IsRequired();
    }
}

public sealed class StoredCase
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "";
}

public sealed class StoredHistory
{
    public string CaseId { get; set; } = "";
    public long Sequence { get; set; }
    public string Kind { get; set; } = "";
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = "";
    public string RecordedAtUtc { get; set; } = "";
}

public sealed class StoredCreationKey
{
    public string RequestKey { get; set; } = "";
    public string CaseId { get; set; } = "";
}

public sealed class StoredMigration
{
    public string MigrationId { get; set; } = "";
    public string ProductVersion { get; set; } = "";
}

public sealed class CaseDbContextFactory : IDesignTimeDbContextFactory<CaseDbContext>
{
    public CaseDbContext CreateDbContext(string[] args)
        => new(CaseDbContext.Configure(new DbContextOptionsBuilder<CaseDbContext>()
            .UseSqlite("Data Source=:memory:")).Options);
}

// EF's SQLite provider has no STRICT-table model switch. Extend only its CREATE TABLE suffix;
// columns, keys and constraints still come from scaffolded EF migration operations (ADR 0007).
public sealed class StrictSqliteMigrationsSqlGenerator(
    MigrationsSqlGeneratorDependencies dependencies, IRelationalAnnotationProvider annotations)
    : SqliteMigrationsSqlGenerator(dependencies, annotations)
{
    protected override void Generate(CreateTableOperation operation, IModel? model, MigrationCommandListBuilder builder, bool terminate = true)
    {
        base.Generate(operation, model, builder, terminate: false);
        if (!operation.Name.StartsWith("__EF", StringComparison.Ordinal)) builder.Append(" STRICT");
        if (terminate) builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator).EndCommand();
    }
}
