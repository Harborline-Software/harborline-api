using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Identity.Migrations;

/// <inheritdoc />
[DbContext(typeof(NodeLocalInstallationIdentityDbContext))]
[Migration("20260718132100_LegacyRenameCheckpointBinding")]
public sealed class _20260718132100_LegacyRenameCheckpointBinding : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "expected_owner_version",
            table: "installation_identity_migration_collisions",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "recovery_actor_id",
            table: "installation_identity_migration_collisions",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "recovery_root_epoch",
            table: "installation_identity_migration_collisions",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "recovery_root_public_key_fingerprint",
            table: "installation_identity_migration_collisions",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "repair_command_fingerprint",
            table: "installation_identity_migration_collisions",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "repair_source_key_digest",
            table: "installation_identity_migration_collisions",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "repair_target_digest",
            table: "installation_identity_migration_collisions",
            type: "TEXT",
            maxLength: 128,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "expected_owner_version",
            table: "installation_identity_migration_collisions");

        migrationBuilder.DropColumn(
            name: "recovery_actor_id",
            table: "installation_identity_migration_collisions");

        migrationBuilder.DropColumn(
            name: "recovery_root_epoch",
            table: "installation_identity_migration_collisions");

        migrationBuilder.DropColumn(
            name: "recovery_root_public_key_fingerprint",
            table: "installation_identity_migration_collisions");

        migrationBuilder.DropColumn(
            name: "repair_command_fingerprint",
            table: "installation_identity_migration_collisions");

        migrationBuilder.DropColumn(
            name: "repair_source_key_digest",
            table: "installation_identity_migration_collisions");

        migrationBuilder.DropColumn(
            name: "repair_target_digest",
            table: "installation_identity_migration_collisions");
    }
}
