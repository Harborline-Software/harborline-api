using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Banking.Migrations
{
    /// <inheritdoc />
    public partial class _20260616000000_BankFeedConnectionsInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bank_feed_connections",
                columns: table => new
                {
                    account_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    connected_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_feed_connections", x => x.account_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bank_feed_connections");
        }
    }
}
