using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Admission.Migrations
{
    /// <inheritdoc />
    public partial class _20260621114414_AdmissionTokensInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admission_tokens",
                columns: table => new
                {
                    token_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    anchor_team_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    anchor_genesis_party = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    anchor_genesis_key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    issued_at = table.Column<long>(type: "INTEGER", nullable: false),
                    ttl_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    redeemed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admission_tokens", x => x.token_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admission_tokens");
        }
    }
}
