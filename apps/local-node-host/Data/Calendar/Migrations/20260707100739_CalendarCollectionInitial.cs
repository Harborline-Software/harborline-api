using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Calendar.Migrations;

/// <inheritdoc />
public partial class _20260707100739_CalendarCollectionInitial : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "calendars",
            columns: table => new
            {
                id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                tenant_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                snapshot_json = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_calendars", x => new { x.tenant_id, x.id });
            });

        migrationBuilder.CreateIndex(
            name: "IX_calendars_tenant_id",
            table: "calendars",
            column: "tenant_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "calendars");
    }
}
