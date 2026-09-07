using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Search.Migrations;

/// <inheritdoc />
public partial class _20260714104253_GrantFreshnessRows : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "owner_version",
            table: "search_grants",
            type: "INTEGER",
            nullable: false,
            defaultValue: 1L);

        migrationBuilder.CreateTable(
            name: "search_grant_authorization_epochs",
            columns: table => new
            {
                tenant_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                principal_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                authorization_epoch = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_search_grant_authorization_epochs", x => new { x.tenant_id, x.principal_id });
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "search_grant_authorization_epochs");

        migrationBuilder.DropColumn(
            name: "owner_version",
            table: "search_grants");
    }
}
