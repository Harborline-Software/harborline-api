using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Migrations;

/// <summary>
/// CP-4 (ADR 0101 Rev 3.2 Wave 5 precondition 3): widens the model MaxLength of the two governed
/// <c>OriginDescription</c> columns (descriptor + quarantine) from 2048 to 6144 — they now hold the
/// tenant-DEK <c>EncryptedField</c> envelope JSON, not the prose. The SQLite store type is TEXT
/// either way, so the provider emits NO schema operation; this migration exists to keep the model
/// snapshot in sync with the module (an out-of-sync snapshot fails the pending-model-changes gate).
/// </summary>
public partial class _20260806010037_WidenSpatialFrameGovernedColumns : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {

    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {

    }
}
