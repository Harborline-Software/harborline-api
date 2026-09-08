using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Identity.Migrations;

/// <inheritdoc />
public partial class _20260713233947_InstallationIdentityCoordination : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "installation_identity_coordinators",
            columns: table => new
            {
                correlation_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                command_type = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                command_fingerprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                payload_schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                account_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                actor_account_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                authority_evidence_digest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                expected_account_owner_version = table.Column<long>(type: "INTEGER", nullable: false),
                expected_account_security_version = table.Column<long>(type: "INTEGER", nullable: false),
                expected_actor_owner_version = table.Column<long>(type: "INTEGER", nullable: false),
                expected_actor_security_version = table.Column<long>(type: "INTEGER", nullable: false),
                tenant_ids_json = table.Column<string>(type: "TEXT", nullable: false),
                intent_payload_json = table.Column<string>(type: "TEXT", nullable: false),
                final_receipts_json = table.Column<string>(type: "TEXT", nullable: false),
                state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                failure_code = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                owner_version = table.Column<long>(type: "INTEGER", nullable: false),
                created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_installation_identity_coordinators", x => x.correlation_id);
                table.ForeignKey(
                    name: "FK_installation_identity_coordinators_installation_accounts_account_id",
                    column: x => x.account_id,
                    principalTable: "installation_accounts",
                    principalColumn: "account_id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_installation_identity_coordinators_account_state",
            table: "installation_identity_coordinators",
            columns: new[] { "account_id", "state" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "installation_identity_coordinators");
    }
}
