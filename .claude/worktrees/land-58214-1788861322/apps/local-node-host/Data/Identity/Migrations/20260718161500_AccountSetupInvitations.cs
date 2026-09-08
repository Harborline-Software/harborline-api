using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Identity.Migrations;

/// <inheritdoc />
[DbContext(typeof(NodeLocalInstallationIdentityDbContext))]
[Migration("20260718161500_AccountSetupInvitations")]
public sealed class _20260718161500_AccountSetupInvitations : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "account_setup_invitations",
            columns: table => new
            {
                invitation_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                tenant_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                inviter_account_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                inviter_principal_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                inviter_party_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                inviter_session_correlation_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                inviter_membership_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                inviter_membership_owner_version = table.Column<long>(type: "INTEGER", nullable: false),
                inviter_grant_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                inviter_grant_owner_version = table.Column<long>(type: "INTEGER", nullable: false),
                inviter_authorization_epoch = table.Column<long>(type: "INTEGER", nullable: false),
                requested_permissions_json = table.Column<string>(type: "TEXT", nullable: false),
                token_digest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                purpose = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                command_fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                issued_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                absolute_expires_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                consumed_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                revoked_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                owner_version = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_account_setup_invitations", x => x.invitation_id);
                table.CheckConstraint(
                    "ck_account_setup_invitation_digest",
                    "length(token_digest) = 64 AND token_digest NOT GLOB '*[^0-9A-F]*'");
                table.CheckConstraint(
                    "ck_account_setup_invitation_expiry",
                    "absolute_expires_at_utc > issued_at_utc");
                table.CheckConstraint(
                    "ck_account_setup_invitation_owner_version",
                    "owner_version > 0");
                table.CheckConstraint(
                    "ck_account_setup_invitation_purpose",
                    "purpose = 'AccountSetup'");
            });

        migrationBuilder.CreateIndex(
            name: "ix_account_setup_invitation_expiry",
            table: "account_setup_invitations",
            columns: new[] { "tenant_id", "absolute_expires_at_utc" });

        migrationBuilder.CreateIndex(
            name: "ux_account_setup_invitation_command",
            table: "account_setup_invitations",
            columns: new[] { "tenant_id", "command_fingerprint" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_account_setup_invitation_token_digest",
            table: "account_setup_invitations",
            column: "token_digest",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "account_setup_invitations");
    }
}
