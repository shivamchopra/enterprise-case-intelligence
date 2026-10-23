using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseCaseIntelligence.Api.Cases.Migrations
{
    /// <inheritdoc />
    public partial class InitialCaseStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cases",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cases", x => x.id);
                    table.CheckConstraint("CK_cases_status", "status IN ('New', 'InReview', 'Closed')");
                });

            migrationBuilder.CreateTable(
                name: "case_creation_keys",
                columns: table => new
                {
                    request_key = table.Column<string>(type: "TEXT", nullable: false),
                    case_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_case_creation_keys", x => x.request_key);
                    table.UniqueConstraint("AK_case_creation_keys_case_id", x => x.case_id);
                    table.CheckConstraint("CK_creation_key_uuid", "length(request_key) = 36\nAND instr(request_key, char(0)) = 0\nAND substr(request_key, 9, 1) = '-'\nAND substr(request_key, 14, 1) = '-'\nAND substr(request_key, 19, 1) = '-'\nAND substr(request_key, 24, 1) = '-'\nAND length(replace(request_key, '-', '')) = 32\nAND replace(request_key, '-', '') NOT GLOB '*[^0-9a-f]*'\nAND request_key <> '00000000-0000-0000-0000-000000000000'");
                    table.ForeignKey(
                        name: "FK_case_creation_keys_cases_case_id",
                        column: x => x.case_id,
                        principalTable: "cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "case_status_history",
                columns: table => new
                {
                    case_id = table.Column<string>(type: "TEXT", nullable: false),
                    sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    from_status = table.Column<string>(type: "TEXT", nullable: true),
                    to_status = table.Column<string>(type: "TEXT", nullable: false),
                    recorded_at_utc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_case_status_history", x => new { x.case_id, x.sequence });
                    table.CheckConstraint("CK_history_from", "from_status IS NULL OR from_status IN ('New', 'InReview', 'Closed')");
                    table.CheckConstraint("CK_history_kind", "kind IN ('Created', 'Baseline', 'StatusChanged')");
                    table.CheckConstraint("CK_history_sequence", "sequence BETWEEN 1 AND 3");
                    table.CheckConstraint("CK_history_to", "to_status IN ('New', 'InReview', 'Closed')");
                    table.CheckConstraint("CK_history_transition", "(sequence = 1 AND kind = 'Created' AND from_status IS NULL AND to_status = 'New')\nOR (sequence = 1 AND kind = 'Baseline' AND from_status IS NULL)\nOR (sequence BETWEEN 2 AND 3 AND kind = 'StatusChanged' AND from_status IS NOT NULL\n    AND ((from_status = 'New' AND to_status = 'InReview')\n        OR (from_status = 'InReview' AND to_status = 'Closed')))");
                    table.ForeignKey(
                        name: "FK_case_status_history_cases_case_id",
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
                name: "case_creation_keys");

            migrationBuilder.DropTable(
                name: "case_status_history");

            migrationBuilder.DropTable(
                name: "cases");
        }
    }
}
