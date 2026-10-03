using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Migrations;

/// <inheritdoc />
public partial class _20261002221104_AddBankAccountCreateKeys : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "bank_account_create_keys",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Principal = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                AccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Response = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<string>(type: "TEXT", maxLength: 33, nullable: false),
                ExpiresAt = table.Column<string>(type: "TEXT", maxLength: 33, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_bank_account_create_keys", x => new { x.TenantId, x.Principal, x.Key });
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "bank_account_create_keys");
    }
}
