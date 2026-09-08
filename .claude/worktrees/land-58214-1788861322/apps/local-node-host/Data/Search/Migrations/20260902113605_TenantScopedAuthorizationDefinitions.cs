using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Search.Migrations;

/// <inheritdoc />
public partial class _20260902113605_TenantScopedAuthorizationDefinitions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "declaring_tenant_id",
            table: "authorization_capability_definitions",
            type: "TEXT",
            maxLength: 256,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "declaring_tenant_id",
            table: "authorization_capability_definitions");
    }
}
