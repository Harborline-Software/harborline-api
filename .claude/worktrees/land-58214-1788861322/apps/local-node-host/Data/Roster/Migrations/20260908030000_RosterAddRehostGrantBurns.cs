using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Harborline.Api.LocalNodeHost.Data.Roster.Migrations;

/// <summary>Adds local single-use receipts beside the signed roster; existing slice-2 receipts survive.</summary>
[DbContext(typeof(NodeLocalRosterDbContext))]
[Migration("20260908030000_RosterAddRehostGrantBurns")]
public sealed class RosterAddRehostGrantBurns : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        "CREATE TABLE IF NOT EXISTS rehost_grant_burns (tenant TEXT NOT NULL, issuer TEXT NOT NULL, nonce TEXT NOT NULL, PRIMARY KEY (tenant, issuer, nonce))");

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("rehost_grant_burns");
}
