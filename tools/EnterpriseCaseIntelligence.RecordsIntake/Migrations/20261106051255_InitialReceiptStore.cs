using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseCaseIntelligence.RecordsIntake.Migrations
{
    /// <inheritdoc />
    public partial class InitialReceiptStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "closure_receipts",
                columns: table => new
                {
                    position = table.Column<long>(type: "INTEGER", nullable: false),
                    case_id = table.Column<string>(type: "TEXT", nullable: false),
                    history_sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    closed_at_utc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_closure_receipts", x => x.position);
                    table.CheckConstraint("CK_receipt_history_sequence", "history_sequence BETWEEN 2 AND 3");
                    table.CheckConstraint("CK_receipt_position", "position > 0");
                });

            migrationBuilder.CreateTable(
                name: "receipt_checkpoint",
                columns: table => new
                {
                    slot = table.Column<int>(type: "INTEGER", nullable: false),
                    stream_id = table.Column<string>(type: "TEXT", nullable: true),
                    position = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_receipt_checkpoint", x => x.slot);
                    table.CheckConstraint("CK_receipt_checkpoint_position", "position >= 0 AND (stream_id IS NOT NULL OR position = 0)");
                    table.CheckConstraint("CK_receipt_checkpoint_singleton", "slot = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "closure_receipts");

            migrationBuilder.DropTable(
                name: "receipt_checkpoint");
        }
    }
}
