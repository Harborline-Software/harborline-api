using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Roster.Migrations;

/// <inheritdoc />
public partial class _20260724122428_RosterAddAdmissionProvenance : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "admitted_via_token_id",
            table: "roster_records",
            type: "TEXT",
            maxLength: 256,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "minting_session_evidence",
            table: "roster_records",
            type: "TEXT",
            maxLength: 256,
            nullable: false,
            defaultValue: "");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // L4: destructive by design. This erases signed-admission provenance from every roster row. The admission
        // context has a separate migration history, so rollback must be coordinated with it from a verified backup;
        // a partial rollback leaves an audit-incomplete pairing state.
        migrationBuilder.DropColumn(
            name: "admitted_via_token_id",
            table: "roster_records");

        migrationBuilder.DropColumn(
            name: "minting_session_evidence",
            table: "roster_records");
    }
}
