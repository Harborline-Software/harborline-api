using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Calendar.Migrations
{
    /// <inheritdoc />
    public partial class _20260624120000_CalendarInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "calendar_events",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    snapshot_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_calendar_events", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "resource_availability",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    resource_kind = table.Column<int>(type: "INTEGER", nullable: false),
                    resource_value = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    snapshot_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "PK_resource_availability",
                        x => new { x.tenant_id, x.resource_kind, x.resource_value });
                });

            migrationBuilder.CreateIndex(
                name: "IX_calendar_events_tenant_id",
                table: "calendar_events",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_resource_availability_tenant_id",
                table: "resource_availability",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calendar_events");

            migrationBuilder.DropTable(
                name: "resource_availability");
        }
    }
}
