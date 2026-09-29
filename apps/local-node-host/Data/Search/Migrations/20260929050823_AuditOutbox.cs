using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Search.Migrations;

/// <inheritdoc />
public partial class _20260929050823_AuditOutbox : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "search_audit_outbox",
            columns: table => new
            {
                audit_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                tenant_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                event_type = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                occurred_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                nonce = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                body_json = table.Column<string>(type: "TEXT", nullable: false),
                actor = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                target_kind = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                target_id = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                target_scope = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                act = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                authority_snapshot_json = table.Column<string>(type: "TEXT", nullable: true),
                published_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true),
                attempts = table.Column<int>(type: "INTEGER", nullable: false),
                last_error = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_search_audit_outbox", x => x.audit_id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_search_audit_outbox_published",
            table: "search_audit_outbox",
            column: "published_at_unix_ms");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "search_audit_outbox");
    }
}
