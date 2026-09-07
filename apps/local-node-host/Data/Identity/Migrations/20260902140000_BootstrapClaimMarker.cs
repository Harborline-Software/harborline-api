using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Identity.Migrations;

[DbContext(typeof(NodeLocalInstallationIdentityDbContext))]
[Migration("20260902140000_BootstrapClaimMarker")]
public partial class _20260902140000_BootstrapClaimMarker : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "bootstrap_claim_marker",
            columns: table => new
            {
                singleton_key = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                installation_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                grant_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                issuer_kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                issuer_identity = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                nonce_digest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                claimed_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_bootstrap_claim_marker", x => x.singleton_key));

        migrationBuilder.CreateIndex(
            name: "ux_bootstrap_claim_marker_grant",
            table: "bootstrap_claim_marker",
            column: "grant_id",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "ux_bootstrap_claim_marker_nonce",
            table: "bootstrap_claim_marker",
            column: "nonce_digest",
            unique: true);

        migrationBuilder.CreateTable(
            name: "bootstrap_claim_window",
            columns: table => new
            {
                singleton_key = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                installation_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                issued_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                deadline_utc = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_bootstrap_claim_window", x => x.singleton_key));

        // Upgraded installations are already retired when either their founder/root grant or their
        // permanent founder/bootstrap audit evidence exists. This is intentionally a singleton
        // tombstone: deleting ordinary tenant grants cannot reopen bootstrap.
        migrationBuilder.Sql(
            """
            INSERT INTO bootstrap_claim_marker (
                singleton_key, installation_id, grant_id, issuer_kind, issuer_identity,
                nonce_digest, claimed_at_utc)
            SELECT
                'bootstrap-claim', i.installation_identity_id,
                COALESCE((SELECT grant_id FROM installation_access_grants LIMIT 1), 'migration-retired'),
                'DesktopOsSession', 'migration:preexisting-founder-evidence',
                '0000000000000000000000000000000000000000000000000000000000000000',
                i.created_at_utc
            FROM installation_identity AS i
            WHERE EXISTS (SELECT 1 FROM installation_access_grants)
               OR EXISTS (
                    SELECT 1 FROM installation_audit_envelopes
                    WHERE event_type IN (
                        'InstallationFounderBootstrapped',
                        'InstallationFounderBindingDesignated',
                        'InstallationBootstrapClaimRedeemed'))
            LIMIT 1;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "bootstrap_claim_window");
        migrationBuilder.DropTable(name: "bootstrap_claim_marker");
    }
}
