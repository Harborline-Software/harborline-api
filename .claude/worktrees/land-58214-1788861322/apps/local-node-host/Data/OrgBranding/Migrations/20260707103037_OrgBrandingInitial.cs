using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.OrgBranding.Migrations;

/// <inheritdoc />
public partial class _20260707103037_OrgBrandingInitial : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "org_branding",
            columns: table => new
            {
                tenant_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                display_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                logo_ref = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                logo_dark_ref = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                accent_color = table.Column<string>(type: "TEXT", maxLength: 9, nullable: true),
                accent_foreground = table.Column<string>(type: "TEXT", maxLength: 9, nullable: true),
                updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                updated_by = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_org_branding", x => x.tenant_id);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "org_branding");
    }
}
