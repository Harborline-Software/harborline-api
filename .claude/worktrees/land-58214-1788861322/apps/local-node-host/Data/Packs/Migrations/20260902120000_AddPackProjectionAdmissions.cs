using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Packs.Migrations;

/// <inheritdoc />
[DbContext(typeof(NodeLocalPacksDbContext))]
[Migration("20260902120000_AddPackProjectionAdmissions")]
public sealed class _20260902120000_AddPackProjectionAdmissions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "pack_projection_admissions",
            columns: table => new
            {
                admission_id = table.Column<Guid>(type: "TEXT", nullable: false),
                tenant = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                pack_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                pack_version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                principal = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                instant = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                derivation_ids_json = table.Column<string>(type: "TEXT", nullable: false),
                projected = table.Column<bool>(type: "INTEGER", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_pack_projection_admissions", x => x.admission_id));

        migrationBuilder.CreateIndex(
            name: "IX_pack_projection_admissions_tenant_projected",
            table: "pack_projection_admissions",
            columns: new[] { "tenant", "projected" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "pack_projection_admissions");
}
