using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Migrations;

/// <inheritdoc />
public partial class _20260925011629_AddLayoutDenialOutbox : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "layout_denial_outbox",
            columns: table => new
            {
                Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                EntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                OccurredAt = table.Column<string>(type: "TEXT", maxLength: 33, nullable: false),
                denial_json = table.Column<string>(type: "TEXT", nullable: false),
                State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                LastError = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_layout_denial_outbox", x => x.Sequence);
            });

        migrationBuilder.CreateIndex(
            name: "ix_layout_denial_outbox_state",
            table: "layout_denial_outbox",
            column: "State");

        migrationBuilder.CreateIndex(
            name: "ux_layout_denial_outbox_entry_id",
            table: "layout_denial_outbox",
            column: "EntryId",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "layout_denial_outbox");
    }
}
