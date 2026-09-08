using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Harborline.Api.LocalNodeHost.Data.Search.Migrations;

/// <summary>Records the atomic boot conversion of legacy signed admissions into grants.</summary>
[DbContext(typeof(NodeLocalSearchDbContext))]
[Migration("20260908030000_RosterAdmissionGrantBackfill")]
public sealed partial class RosterAdmissionGrantBackfill : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateTable(
        name: "roster_admission_grant_backfill",
        columns: table => new { id = table.Column<int>(type: "INTEGER", nullable: false),
            record_count = table.Column<int>(type: "INTEGER", nullable: false) },
        constraints: table => table.PrimaryKey("PK_roster_admission_grant_backfill", row => row.id));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Admission grant conversion is permanent; restore the database to undo it.");
}
