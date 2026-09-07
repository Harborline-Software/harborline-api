using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class _20260624030425_AddWorkflowInstanceIteration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Iteration",
                table: "workflow_instances",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Iteration",
                table: "workflow_instances");
        }
    }
}
