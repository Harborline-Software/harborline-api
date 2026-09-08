using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Identity.Migrations;

/// <inheritdoc />
public partial class _20260728043828_LegacyBearerCutoverEvidence : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "legacy_bearer_revocation_digest",
            table: "installation_identity_cutover_state",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.UpdateData(
            table: "installation_identity_cutover_state",
            keyColumn: "singleton_key",
            keyValue: "installation-identity-authority",
            column: "legacy_bearer_revocation_digest",
            value: null);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "legacy_bearer_revocation_digest",
            table: "installation_identity_cutover_state");
    }
}
