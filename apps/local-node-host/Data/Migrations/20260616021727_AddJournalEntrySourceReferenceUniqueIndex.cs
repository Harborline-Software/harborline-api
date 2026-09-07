using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class _20260616021727_AddJournalEntrySourceReferenceUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ux_journal_entries_tenant_source_ref",
                table: "journal_entries",
                columns: new[] { "TenantId", "SourceReference" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_journal_entries_tenant_source_ref",
                table: "journal_entries");
        }
    }
}
