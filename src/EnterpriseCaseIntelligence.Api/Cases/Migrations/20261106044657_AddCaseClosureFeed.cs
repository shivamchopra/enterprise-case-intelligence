using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseCaseIntelligence.Api.Cases.Migrations
{
    /// <inheritdoc />
    public partial class AddCaseClosureFeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "case_closure_notices",
                columns: table => new
                {
                    position = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    case_id = table.Column<string>(type: "TEXT", nullable: false),
                    history_sequence = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_case_closure_notices", x => x.position);
                    table.UniqueConstraint("AK_case_closure_notices_case_id_history_sequence", x => new { x.case_id, x.history_sequence });
                    table.CheckConstraint("CK_closure_position", "position > 0");
                    table.ForeignKey(
                        name: "FK_case_closure_notices_case_status_history_case_id_history_sequence",
                        columns: x => new { x.case_id, x.history_sequence },
                        principalTable: "case_status_history",
                        principalColumns: new[] { "case_id", "sequence" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "case_closure_stream",
                columns: table => new
                {
                    slot = table.Column<int>(type: "INTEGER", nullable: false),
                    stream_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_case_closure_stream", x => x.slot);
                    table.CheckConstraint("CK_closure_stream_slot", "slot = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "case_closure_notices");

            migrationBuilder.DropTable(
                name: "case_closure_stream");
        }
    }
}
