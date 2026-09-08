using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Packs.Migrations;

/// <inheritdoc />
public partial class _20260707140050_AddFeedChannelSequences : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "feed_channel_sequences",
            columns: table => new
            {
                tenant = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                channel_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                high_water_sequence = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_feed_channel_sequences", x => new { x.tenant, x.channel_id });
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "feed_channel_sequences");
    }
}
