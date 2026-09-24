using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Calendar.Migrations;

/// <inheritdoc />
public partial class _20260920033344_CalendarCapacityEpochs : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "calendar_capacity_epochs",
            columns: table => new
            {
                tenant_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                resource = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                epoch = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_calendar_capacity_epochs", x => new { x.tenant_id, x.resource });
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "calendar_capacity_epochs");
    }
}
