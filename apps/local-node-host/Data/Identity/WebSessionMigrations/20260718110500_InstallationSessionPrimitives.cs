using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Identity.WebSessionMigrations;

/// <inheritdoc />
[DbContext(typeof(NodeLocalWebSessionDbContext))]
[Migration("20260718110500_InstallationSessionPrimitives")]
public partial class _20260718110500_InstallationSessionPrimitives : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE "__temp_web_installation_sessions" (
                "session_correlation_id" TEXT NOT NULL CONSTRAINT "PK_web_installation_sessions" PRIMARY KEY,
                "account_id" TEXT NOT NULL,
                "account_security_version" INTEGER NOT NULL,
                "installation_grant_id" TEXT NOT NULL,
                "installation_grant_owner_version" INTEGER NOT NULL,
                "authorization_epoch" INTEGER NOT NULL,
                "handle_digest" TEXT NOT NULL,
                "antiforgery_state_id" TEXT NOT NULL,
                "coordination_correlation_id" TEXT NOT NULL,
                "issued_at_utc" INTEGER NOT NULL,
                "absolute_expires_at_utc" INTEGER NOT NULL,
                "owner_version" INTEGER NOT NULL,
                CONSTRAINT "ck_web_installation_session_authority_versions"
                    CHECK (account_security_version > 0 AND installation_grant_owner_version > 0
                        AND authorization_epoch > 0),
                CONSTRAINT "ck_web_installation_session_expiry"
                    CHECK (absolute_expires_at_utc > issued_at_utc
                        AND absolute_expires_at_utc <= issued_at_utc + 900000),
                CONSTRAINT "ck_web_installation_session_handle_digest"
                    CHECK (length(handle_digest) = 64 AND handle_digest NOT GLOB '*[^0-9A-F]*'),
                CONSTRAINT "ck_web_installation_session_owner_version" CHECK (owner_version > 0)
            );
            INSERT INTO "__temp_web_installation_sessions" (
                "session_correlation_id", "account_id", "account_security_version",
                "installation_grant_id", "installation_grant_owner_version", "authorization_epoch",
                "handle_digest", "antiforgery_state_id", "coordination_correlation_id",
                "issued_at_utc", "absolute_expires_at_utc", "owner_version")
            SELECT "session_correlation_id", "account_id", "account_security_version",
                "installation_grant_id", "installation_grant_owner_version", "authorization_epoch",
                "handle_digest", "antiforgery_state_id", "coordination_correlation_id",
                "issued_at_utc", "absolute_expires_at_utc", "owner_version"
            FROM "web_installation_sessions";
            DROP TABLE "web_installation_sessions";
            ALTER TABLE "__temp_web_installation_sessions" RENAME TO "web_installation_sessions";
            CREATE INDEX "ix_web_installation_session_account"
                ON "web_installation_sessions" ("account_id");
            CREATE INDEX "ix_web_installation_session_antiforgery"
                ON "web_installation_sessions" ("antiforgery_state_id");
            CREATE INDEX "ix_web_installation_session_expiry"
                ON "web_installation_sessions" ("absolute_expires_at_utc");
            CREATE UNIQUE INDEX "ux_web_installation_session_handle_digest"
                ON "web_installation_sessions" ("handle_digest");
            """);

        migrationBuilder.DropIndex(
            name: "ix_web_session_revocation_subject",
            table: "web_session_revocations");
        migrationBuilder.CreateIndex(
            name: "ux_web_session_revocation_subject",
            table: "web_session_revocations",
            columns: new[] { "audience", "subject_correlation_id" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ux_web_session_revocation_subject",
            table: "web_session_revocations");
        migrationBuilder.CreateIndex(
            name: "ix_web_session_revocation_subject",
            table: "web_session_revocations",
            columns: new[] { "audience", "subject_correlation_id" });

        migrationBuilder.Sql(
            """
            CREATE TABLE "__temp_web_installation_sessions" (
                "session_correlation_id" TEXT NOT NULL CONSTRAINT "PK_web_installation_sessions" PRIMARY KEY,
                "account_id" TEXT NOT NULL,
                "account_security_version" INTEGER NOT NULL,
                "installation_grant_id" TEXT NOT NULL,
                "installation_grant_owner_version" INTEGER NOT NULL,
                "authorization_epoch" INTEGER NOT NULL,
                "handle_digest" TEXT NOT NULL,
                "antiforgery_state_id" TEXT NOT NULL,
                "coordination_correlation_id" TEXT NOT NULL,
                "issued_at_utc" INTEGER NOT NULL,
                "absolute_expires_at_utc" INTEGER NOT NULL,
                "owner_version" INTEGER NOT NULL,
                CONSTRAINT "ck_web_installation_session_expiry"
                    CHECK (absolute_expires_at_utc > issued_at_utc),
                CONSTRAINT "ck_web_installation_session_owner_version" CHECK (owner_version > 0)
            );
            INSERT INTO "__temp_web_installation_sessions" (
                "session_correlation_id", "account_id", "account_security_version",
                "installation_grant_id", "installation_grant_owner_version", "authorization_epoch",
                "handle_digest", "antiforgery_state_id", "coordination_correlation_id",
                "issued_at_utc", "absolute_expires_at_utc", "owner_version")
            SELECT "session_correlation_id", "account_id", "account_security_version",
                "installation_grant_id", "installation_grant_owner_version", "authorization_epoch",
                "handle_digest", "antiforgery_state_id", "coordination_correlation_id",
                "issued_at_utc", "absolute_expires_at_utc", "owner_version"
            FROM "web_installation_sessions";
            DROP TABLE "web_installation_sessions";
            ALTER TABLE "__temp_web_installation_sessions" RENAME TO "web_installation_sessions";
            CREATE INDEX "ix_web_installation_session_account"
                ON "web_installation_sessions" ("account_id");
            CREATE INDEX "ix_web_installation_session_antiforgery"
                ON "web_installation_sessions" ("antiforgery_state_id");
            CREATE INDEX "ix_web_installation_session_expiry"
                ON "web_installation_sessions" ("absolute_expires_at_utc");
            CREATE UNIQUE INDEX "ux_web_installation_session_handle_digest"
                ON "web_installation_sessions" ("handle_digest");
            """);
    }
}
