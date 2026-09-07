using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Comms.Migrations
{
    /// <inheritdoc />
    public partial class _20260620133344_CommsInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "messages",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    author_party_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    author_issuer_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    body = table.Column<string>(type: "TEXT", nullable: false),
                    nonce = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    signature = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    authored_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_messages_tenant_id",
                table: "messages",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_messages_tenant_id_authored_at",
                table: "messages",
                columns: new[] { "tenant_id", "authored_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "messages");
        }
    }
}
