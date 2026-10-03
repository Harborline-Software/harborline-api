using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace Harborline.Api.LocalNodeHost.Data.Migrations;
/// <inheritdoc />
public partial class _20261003000000_AddFormSubmitProjectionDefinition : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(name: "ProjectionDefinitionJson", table: "form_submit_outbox", type: "TEXT", nullable: true);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "ProjectionDefinitionJson", table: "form_submit_outbox");
}
