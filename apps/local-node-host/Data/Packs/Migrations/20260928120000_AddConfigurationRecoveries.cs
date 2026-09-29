using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Packs.Migrations;

/// <summary>
/// T-587 / T-909 slice 6. The record an offline <c>recover-configuration</c> commits and its audit row, written in
/// one transaction with the repairs the record names.
/// </summary>
[DbContext(typeof(NodeLocalPacksDbContext))]
[Migration("20260928120000_AddConfigurationRecoveries")]
public sealed class _20260928120000_AddConfigurationRecoveries : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "configuration_recoveries",
            columns: table => new
            {
                tenant = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                recovery_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                effective_digest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                reason = table.Column<string>(type: "TEXT", nullable: false),
                authority_snapshot = table.Column<string>(type: "TEXT", nullable: false),
                record_json = table.Column<string>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_configuration_recoveries", x => new { x.tenant, x.recovery_id }));

        migrationBuilder.CreateTable(
            name: "configuration_recovery_audit",
            columns: table => new
            {
                audit_id = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                tenant = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                actor_id = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                recorded_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                payload_json = table.Column<string>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_configuration_recovery_audit", x => x.audit_id));
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "configuration_recovery_audit");
        migrationBuilder.DropTable(name: "configuration_recoveries");
    }
}
