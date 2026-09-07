using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class _20260623210653_AddWorkflowEngineTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workflow_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InstanceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Seq = table.Column<long>(type: "INTEGER", nullable: false),
                    Step = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    data_json = table.Column<string>(type: "TEXT", nullable: false),
                    OccurredAt = table.Column<string>(type: "TEXT", maxLength: 33, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "workflow_instances",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DefinitionKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DefinitionVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CurrentStep = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    state_json = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", maxLength: 33, nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", maxLength: 33, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_instances", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "workflow_step_idempotency",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    InstanceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Step = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Iteration = table.Column<int>(type: "INTEGER", nullable: false),
                    result_json = table.Column<string>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<string>(type: "TEXT", maxLength: 33, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_step_idempotency", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "ux_workflow_events_instance_seq",
                table: "workflow_events",
                columns: new[] { "InstanceId", "Seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workflow_instances_tenant_id",
                table: "workflow_instances",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_instances_tenant_status",
                table: "workflow_instances",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_step_idempotency_instance",
                table: "workflow_step_idempotency",
                column: "InstanceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workflow_events");

            migrationBuilder.DropTable(
                name: "workflow_instances");

            migrationBuilder.DropTable(
                name: "workflow_step_idempotency");
        }
    }
}
