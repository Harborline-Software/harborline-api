using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Migrations.Drafts;

/// <inheritdoc />
public partial class _20260701175903_AddSubmissionDraftTable : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "form_drafts",
            columns: table => new
            {
                tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                case_id = table.Column<string>(type: "TEXT", nullable: false),
                party_id = table.Column<string>(type: "TEXT", nullable: false),
                form_id = table.Column<string>(type: "TEXT", nullable: false),
                schema_ref = table.Column<string>(type: "TEXT", nullable: false),
                definition_id = table.Column<string>(type: "TEXT", nullable: false),
                definition_version = table.Column<string>(type: "TEXT", nullable: false),
                engine_version = table.Column<string>(type: "TEXT", nullable: false),
                locale_chain_json = table.Column<string>(type: "TEXT", nullable: false),
                body = table.Column<byte[]>(type: "BLOB", nullable: false),
                subject_id = table.Column<string>(type: "TEXT", nullable: true),
                created_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                updated_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                expires_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_form_drafts", x => new { x.tenant_id, x.case_id, x.party_id });
            });

        migrationBuilder.CreateIndex(
            name: "ix_form_drafts_expires_at",
            table: "form_drafts",
            column: "expires_at_utc");

        migrationBuilder.CreateIndex(
            name: "ix_form_drafts_tenant_party",
            table: "form_drafts",
            columns: new[] { "tenant_id", "party_id" });

        migrationBuilder.CreateIndex(
            name: "ix_form_drafts_tenant_subject",
            table: "form_drafts",
            columns: new[] { "tenant_id", "subject_id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "form_drafts");
    }
}
