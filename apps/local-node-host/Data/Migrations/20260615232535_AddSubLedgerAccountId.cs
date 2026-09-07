using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class _20260615232535_AddSubLedgerAccountId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SubLedgerAccountId",
                table: "invoices",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubLedgerAccountId",
                table: "bills",
                type: "TEXT",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SubLedgerAccountId",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "SubLedgerAccountId",
                table: "bills");
        }
    }
}
