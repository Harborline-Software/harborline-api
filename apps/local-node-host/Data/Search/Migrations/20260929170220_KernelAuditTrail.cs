using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Search.Migrations;

/// <inheritdoc />
public partial class _20260929170220_KernelAuditTrail : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "signed_payload_json",
            table: "search_audit_outbox",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "search_audit_trail",
            columns: table => new
            {
                audit_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                tenant_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                event_type = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                occurred_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                record_json = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_search_audit_trail", x => x.audit_id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_search_audit_trail_tenant",
            table: "search_audit_trail",
            column: "tenant_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "search_audit_trail");

        migrationBuilder.DropColumn(
            name: "signed_payload_json",
            table: "search_audit_outbox");
    }
}
