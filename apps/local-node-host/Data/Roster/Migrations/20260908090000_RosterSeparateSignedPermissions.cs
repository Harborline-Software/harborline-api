using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Harborline.Api.LocalNodeHost.Data.Roster.Migrations;

/// <summary>Separate historical signed evidence from the legacy carried field; never promote legacy values.</summary>
[DbContext(typeof(NodeLocalRosterDbContext))]
[Migration("20260908090000_RosterSeparateSignedPermissions")]
public sealed class RosterSeparateSignedPermissions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>("signed_permissions", "roster_records",
            type: "TEXT", nullable: false, defaultValue: "");

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn("signed_permissions", "roster_records");
}
