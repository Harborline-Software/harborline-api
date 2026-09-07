using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Roster.Migrations;

/// <inheritdoc />
public partial class _20260626215943_RosterAddXWingPublicKey : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "xwing_public_key",
            table: "roster_records",
            type: "TEXT",
            maxLength: 2048,
            nullable: false,
            defaultValue: "");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "xwing_public_key",
            table: "roster_records");
    }
}
