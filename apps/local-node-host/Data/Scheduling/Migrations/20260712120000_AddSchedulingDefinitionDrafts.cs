using Microsoft.EntityFrameworkCore.Migrations;

namespace Harborline.Api.LocalNodeHost.Data.Scheduling.Migrations;

public sealed partial class AddSchedulingDefinitionDrafts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "scheduling_definition_drafts",
            columns: table => new
            {
                tenant_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                definition_id = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                revision = table.Column<int>(type: "INTEGER", nullable: false),
                definition_json = table.Column<string>(type: "TEXT", nullable: false),
                updated_by = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                updated_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_scheduling_definition_drafts",
                x => new { x.tenant_id, x.definition_id, x.revision }));
        migrationBuilder.CreateIndex(
            name: "IX_scheduling_definition_drafts_tenant_id_definition_id_revision",
            table: "scheduling_definition_drafts",
            columns: new[] { "tenant_id", "definition_id", "revision" });

        migrationBuilder.CreateTable(
            name: "scheduling_definition_draft_audit",
            columns: table => new
            {
                tenant_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                audit_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                definition_id = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                revision = table.Column<int>(type: "INTEGER", nullable: false),
                actor_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                occurred_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_scheduling_definition_draft_audit",
                x => new { x.tenant_id, x.audit_id }));
        migrationBuilder.CreateIndex(
            name: "IX_scheduling_definition_draft_audit_tenant_id_definition_id_revision",
            table: "scheduling_definition_draft_audit",
            columns: new[] { "tenant_id", "definition_id", "revision" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("scheduling_definition_draft_audit");
        migrationBuilder.DropTable("scheduling_definition_drafts");
    }
}
