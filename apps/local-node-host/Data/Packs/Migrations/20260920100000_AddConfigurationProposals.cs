using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Packs.Migrations;

/// <summary>
/// T-461. Proposed changes, their immutable Saved versions and the signed Released packages offered
/// for activation. None of these tables is on the effective-generation path: the pointer stays in
/// configuration_effective_generations, which T-644 added and this migration does not touch.
/// </summary>
[DbContext(typeof(NodeLocalPacksDbContext))]
[Migration("20260920100000_AddConfigurationProposals")]
public sealed class _20260920100000_AddConfigurationProposals : Migration
{
    // CA1861: a constant array argument allocates per call; the rule asks for a static field.
    private static readonly string[] ReleasedProposalIndexColumns = ["tenant", "proposal_id"];

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "configuration_proposals",
            columns: table => new
            {
                tenant = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                proposal_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                baseline_digest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                edits_json = table.Column<string>(type: "TEXT", nullable: false),
                started_by = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                started_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                autosaved_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                saved_version_count = table.Column<int>(type: "INTEGER", nullable: false),
                checked_digest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                check_receipt_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
            },
            constraints: table => table.PrimaryKey(
                "PK_configuration_proposals", x => new { x.tenant, x.proposal_id }));

        migrationBuilder.CreateTable(
            name: "configuration_saved_versions",
            columns: table => new
            {
                tenant = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                proposal_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                digest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                baseline_digest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                author = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                rationale = table.Column<string>(type: "TEXT", nullable: false),
                saved_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                edits_json = table.Column<string>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey(
                "PK_configuration_saved_versions", x => new { x.tenant, x.proposal_id, x.ordinal }));

        migrationBuilder.CreateTable(
            name: "configuration_released_packages",
            columns: table => new
            {
                tenant = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                digest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                proposal_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                saved_version_digest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                baseline_digest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                package_key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                revision = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                document = table.Column<byte[]>(type: "BLOB", nullable: false),
                signature_json = table.Column<string>(type: "TEXT", nullable: false),
                released_by = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                check_receipt_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                released_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey(
                "PK_configuration_released_packages", x => new { x.tenant, x.digest }));

        migrationBuilder.CreateIndex(
            name: "IX_configuration_released_packages_tenant_proposal_id",
            table: "configuration_released_packages",
            columns: ReleasedProposalIndexColumns);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "configuration_released_packages");
        migrationBuilder.DropTable(name: "configuration_saved_versions");
        migrationBuilder.DropTable(name: "configuration_proposals");
    }
}
