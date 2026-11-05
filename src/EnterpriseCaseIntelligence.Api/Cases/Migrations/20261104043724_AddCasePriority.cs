using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseCaseIntelligence.Api.Cases.Migrations
{
    /// <inheritdoc />
    public partial class AddCasePriority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "case_priorities",
                columns: table => new
                {
                    case_id = table.Column<string>(type: "TEXT", nullable: false),
                    priority = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_case_priorities", x => x.case_id);
                    table.CheckConstraint("CK_priorities_value", "priority IN ('Low', 'Normal', 'High')");
                    table.ForeignKey(
                        name: "FK_case_priorities_cases_case_id",
                        column: x => x.case_id,
                        principalTable: "cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "case_priorities");
        }
    }
}
