using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Packs.Migrations;

/// <summary>
/// DES-0029 kernel-core-ck-6. An activation's evidence outbox row carries the authority its decision captured, so the
/// running host's drain can deliver it after the live decision is gone, and records its last delivery failure.
/// </summary>
[DbContext(typeof(NodeLocalPacksDbContext))]
[Migration("20260929180000_AddConfigurationEvidenceAuthority")]
public sealed class _20260929180000_AddConfigurationEvidenceAuthority : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "authority_snapshot_json", table: "configuration_evidence_outbox", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>(name: "last_error", table: "configuration_evidence_outbox", type: "TEXT", nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "last_error", table: "configuration_evidence_outbox");
        migrationBuilder.DropColumn(name: "authority_snapshot_json", table: "configuration_evidence_outbox");
    }
}
