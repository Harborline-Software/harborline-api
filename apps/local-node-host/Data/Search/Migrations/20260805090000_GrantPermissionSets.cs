using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Harborline.Api.LocalNodeHost.Data.Search;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Search.Migrations;

/// <summary>
/// Adds the PBAC bundle column to durable grants. Existing rows retain their legacy role JSON and are
/// interpreted through PermissionCompositions until the next write persists the bundle.
/// </summary>
[DbContext(typeof(NodeLocalSearchDbContext))]
[Migration("20260805090000_GrantPermissionSets")]
public partial class _20260805090000_GrantPermissionSets : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "permissions_json",
            table: "search_grants",
            type: "TEXT",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "permissions_json",
            table: "search_grants");
    }
}
