using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Search.Migrations;

[DbContext(typeof(NodeLocalSearchDbContext))]
[Migration("20260902120000_AuthorizationClosureOwnerVersion")]
public sealed class AuthorizationClosureOwnerVersion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "grant_owner_version",
            table: "authorization_principal_atom_closure",
            type: "INTEGER",
            nullable: false,
            defaultValue: 1L);

        migrationBuilder.Sql("""
            UPDATE authorization_principal_atom_closure
            SET grant_owner_version = (
                SELECT owner_version
                FROM search_grants
                WHERE search_grants.tenant_id = authorization_principal_atom_closure.tenant_id
                  AND search_grants.grant_id = authorization_principal_atom_closure.grant_id);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(
            name: "grant_owner_version",
            table: "authorization_principal_atom_closure");
}
