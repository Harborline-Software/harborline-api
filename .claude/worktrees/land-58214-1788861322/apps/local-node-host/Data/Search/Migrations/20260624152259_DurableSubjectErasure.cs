using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Search.Migrations
{
    /// <inheritdoc />
    public partial class _20260624152259_DurableSubjectErasure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "search_subject_erasures",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    subject_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    erased_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_subject_erasures", x => new { x.tenant_id, x.subject_id });
                });

            migrationBuilder.CreateTable(
                name: "search_subject_tombstones",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    pseudonym = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    erased_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    approving_actors_json = table.Column<string>(type: "TEXT", nullable: false),
                    legal_basis = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_subject_tombstones", x => new { x.tenant_id, x.pseudonym });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "search_subject_erasures");

            migrationBuilder.DropTable(
                name: "search_subject_tombstones");
        }
    }
}
