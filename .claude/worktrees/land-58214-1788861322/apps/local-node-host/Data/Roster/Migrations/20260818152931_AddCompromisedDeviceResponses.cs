using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Roster.Migrations;

/// <inheritdoc />
public partial class _20260818152931_AddCompromisedDeviceResponses : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "compromised_device_responses",
            columns: table => new
            {
                correlation_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                payload_json = table.Column<string>(type: "TEXT", nullable: false),
                signer_public_key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                signed_at = table.Column<long>(type: "INTEGER", nullable: false),
                signing_nonce = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                signature = table.Column<byte[]>(type: "BLOB", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_compromised_device_responses", x => x.correlation_id);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "compromised_device_responses");
    }
}
