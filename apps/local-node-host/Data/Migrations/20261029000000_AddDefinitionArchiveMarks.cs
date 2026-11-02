using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace Harborline.Api.LocalNodeHost.Data.Migrations;

public partial class AddDefinitionArchiveMarks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateTable(
            name: "definition_archive_marks",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "TEXT", nullable: false),
                Kind = table.Column<int>(type: "INTEGER", nullable: false),
                DefinitionKey = table.Column<string>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_definition_archive_marks", row => new { row.TenantId, row.Kind, row.DefinitionKey }));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "definition_archive_marks");
}
