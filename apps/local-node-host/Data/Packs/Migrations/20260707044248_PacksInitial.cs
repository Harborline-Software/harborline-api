using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Packs.Migrations;

/// <inheritdoc />
public partial class _20260707044248_PacksInitial : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "pack_installed_versions",
            columns: table => new
            {
                tenant = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                pack_key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                lifecycle = table.Column<int>(type: "INTEGER", nullable: false),
                payload_json = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_pack_installed_versions", x => new { x.tenant, x.pack_key, x.version });
            });

        migrationBuilder.CreateTable(
            name: "pack_key_ownership",
            columns: table => new
            {
                tenant = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                content_key = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                owning_pack_key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_pack_key_ownership", x => new { x.tenant, x.content_key });
            });

        migrationBuilder.CreateTable(
            name: "pack_overrides",
            columns: table => new
            {
                tenant = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                pack_key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                content_key = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                overlay_json = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_pack_overrides", x => new { x.tenant, x.pack_key, x.content_key });
            });

        migrationBuilder.CreateTable(
            name: "pack_watermarks",
            columns: table => new
            {
                tenant = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                pack_key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                floors_json = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_pack_watermarks", x => new { x.tenant, x.pack_key });
            });

        migrationBuilder.CreateIndex(
            name: "IX_pack_installed_versions_tenant_pack_key_lifecycle",
            table: "pack_installed_versions",
            columns: new[] { "tenant", "pack_key", "lifecycle" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "pack_installed_versions");

        migrationBuilder.DropTable(
            name: "pack_key_ownership");

        migrationBuilder.DropTable(
            name: "pack_overrides");

        migrationBuilder.DropTable(
            name: "pack_watermarks");
    }
}
