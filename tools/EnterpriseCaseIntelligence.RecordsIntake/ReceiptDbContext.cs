using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EnterpriseCaseIntelligence.RecordsIntake;

internal sealed class ReceiptDbContext(DbContextOptions<ReceiptDbContext> options) : DbContext(options)
{
    internal DbSet<ReceiptCheckpoint> Checkpoints => Set<ReceiptCheckpoint>();
    internal DbSet<Receipt> Receipts => Set<Receipt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReceiptCheckpoint>(entity =>
        {
            entity.ToTable("receipt_checkpoint", table =>
            {
                table.HasCheckConstraint("CK_receipt_checkpoint_singleton", "slot = 1");
                table.HasCheckConstraint("CK_receipt_checkpoint_position", "position >= 0 AND (stream_id IS NOT NULL OR position = 0)");
            });
            entity.HasKey(row => row.Slot);
            entity.Property(row => row.Slot).HasColumnName("slot").ValueGeneratedNever();
            entity.Property(row => row.StreamId).HasColumnName("stream_id");
            entity.Property(row => row.Position).HasColumnName("position");
        });

        modelBuilder.Entity<Receipt>(entity =>
        {
            entity.ToTable("closure_receipts", table =>
            {
                table.HasCheckConstraint("CK_receipt_position", "position > 0");
                table.HasCheckConstraint("CK_receipt_history_sequence", "history_sequence BETWEEN 2 AND 3");
            });
            entity.HasKey(row => row.Position);
            entity.Property(row => row.Position).HasColumnName("position").ValueGeneratedNever();
            entity.Property(row => row.CaseId).HasColumnName("case_id");
            entity.Property(row => row.HistorySequence).HasColumnName("history_sequence");
            entity.Property(row => row.ClosedAtUtc).HasColumnName("closed_at_utc");
        });
    }
}

internal sealed class ReceiptCheckpoint
{
    public int Slot { get; init; }
    public string? StreamId { get; set; }
    public long Position { get; set; }
}

internal sealed class Receipt
{
    public long Position { get; init; }
    public required string CaseId { get; init; }
    public int HistorySequence { get; init; }
    public required string ClosedAtUtc { get; init; }
}

internal sealed class ReceiptDbContextFactory : IDesignTimeDbContextFactory<ReceiptDbContext>
{
    public ReceiptDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<ReceiptDbContext>().UseSqlite("Data Source=:memory:").Options);
}
