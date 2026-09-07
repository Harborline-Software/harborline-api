using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Admission.Migrations;

/// <inheritdoc />
public partial class _20260724123053_AdmissionAddPairingInviteBindings : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "pairing_invite_bindings",
            columns: table => new
            {
                token_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                bound_party_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                session_correlation_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, defaultValue: ""),
                membership_json = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_pairing_invite_bindings", x => x.token_id);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // L4: destructive by design. Rolling this context back deletes every unredeemed web-pairing binding, while
        // the separately migrated roster context may still retain admission provenance. Operators must back up the
        // node store and roll the admission + roster histories as one maintenance operation.
        migrationBuilder.DropTable(
            name: "pairing_invite_bindings");
    }
}
