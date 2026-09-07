using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Maintenance.Migrations
{
    /// <inheritdoc />
    public partial class _20260613223256_MaintenanceInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "maintenance_tickets",
                columns: table => new
                {
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    subject = table.Column<string>(type: "TEXT", nullable: false),
                    property = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    priority = table.Column<string>(type: "TEXT", nullable: false),
                    assigned_to = table.Column<string>(type: "TEXT", nullable: true),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    resolution = table.Column<string>(type: "TEXT", nullable: true),
                    cost = table.Column<decimal>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    modified_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_maintenance_tickets", x => x.name);
                });

            migrationBuilder.CreateIndex(
                name: "IX_maintenance_tickets_status",
                table: "maintenance_tickets",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "maintenance_tickets");
        }
    }
}
