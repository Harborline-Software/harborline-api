using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Search.Migrations;

/// <inheritdoc />
public partial class _20260902010100_AuthorizationStoreOfRecord : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Clean break approved by ticket 204: legacy permission-shaped rows cannot be assigned truthful
        // role, provenance, review, or revocation evidence, so discard them instead of fabricating a backfill.
        migrationBuilder.Sql("DELETE FROM search_grant_authorization_epochs;");
        migrationBuilder.Sql("DELETE FROM search_grants;");

        migrationBuilder.DropIndex(
            name: "IX_search_grants_tenant_id_source_reference",
            table: "search_grants");

        migrationBuilder.DropColumn(
            name: "permissions_json",
            table: "search_grants");

        migrationBuilder.DropColumn(
            name: "roles_json",
            table: "search_grants");

        migrationBuilder.DropColumn(
            name: "scope_json",
            table: "search_grants");

        migrationBuilder.RenameColumn(
            name: "principal_id",
            table: "search_grants",
            newName: "subject_id");

        migrationBuilder.RenameIndex(
            name: "IX_search_grants_tenant_id_principal_id",
            table: "search_grants",
            newName: "IX_search_grants_tenant_id_subject_id");

        migrationBuilder.AddColumn<string>(
            name: "approver",
            table: "search_grants",
            type: "TEXT",
            maxLength: 256,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<int>(
            name: "granter_kind",
            table: "search_grants",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<long>(
            name: "last_reviewed_at_unix_ms",
            table: "search_grants",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<string>(
            name: "last_reviewed_by",
            table: "search_grants",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "validity_changed_by",
            table: "search_grants",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "validity_change_reason_code",
            table: "search_grants",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "validity_change_reason_reference",
            table: "search_grants",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "reason_code",
            table: "search_grants",
            type: "TEXT",
            maxLength: 64,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "reason_reference",
            table: "search_grants",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "revocation_reason_code",
            table: "search_grants",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "revocation_reason_reference",
            table: "search_grants",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "revoked_by",
            table: "search_grants",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "role_name",
            table: "search_grants",
            type: "TEXT",
            maxLength: 128,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "role_vocabulary",
            table: "search_grants",
            type: "TEXT",
            maxLength: 128,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<int>(
            name: "scope_type",
            table: "search_grants",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "scope_value",
            table: "search_grants",
            type: "TEXT",
            maxLength: 512,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<int>(
            name: "source",
            table: "search_grants",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "status",
            table: "search_grants",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.CreateTable(
            name: "authorization_binding_revisions",
            columns: table => new
            {
                tenant_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                definition_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                revision = table.Column<long>(type: "INTEGER", nullable: false),
                changed_by = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                changed_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                reason = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                warning = table.Column<int>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_authorization_binding_revisions", x => new { x.tenant_id, x.definition_id, x.revision });
            });

        migrationBuilder.CreateTable(
            name: "authorization_binding_roles",
            columns: table => new
            {
                tenant_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                definition_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                revision = table.Column<long>(type: "INTEGER", nullable: false),
                vocabulary = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                role_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_authorization_binding_roles", x => new { x.tenant_id, x.definition_id, x.revision, x.vocabulary, x.role_name });
            });

        migrationBuilder.CreateTable(
            name: "authorization_capability_definitions",
            columns: table => new
            {
                definition_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                revision = table.Column<long>(type: "INTEGER", nullable: false),
                publisher_package_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                operation = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                scope_type = table.Column<int>(type: "INTEGER", nullable: false),
                scope_value = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_authorization_capability_definitions", x => new { x.definition_id, x.revision });
            });

        migrationBuilder.CreateTable(
            name: "authorization_capability_offered_roles",
            columns: table => new
            {
                definition_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                revision = table.Column<long>(type: "INTEGER", nullable: false),
                vocabulary = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                role_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_authorization_capability_offered_roles", x => new { x.definition_id, x.revision, x.vocabulary, x.role_name });
            });

        migrationBuilder.CreateTable(
            name: "authorization_catalog_version",
            columns: table => new
            {
                id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                version = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_authorization_catalog_version", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "authorization_roles",
            columns: table => new
            {
                vocabulary = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                role_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                role_definition_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                display_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                owner_kind = table.Column<int>(type: "INTEGER", nullable: false),
                owner_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                is_sealed = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_authorization_roles", x => new { x.vocabulary, x.role_name });
            });

        migrationBuilder.CreateTable(
            name: "authorization_tenant_versions",
            columns: table => new
            {
                tenant_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                version = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_authorization_tenant_versions", x => x.tenant_id);
            });

        migrationBuilder.InsertData(
            table: "authorization_catalog_version",
            columns: new[] { "id", "version" },
            values: new object[] { 1, 0L });

        migrationBuilder.CreateIndex(
            name: "IX_search_grants_tenant_id_source_reference",
            table: "search_grants",
            columns: new[] { "tenant_id", "source_reference" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "authorization_binding_revisions");

        migrationBuilder.DropTable(
            name: "authorization_binding_roles");

        migrationBuilder.DropTable(
            name: "authorization_capability_definitions");

        migrationBuilder.DropTable(
            name: "authorization_capability_offered_roles");

        migrationBuilder.DropTable(
            name: "authorization_catalog_version");

        migrationBuilder.DropTable(
            name: "authorization_roles");

        migrationBuilder.DropTable(
            name: "authorization_tenant_versions");

        migrationBuilder.DropIndex(
            name: "IX_search_grants_tenant_id_source_reference",
            table: "search_grants");

        migrationBuilder.DropColumn(
            name: "approver",
            table: "search_grants");

        migrationBuilder.DropColumn(
            name: "granter_kind",
            table: "search_grants");

        migrationBuilder.DropColumn(
            name: "last_reviewed_at_unix_ms",
            table: "search_grants");

        migrationBuilder.DropColumn(
            name: "last_reviewed_by",
            table: "search_grants");

        migrationBuilder.DropColumn(
            name: "validity_changed_by",
            table: "search_grants");

        migrationBuilder.DropColumn(
            name: "validity_change_reason_code",
            table: "search_grants");

        migrationBuilder.DropColumn(
            name: "validity_change_reason_reference",
            table: "search_grants");

        migrationBuilder.DropColumn(
            name: "reason_code",
            table: "search_grants");

        migrationBuilder.DropColumn(
            name: "reason_reference",
            table: "search_grants");

        migrationBuilder.DropColumn(
            name: "revocation_reason_code",
            table: "search_grants");

        migrationBuilder.DropColumn(
            name: "revocation_reason_reference",
            table: "search_grants");

        migrationBuilder.DropColumn(
            name: "revoked_by",
            table: "search_grants");

        migrationBuilder.DropColumn(
            name: "role_name",
            table: "search_grants");

        migrationBuilder.DropColumn(
            name: "role_vocabulary",
            table: "search_grants");

        migrationBuilder.DropColumn(
            name: "scope_type",
            table: "search_grants");

        migrationBuilder.DropColumn(
            name: "scope_value",
            table: "search_grants");

        migrationBuilder.DropColumn(
            name: "source",
            table: "search_grants");

        migrationBuilder.DropColumn(
            name: "status",
            table: "search_grants");

        migrationBuilder.RenameColumn(
            name: "subject_id",
            table: "search_grants",
            newName: "principal_id");

        migrationBuilder.RenameIndex(
            name: "IX_search_grants_tenant_id_subject_id",
            table: "search_grants",
            newName: "IX_search_grants_tenant_id_principal_id");

        migrationBuilder.AddColumn<string>(
            name: "permissions_json",
            table: "search_grants",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "roles_json",
            table: "search_grants",
            type: "TEXT",
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "scope_json",
            table: "search_grants",
            type: "TEXT",
            nullable: false,
            defaultValue: "");

        migrationBuilder.CreateIndex(
            name: "IX_search_grants_tenant_id_source_reference",
            table: "search_grants",
            columns: new[] { "tenant_id", "source_reference" });
    }
}
