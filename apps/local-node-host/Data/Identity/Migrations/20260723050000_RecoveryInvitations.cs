using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Identity.Migrations;

/// <inheritdoc />
[DbContext(typeof(NodeLocalInstallationIdentityDbContext))]
[Migration("20260723050000_RecoveryInvitations")]
public sealed class _20260723050000_RecoveryInvitations : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "recovery_invitations",
            columns: table => new
            {
                recovery_invitation_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                tenant_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                issuer_account_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                issuer_principal_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                target_account_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                target_normalized_username = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                token_digest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                command_fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                issued_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                absolute_expires_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                consumed_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                credential_commitment_digest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                completed_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                revoked_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                owner_version = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_recovery_invitations", x => x.recovery_invitation_id);
                table.CheckConstraint(
                    "ck_recovery_invitation_digest",
                    "length(token_digest) = 64 AND token_digest NOT GLOB '*[^0-9A-F]*'");
                table.CheckConstraint(
                    "ck_recovery_invitation_expiry",
                    "absolute_expires_at_utc > issued_at_utc");
                table.CheckConstraint(
                    "ck_recovery_invitation_owner_version",
                    "owner_version > 0");
                table.ForeignKey(
                    name: "FK_recovery_invitations_installation_accounts_target_account_id",
                    column: x => x.target_account_id,
                    principalTable: "installation_accounts",
                    principalColumn: "account_id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_recovery_invitation_target_expiry",
            table: "recovery_invitations",
            columns: new[] { "target_account_id", "absolute_expires_at_utc" });

        migrationBuilder.CreateIndex(
            name: "ux_recovery_invitation_command",
            table: "recovery_invitations",
            columns: new[] { "tenant_id", "command_fingerprint" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_recovery_invitation_token_digest",
            table: "recovery_invitations",
            column: "token_digest",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "recovery_invitations");
    }
}
