using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Harborline.Api.LocalNodeHost.Data.Roster.Migrations;

/// <summary>Retains local receipt time without inventing receipts for legacy rows.</summary>
[DbContext(typeof(NodeLocalRosterDbContext))]
[Migration("20260908123000_RosterReceiveTime")]
public sealed class RosterReceiveTime : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<long>("received_at", "roster_records", type: "INTEGER", nullable: true);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn("received_at", "roster_records");
}
