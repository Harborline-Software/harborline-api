using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Leases.Migrations
{
    /// <inheritdoc />
    public partial class _20260614220950_LeaseInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "leases",
                columns: table => new
                {
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    tenant = table.Column<string>(type: "TEXT", nullable: false),
                    property = table.Column<string>(type: "TEXT", nullable: false),
                    unit = table.Column<string>(type: "TEXT", nullable: false),
                    start_date = table.Column<string>(type: "TEXT", nullable: false),
                    end_date = table.Column<string>(type: "TEXT", nullable: false),
                    monthly_rent = table.Column<decimal>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    company = table.Column<string>(type: "TEXT", nullable: false),
                    term_cadence = table.Column<string>(type: "TEXT", nullable: false),
                    auto_renew = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    modified_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leases", x => x.name);
                });

            migrationBuilder.CreateIndex(
                name: "IX_leases_property",
                table: "leases",
                column: "property");

            migrationBuilder.CreateIndex(
                name: "IX_leases_status",
                table: "leases",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "leases");
        }
    }
}
