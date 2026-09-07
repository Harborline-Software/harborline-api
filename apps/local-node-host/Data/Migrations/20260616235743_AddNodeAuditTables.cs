using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class _20260616235743_AddNodeAuditTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "node_audit_events",
                columns: table => new
                {
                    AuditId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    OccurredAt = table.Column<string>(type: "TEXT", maxLength: 33, nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    payload_json = table.Column<string>(type: "TEXT", nullable: false),
                    PrevHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    signature = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_node_audit_events", x => x.AuditId);
                });

            migrationBuilder.CreateTable(
                name: "node_audit_signature_epochs",
                columns: table => new
                {
                    EpochId = table.Column<long>(type: "INTEGER", nullable: false),
                    BoundaryAt = table.Column<string>(type: "TEXT", maxLength: 33, nullable: false),
                    IssuerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    sealed_public_key = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_node_audit_signature_epochs", x => x.EpochId);
                });

            migrationBuilder.CreateIndex(
                name: "ix_node_audit_events_tenant_correlation",
                table: "node_audit_events",
                columns: new[] { "TenantId", "CorrelationId" });

            migrationBuilder.CreateIndex(
                name: "ix_node_audit_events_tenant_event_type",
                table: "node_audit_events",
                columns: new[] { "TenantId", "EventType" });

            migrationBuilder.CreateIndex(
                name: "ix_node_audit_events_tenant_id",
                table: "node_audit_events",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ix_node_audit_events_tenant_occurred",
                table: "node_audit_events",
                columns: new[] { "TenantId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "ix_node_audit_signature_epochs_boundary",
                table: "node_audit_signature_epochs",
                column: "BoundaryAt");

            migrationBuilder.CreateIndex(
                name: "ix_node_audit_signature_epochs_issuer",
                table: "node_audit_signature_epochs",
                column: "IssuerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "node_audit_events");

            migrationBuilder.DropTable(
                name: "node_audit_signature_epochs");
        }
    }
}
