using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Packs.Migrations;

/// <inheritdoc />
[DbContext(typeof(NodeLocalPacksDbContext))]
[Migration("20260919090000_AddConfigurationGenerations")]
public sealed class _20260919090000_AddConfigurationGenerations : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "configuration_effective_generations",
            columns: table => new
            {
                tenant = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                digest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                references_json = table.Column<string>(type: "TEXT", nullable: false),
                principal = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                activated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                decision_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                evidence_intent_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_configuration_effective_generations", x => x.tenant));

        migrationBuilder.CreateTable(
            name: "configuration_prepared_projections",
            columns: table => new
            {
                tenant = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                candidate_digest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                revision = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                projection_digest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                references_json = table.Column<string>(type: "TEXT", nullable: false),
                baseline_digest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                destination_digest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                prepared_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey(
                "PK_configuration_prepared_projections", x => new { x.tenant, x.candidate_digest }));

        migrationBuilder.CreateTable(
            name: "configuration_evidence_outbox",
            columns: table => new
            {
                tenant = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                intent_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                reason = table.Column<string>(type: "TEXT", nullable: false),
                inputs_digest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                decision_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                decision_json = table.Column<string>(type: "TEXT", nullable: false),
                prior_digest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                new_digest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                principal = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                committed_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                published_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
            },
            constraints: table => table.PrimaryKey(
                "PK_configuration_evidence_outbox", x => new { x.tenant, x.intent_id }));

        migrationBuilder.CreateIndex(
            name: "IX_configuration_evidence_outbox_tenant_published_at",
            table: "configuration_evidence_outbox",
            columns: new[] { "tenant", "published_at" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "configuration_evidence_outbox");
        migrationBuilder.DropTable(name: "configuration_prepared_projections");
        migrationBuilder.DropTable(name: "configuration_effective_generations");
    }
}
