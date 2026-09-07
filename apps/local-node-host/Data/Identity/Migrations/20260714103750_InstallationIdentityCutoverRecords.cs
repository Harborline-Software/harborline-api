using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Identity.Migrations;

/// <inheritdoc />
public partial class _20260714103750_InstallationIdentityCutoverRecords : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "installation_identity_cutover_state",
            columns: table => new
            {
                singleton_key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                stage = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                authority_version = table.Column<long>(type: "INTEGER", nullable: false),
                migration_run_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                v1_write_barrier_version = table.Column<long>(type: "INTEGER", nullable: false),
                source_watermark_digest = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                final_verification_digest = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                owner_version = table.Column<long>(type: "INTEGER", nullable: false),
                updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                committed_at_utc = table.Column<long>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_installation_identity_cutover_state", x => x.singleton_key);
                table.CheckConstraint("ck_identity_cutover_authority_version", "authority_version >= 1");
                table.CheckConstraint("ck_identity_cutover_barrier_version", "v1_write_barrier_version >= 0");
                table.CheckConstraint("ck_identity_cutover_owner_version", "owner_version > 0");
            });

        migrationBuilder.CreateTable(
            name: "installation_identity_migration_collisions",
            columns: table => new
            {
                collision_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                collision_key_digest = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                candidate_count = table.Column<int>(type: "INTEGER", nullable: false),
                expected_source_version = table.Column<long>(type: "INTEGER", nullable: false),
                status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                repair_idempotency_key_digest = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                audit_correlation_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                owner_version = table.Column<long>(type: "INTEGER", nullable: false),
                created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_installation_identity_migration_collisions", x => x.collision_id);
                table.CheckConstraint("ck_identity_collision_candidate_count", "candidate_count > 1");
                table.CheckConstraint("ck_identity_collision_owner_version", "owner_version > 0");
            });

        migrationBuilder.CreateTable(
            name: "installation_identity_migration_lease",
            columns: table => new
            {
                singleton_key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                lease_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                holder_fingerprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                lease_generation = table.Column<long>(type: "INTEGER", nullable: false),
                owner_version = table.Column<long>(type: "INTEGER", nullable: false),
                acquired_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                expires_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                released_at_utc = table.Column<long>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_installation_identity_migration_lease", x => x.singleton_key);
                table.CheckConstraint("ck_identity_migration_lease_expiry", "expires_at_utc > acquired_at_utc");
                table.CheckConstraint("ck_identity_migration_lease_generation", "lease_generation > 0");
                table.CheckConstraint("ck_identity_migration_lease_owner_version", "owner_version > 0");
            });

        migrationBuilder.CreateTable(
            name: "installation_identity_migration_source_watermarks",
            columns: table => new
            {
                source_kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                source_partition = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                migration_run_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                source_version = table.Column<long>(type: "INTEGER", nullable: false),
                high_watermark = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                snapshot_digest = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                owner_version = table.Column<long>(type: "INTEGER", nullable: false),
                captured_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_installation_identity_migration_source_watermarks", x => new { x.source_kind, x.source_partition });
                table.CheckConstraint("ck_identity_source_watermark_owner_version", "owner_version > 0");
                table.CheckConstraint("ck_identity_source_watermark_version", "source_version >= 0");
            });

        migrationBuilder.CreateTable(
            name: "installation_identity_migration_tombstones",
            columns: table => new
            {
                tombstone_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                source_kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                source_key_digest = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                source_version = table.Column<long>(type: "INTEGER", nullable: false),
                reason_code = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                audit_correlation_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                projection_digest = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                created_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_installation_identity_migration_tombstones", x => x.tombstone_id);
            });

        migrationBuilder.CreateTable(
            name: "installation_identity_root_designation",
            columns: table => new
            {
                singleton_key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                designation_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                source_composite_key_digest = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                account_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                expected_source_version = table.Column<long>(type: "INTEGER", nullable: false),
                idempotency_key_digest = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                audit_correlation_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                owner_version = table.Column<long>(type: "INTEGER", nullable: false),
                designated_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                verified_at_utc = table.Column<long>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_installation_identity_root_designation", x => x.singleton_key);
                table.CheckConstraint("ck_identity_root_designation_owner_version", "owner_version > 0");
                table.ForeignKey(
                    name: "FK_installation_identity_root_designation_installation_accounts_account_id",
                    column: x => x.account_id,
                    principalTable: "installation_accounts",
                    principalColumn: "account_id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.InsertData(
            table: "installation_identity_cutover_state",
            columns: new[] { "singleton_key", "authority_version", "committed_at_utc", "final_verification_digest", "migration_run_id", "owner_version", "source_watermark_digest", "stage", "updated_at_utc", "v1_write_barrier_version" },
            values: new object[] { "installation-identity-authority", 1L, null, null, null, 1L, null, "LegacyV1Authoritative", 0L, 0L });

        migrationBuilder.CreateIndex(
            name: "ux_identity_migration_collision_digest",
            table: "installation_identity_migration_collisions",
            column: "collision_key_digest",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_identity_migration_lease_id",
            table: "installation_identity_migration_lease",
            column: "lease_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_identity_source_watermarks_run_kind",
            table: "installation_identity_migration_source_watermarks",
            columns: new[] { "migration_run_id", "source_kind" });

        migrationBuilder.CreateIndex(
            name: "ux_identity_migration_tombstone_audit",
            table: "installation_identity_migration_tombstones",
            column: "audit_correlation_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_identity_migration_tombstone_source",
            table: "installation_identity_migration_tombstones",
            columns: new[] { "source_kind", "source_key_digest" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_installation_identity_root_designation_account_id",
            table: "installation_identity_root_designation",
            column: "account_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_identity_root_designation_audit",
            table: "installation_identity_root_designation",
            column: "audit_correlation_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_identity_root_designation_id",
            table: "installation_identity_root_designation",
            column: "designation_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_identity_root_designation_idempotency",
            table: "installation_identity_root_designation",
            column: "idempotency_key_digest",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "installation_identity_cutover_state");

        migrationBuilder.DropTable(
            name: "installation_identity_migration_collisions");

        migrationBuilder.DropTable(
            name: "installation_identity_migration_lease");

        migrationBuilder.DropTable(
            name: "installation_identity_migration_source_watermarks");

        migrationBuilder.DropTable(
            name: "installation_identity_migration_tombstones");

        migrationBuilder.DropTable(
            name: "installation_identity_root_designation");
    }
}
