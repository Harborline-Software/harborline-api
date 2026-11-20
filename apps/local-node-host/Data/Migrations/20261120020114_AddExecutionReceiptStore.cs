using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Migrations;

/// <inheritdoc />
public partial class _20261120020114_AddExecutionReceiptStore : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "execution_receipts",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "TEXT", nullable: false),
                RunKind = table.Column<string>(type: "TEXT", nullable: false),
                RunValue = table.Column<Guid>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                AccessDecisionId = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_execution_receipts", x => new { x.TenantId, x.RunKind, x.RunValue });
            });

        migrationBuilder.CreateTable(
            name: "execution_trace_steps",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "TEXT", nullable: false),
                RunKind = table.Column<string>(type: "TEXT", nullable: false),
                RunValue = table.Column<Guid>(type: "TEXT", nullable: false),
                Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                Phase = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_execution_trace_steps", x => new { x.TenantId, x.RunKind, x.RunValue, x.Ordinal });
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "execution_receipts");

        migrationBuilder.DropTable(
            name: "execution_trace_steps");
    }
}
