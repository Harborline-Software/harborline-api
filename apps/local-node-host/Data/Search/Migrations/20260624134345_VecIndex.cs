using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Search.Migrations
{
    /// <inheritdoc />
    public partial class _20260624134345_VecIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "search_grants",
                columns: table => new
                {
                    grant_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    principal_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    roles_json = table.Column<string>(type: "TEXT", nullable: false),
                    scope_json = table.Column<string>(type: "TEXT", nullable: false),
                    residency = table.Column<int>(type: "INTEGER", nullable: false),
                    validity_from_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    validity_until_unix_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    granted_by = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    granted_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    revoked_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    source_reference = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_grants", x => new { x.tenant_id, x.grant_id });
                });

            migrationBuilder.CreateTable(
                name: "search_vec_rows",
                columns: table => new
                {
                    record_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    subject_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    model_version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    dimension = table.Column<int>(type: "INTEGER", nullable: false),
                    encrypted_embedding = table.Column<byte[]>(type: "BLOB", nullable: false),
                    embedding_nonce = table.Column<byte[]>(type: "BLOB", nullable: false),
                    key_version = table.Column<int>(type: "INTEGER", nullable: false),
                    residency = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_vec_rows", x => x.record_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_search_grants_tenant_id_principal_id",
                table: "search_grants",
                columns: new[] { "tenant_id", "principal_id" });

            migrationBuilder.CreateIndex(
                name: "IX_search_grants_tenant_id_source_reference",
                table: "search_grants",
                columns: new[] { "tenant_id", "source_reference" });

            migrationBuilder.CreateIndex(
                name: "IX_search_vec_rows_tenant_id",
                table: "search_vec_rows",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_search_vec_rows_tenant_id_subject_id",
                table: "search_vec_rows",
                columns: new[] { "tenant_id", "subject_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "search_grants");

            migrationBuilder.DropTable(
                name: "search_vec_rows");
        }
    }
}
