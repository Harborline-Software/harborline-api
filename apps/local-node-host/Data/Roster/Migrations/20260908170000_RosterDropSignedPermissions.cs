using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Harborline.Api.LocalNodeHost.Data.Roster.Migrations;

/// <summary>Removes transitional permission evidence after roster wire version 3 retires that signed field.</summary>
[DbContext(typeof(NodeLocalRosterDbContext))]
[Migration("20260908170000_RosterDropSignedPermissions")]
public sealed class RosterDropSignedPermissions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("ALTER TABLE roster_records DROP COLUMN signed_permissions");

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>("signed_permissions", "roster_records",
            type: "TEXT", nullable: false, defaultValue: "");
}
