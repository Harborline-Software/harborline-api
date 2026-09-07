using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Roster.Migrations
{
    /// <inheritdoc />
    public partial class _20260620201710_RosterInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "roster_records",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    kind = table.Column<int>(type: "INTEGER", nullable: false),
                    team_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    party_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    public_key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    permissions = table.Column<string>(type: "TEXT", nullable: false),
                    admitted_by_key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    admitted_by_party = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    nonce = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    signature = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    is_genesis = table.Column<bool>(type: "INTEGER", nullable: false),
                    issued_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roster_records", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_roster_records_team_id",
                table: "roster_records",
                column: "team_id");

            migrationBuilder.CreateIndex(
                name: "IX_roster_records_team_id_issued_at",
                table: "roster_records",
                columns: new[] { "team_id", "issued_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "roster_records");
        }
    }
}
