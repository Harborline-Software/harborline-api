using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Harborline.Api.LocalNodeHost.Data.Roster.Migrations;

/// <summary>Removes the retired permission set from the durable roster record.</summary>
[DbContext(typeof(NodeLocalRosterDbContext))]
[Migration("20260928120000_RosterDropLegacyPermissions")]
public sealed class RosterDropLegacyPermissions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("ALTER TABLE roster_records DROP COLUMN permissions");

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>("permissions", "roster_records",
            type: "TEXT", nullable: false, defaultValue: "");
}
