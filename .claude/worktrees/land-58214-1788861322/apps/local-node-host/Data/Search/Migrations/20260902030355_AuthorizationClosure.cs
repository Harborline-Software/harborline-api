using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Search.Migrations;

/// <inheritdoc />
public partial class _20260902030355_AuthorizationClosure : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "authorization_closure_state",
            columns: table => new
            {
                tenant_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                built_catalog_version = table.Column<long>(type: "INTEGER", nullable: false),
                built_tenant_version = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_authorization_closure_state", x => x.tenant_id);
            });

        migrationBuilder.CreateTable(
            name: "authorization_principal_atom_closure",
            columns: table => new
            {
                tenant_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                principal_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                operation = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                scope_type = table.Column<int>(type: "INTEGER", nullable: false),
                scope_value = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                role_vocabulary = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                role_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                grant_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                definition_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                valid_from_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                valid_to_unix_ms = table.Column<long>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_authorization_principal_atom_closure", x => new { x.tenant_id, x.principal_id, x.operation, x.scope_type, x.scope_value, x.role_vocabulary, x.role_name, x.grant_id, x.definition_id });
            });

        migrationBuilder.CreateIndex(
            name: "IX_authorization_principal_atom_closure_tenant_operation_scope_principal",
            table: "authorization_principal_atom_closure",
            columns: new[] { "tenant_id", "operation", "scope_value", "principal_id" });

        migrationBuilder.CreateIndex(
            name: "IX_authorization_principal_atom_closure_tenant_principal_operation_scope",
            table: "authorization_principal_atom_closure",
            columns: new[] { "tenant_id", "principal_id", "operation", "scope_value" });

        migrationBuilder.CreateIndex(
            name: "IX_authorization_principal_atom_closure_tenant_role_operation_scope",
            table: "authorization_principal_atom_closure",
            columns: new[] { "tenant_id", "role_vocabulary", "role_name", "operation", "scope_value" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "authorization_closure_state");

        migrationBuilder.DropTable(
            name: "authorization_principal_atom_closure");
    }
}
