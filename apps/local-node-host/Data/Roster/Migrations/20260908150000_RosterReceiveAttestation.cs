using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Harborline.Api.LocalNodeHost.Data.Roster.Migrations;

/// <summary>Persists the wire-versioned signature that authenticates each durable receive time.</summary>
[DbContext(typeof(NodeLocalRosterDbContext))]
[Migration("20260908150000_RosterReceiveAttestation")]
public sealed class RosterReceiveAttestation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>("wire_format_version", "roster_records", type: "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<string>("received_by_party", "roster_records", type: "TEXT", maxLength: 256, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>("received_by_key", "roster_records", type: "TEXT", maxLength: 256, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>("receive_attestation_signature", "roster_records", type: "TEXT", maxLength: 256, nullable: false, defaultValue: "");
        migrationBuilder.CreateIndex("IX_roster_records_team_id_received_at", "roster_records", new[] { "team_id", "received_at" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_roster_records_team_id_received_at", "roster_records");
        migrationBuilder.DropColumn("wire_format_version", "roster_records");
        migrationBuilder.DropColumn("received_by_party", "roster_records");
        migrationBuilder.DropColumn("received_by_key", "roster_records");
        migrationBuilder.DropColumn("receive_attestation_signature", "roster_records");
    }
}
