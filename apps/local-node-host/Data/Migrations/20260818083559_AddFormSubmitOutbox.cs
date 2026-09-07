using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Migrations;

/// <inheritdoc />
public partial class _20260818083559_AddFormSubmitOutbox : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "form_submit_outbox",
            columns: table => new
            {
                Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                EntryId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                FormId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                InstanceId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ActorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                SubmittedAt = table.Column<string>(type: "TEXT", maxLength: 33, nullable: false),
                submitted_values_json = table.Column<string>(type: "TEXT", nullable: false),
                CaseRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                LastError = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_form_submit_outbox", x => x.Sequence);
            });

        migrationBuilder.CreateIndex(
            name: "ix_form_submit_outbox_tenant_state_sequence",
            table: "form_submit_outbox",
            columns: new[] { "TenantId", "State", "Sequence" });

        migrationBuilder.CreateIndex(
            name: "ux_form_submit_outbox_entry_id",
            table: "form_submit_outbox",
            column: "EntryId",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "form_submit_outbox");
    }
}
