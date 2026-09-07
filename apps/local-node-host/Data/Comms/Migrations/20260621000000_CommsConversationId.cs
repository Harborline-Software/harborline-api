using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Comms.Migrations
{
    /// <summary>
    /// C1 — additive conversation scope for the comms doctype. Adds the <c>conversation_id</c> column
    /// (default <c>"team"</c>, so existing team-wide rows back-fill to the well-known team channel seamlessly)
    /// + the <c>(tenant_id, conversation_id, authored_at)</c> read index. Additive + non-destructive: no
    /// existing column changes, no data loss; the team channel is preserved exactly as the back-filled
    /// conversation. Hand-authored to mirror the EF-generated shape (the repo pins EF Core 11 preview; the
    /// global <c>dotnet ef</c> is 9.x and cannot scaffold the preview model — the standing pattern is to
    /// hand-write additive column migrations against the readable initial migration).
    /// </summary>
    public partial class _20260621000000_CommsConversationId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "conversation_id",
                table: "messages",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                // Back-fill existing rows to the well-known team channel — the pre-C1 team-wide log becomes the
                // "team" conversation seamlessly (CommsConversation.TeamConversationId).
                defaultValue: "team");

            migrationBuilder.CreateIndex(
                name: "IX_messages_tenant_id_conversation_id_authored_at",
                table: "messages",
                columns: new[] { "tenant_id", "conversation_id", "authored_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_messages_tenant_id_conversation_id_authored_at",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "conversation_id",
                table: "messages");
        }
    }
}
