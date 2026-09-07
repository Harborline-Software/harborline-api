using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Identity.WebSessionMigrations;

/// <inheritdoc />
public partial class _20260716214207_WebSessionRecords : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "web_account_access_challenges",
            columns: table => new
            {
                challenge_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                account_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                account_security_version = table.Column<long>(type: "INTEGER", nullable: false),
                handle_digest = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                coordination_correlation_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                issued_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                absolute_expires_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                consumed_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                revoked_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                owner_version = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_web_account_access_challenges", x => x.challenge_id);
                table.CheckConstraint("ck_web_account_challenge_expiry", "absolute_expires_at_utc > issued_at_utc");
                table.CheckConstraint("ck_web_account_challenge_owner_version", "owner_version > 0");
            });

        migrationBuilder.CreateTable(
            name: "web_antiforgery_states",
            columns: table => new
            {
                antiforgery_state_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                audience = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                account_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                subject_correlation_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                token_digest = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                coordination_correlation_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                issued_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                absolute_expires_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                consumed_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                revoked_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                owner_version = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_web_antiforgery_states", x => x.antiforgery_state_id);
                table.CheckConstraint("ck_web_antiforgery_state_expiry", "absolute_expires_at_utc > issued_at_utc");
                table.CheckConstraint("ck_web_antiforgery_state_owner_version", "owner_version > 0");
            });

        migrationBuilder.CreateTable(
            name: "web_installation_sessions",
            columns: table => new
            {
                session_correlation_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                account_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                account_security_version = table.Column<long>(type: "INTEGER", nullable: false),
                installation_grant_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                installation_grant_owner_version = table.Column<long>(type: "INTEGER", nullable: false),
                authorization_epoch = table.Column<long>(type: "INTEGER", nullable: false),
                handle_digest = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                antiforgery_state_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                coordination_correlation_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                issued_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                absolute_expires_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                owner_version = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_web_installation_sessions", x => x.session_correlation_id);
                table.CheckConstraint("ck_web_installation_session_expiry", "absolute_expires_at_utc > issued_at_utc");
                table.CheckConstraint("ck_web_installation_session_owner_version", "owner_version > 0");
            });

        migrationBuilder.CreateTable(
            name: "web_session_revocations",
            columns: table => new
            {
                revocation_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                audience = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                account_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                subject_correlation_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                superseded_by_correlation_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                reason_code = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                coordination_correlation_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                revoked_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                owner_version = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_web_session_revocations", x => x.revocation_id);
                table.CheckConstraint("ck_web_session_revocation_owner_version", "owner_version > 0");
            });

        migrationBuilder.CreateTable(
            name: "web_user_sessions",
            columns: table => new
            {
                session_correlation_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                account_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                account_security_version = table.Column<long>(type: "INTEGER", nullable: false),
                tenant_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                membership_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                membership_owner_version = table.Column<long>(type: "INTEGER", nullable: false),
                tenant_principal_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                canonical_party_reference = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                pinned_grant_owner_versions_json = table.Column<string>(type: "TEXT", nullable: false),
                authorization_epoch = table.Column<long>(type: "INTEGER", nullable: false),
                handle_digest = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                antiforgery_state_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                coordination_correlation_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                issued_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                idle_expires_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                absolute_expires_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                owner_version = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_web_user_sessions", x => x.session_correlation_id);
                table.CheckConstraint("ck_web_user_session_absolute_expiry", "absolute_expires_at_utc > issued_at_utc");
                table.CheckConstraint("ck_web_user_session_idle_expiry", "idle_expires_at_utc > issued_at_utc AND idle_expires_at_utc <= absolute_expires_at_utc");
                table.CheckConstraint("ck_web_user_session_owner_version", "owner_version > 0");
            });

        migrationBuilder.CreateIndex(
            name: "ix_web_account_challenge_account",
            table: "web_account_access_challenges",
            column: "account_id");

        migrationBuilder.CreateIndex(
            name: "ix_web_account_challenge_expiry",
            table: "web_account_access_challenges",
            column: "absolute_expires_at_utc");

        migrationBuilder.CreateIndex(
            name: "ux_web_account_challenge_handle_digest",
            table: "web_account_access_challenges",
            column: "handle_digest",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_web_antiforgery_state_expiry",
            table: "web_antiforgery_states",
            column: "absolute_expires_at_utc");

        migrationBuilder.CreateIndex(
            name: "ix_web_antiforgery_state_subject",
            table: "web_antiforgery_states",
            columns: new[] { "audience", "subject_correlation_id" });

        migrationBuilder.CreateIndex(
            name: "ux_web_antiforgery_state_token_digest",
            table: "web_antiforgery_states",
            column: "token_digest",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_web_installation_session_account",
            table: "web_installation_sessions",
            column: "account_id");

        migrationBuilder.CreateIndex(
            name: "ix_web_installation_session_antiforgery",
            table: "web_installation_sessions",
            column: "antiforgery_state_id");

        migrationBuilder.CreateIndex(
            name: "ix_web_installation_session_expiry",
            table: "web_installation_sessions",
            column: "absolute_expires_at_utc");

        migrationBuilder.CreateIndex(
            name: "ux_web_installation_session_handle_digest",
            table: "web_installation_sessions",
            column: "handle_digest",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_web_session_revocation_account",
            table: "web_session_revocations",
            column: "account_id");

        migrationBuilder.CreateIndex(
            name: "ix_web_session_revocation_subject",
            table: "web_session_revocations",
            columns: new[] { "audience", "subject_correlation_id" });

        migrationBuilder.CreateIndex(
            name: "ix_web_user_session_account_tenant",
            table: "web_user_sessions",
            columns: new[] { "account_id", "tenant_id" });

        migrationBuilder.CreateIndex(
            name: "ix_web_user_session_antiforgery",
            table: "web_user_sessions",
            column: "antiforgery_state_id");

        migrationBuilder.CreateIndex(
            name: "ix_web_user_session_expiry",
            table: "web_user_sessions",
            column: "absolute_expires_at_utc");

        migrationBuilder.CreateIndex(
            name: "ix_web_user_session_membership",
            table: "web_user_sessions",
            columns: new[] { "tenant_id", "membership_id" });

        migrationBuilder.CreateIndex(
            name: "ux_web_user_session_handle_digest",
            table: "web_user_sessions",
            column: "handle_digest",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "web_account_access_challenges");

        migrationBuilder.DropTable(
            name: "web_antiforgery_states");

        migrationBuilder.DropTable(
            name: "web_installation_sessions");

        migrationBuilder.DropTable(
            name: "web_session_revocations");

        migrationBuilder.DropTable(
            name: "web_user_sessions");
    }
}
