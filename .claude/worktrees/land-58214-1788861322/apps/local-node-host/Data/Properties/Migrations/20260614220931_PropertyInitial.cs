using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Properties.Migrations
{
    /// <inheritdoc />
    public partial class _20260614220931_PropertyInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "properties",
                columns: table => new
                {
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    property_name = table.Column<string>(type: "TEXT", nullable: false),
                    address_line_1 = table.Column<string>(type: "TEXT", nullable: false),
                    city = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    postal_code = table.Column<string>(type: "TEXT", nullable: false),
                    units = table.Column<int>(type: "INTEGER", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    company = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    modified_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_properties", x => x.name);
                });

            migrationBuilder.CreateIndex(
                name: "IX_properties_company",
                table: "properties",
                column: "company");

            migrationBuilder.CreateIndex(
                name: "IX_properties_status",
                table: "properties",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "properties");
        }
    }
}
