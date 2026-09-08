using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Identity.Migrations;

/// <inheritdoc />
public partial class _20260713214619_InstallationIdentityInitial : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "installation_accounts",
            columns: table => new
            {
                account_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                normalized_username = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                credential_hash = table.Column<string>(type: "TEXT", nullable: false),
                credential_algorithm = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                credential_ceremony_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                credential_version = table.Column<int>(type: "INTEGER", nullable: false),
                status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                security_version = table.Column<long>(type: "INTEGER", nullable: false),
                owner_version = table.Column<long>(type: "INTEGER", nullable: false),
                created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_installation_accounts", x => x.account_id);
            });

        migrationBuilder.CreateTable(
            name: "installation_identity",
            columns: table => new
            {
                singleton_key = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                installation_identity_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                active_root_epoch = table.Column<long>(type: "INTEGER", nullable: false),
                authority_version = table.Column<long>(type: "INTEGER", nullable: false),
                owner_version = table.Column<long>(type: "INTEGER", nullable: false),
                created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_installation_identity", x => x.singleton_key);
                table.UniqueConstraint("ak_installation_identity_id", x => x.installation_identity_id);
            });

        migrationBuilder.CreateTable(
            name: "installation_access_grants",
            columns: table => new
            {
                account_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                grant_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                permissions_json = table.Column<string>(type: "TEXT", nullable: false),
                status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                issuer_kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                issuer_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                authorization_epoch = table.Column<long>(type: "INTEGER", nullable: false),
                owner_version = table.Column<long>(type: "INTEGER", nullable: false),
                audit_correlation_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_installation_access_grants", x => x.account_id);
                table.UniqueConstraint("ak_installation_access_grants_grant_id", x => x.grant_id);
                table.ForeignKey(
                    name: "FK_installation_access_grants_installation_accounts_account_id",
                    column: x => x.account_id,
                    principalTable: "installation_accounts",
                    principalColumn: "account_id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "installation_audit_envelopes",
            columns: table => new
            {
                installation_identity_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                sequence = table.Column<long>(type: "INTEGER", nullable: false),
                correlation_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                command_fingerprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                event_type = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                actor_kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                actor_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                root_epoch = table.Column<long>(type: "INTEGER", nullable: false),
                root_public_key_fingerprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                previous_hash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                envelope_hash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                payload_digest = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                occurred_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_installation_audit_envelopes", x => new { x.installation_identity_id, x.sequence });
                table.ForeignKey(
                    name: "FK_installation_audit_envelopes_installation_identity_installation_identity_id",
                    column: x => x.installation_identity_id,
                    principalTable: "installation_identity",
                    principalColumn: "installation_identity_id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "installation_audit_heads",
            columns: table => new
            {
                installation_identity_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                sequence = table.Column<long>(type: "INTEGER", nullable: false),
                head_hash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                owner_version = table.Column<long>(type: "INTEGER", nullable: false),
                updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_installation_audit_heads", x => x.installation_identity_id);
                table.ForeignKey(
                    name: "FK_installation_audit_heads_installation_identity_installation_identity_id",
                    column: x => x.installation_identity_id,
                    principalTable: "installation_identity",
                    principalColumn: "installation_identity_id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "installation_root_key_epochs",
            columns: table => new
            {
                installation_identity_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                epoch_number = table.Column<long>(type: "INTEGER", nullable: false),
                root_public_key_fingerprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                transition_correlation_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                previous_root_fingerprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                retired_at_utc = table.Column<long>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_installation_root_key_epochs", x => new { x.installation_identity_id, x.epoch_number });
                table.ForeignKey(
                    name: "FK_installation_root_key_epochs_installation_identity_installation_identity_id",
                    column: x => x.installation_identity_id,
                    principalTable: "installation_identity",
                    principalColumn: "installation_identity_id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ux_installation_access_grants_audit_correlation",
            table: "installation_access_grants",
            column: "audit_correlation_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_installation_accounts_normalized_username",
            table: "installation_accounts",
            column: "normalized_username",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_installation_audit_envelopes_correlation",
            table: "installation_audit_envelopes",
            column: "correlation_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_installation_root_key_epochs_correlation",
            table: "installation_root_key_epochs",
            column: "transition_correlation_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_installation_root_key_epochs_fingerprint",
            table: "installation_root_key_epochs",
            column: "root_public_key_fingerprint",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_installation_root_key_epochs_one_active",
            table: "installation_root_key_epochs",
            column: "status",
            unique: true,
            filter: "status = 'Active'");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "installation_access_grants");

        migrationBuilder.DropTable(
            name: "installation_audit_envelopes");

        migrationBuilder.DropTable(
            name: "installation_audit_heads");

        migrationBuilder.DropTable(
            name: "installation_root_key_epochs");

        migrationBuilder.DropTable(
            name: "installation_accounts");

        migrationBuilder.DropTable(
            name: "installation_identity");
    }
}
