using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace Harborline.Api.LocalNodeHost.Data.Migrations;

/// <summary>
/// The content store's epoch bookkeeping (T-521/content-store-epoch-key-store, ADR 0116): each item names the epoch whose
/// key wraps its data key, and a retired epoch leaves a durable marker. The keys themselves are never in this database.
/// </summary>
public partial class AddContentStoreEpochs : Migration
{
    private static readonly string[] ItemTables = ["content_store_items", "content_store_staged"];
    private static readonly string[] EpochIdentity = ["TenantId", "EpochId"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var name in ItemTables)
        {
            // Rows written before epochs name no epoch; no shipped node holds them, since host enablement stays held.
            migrationBuilder.AddColumn<Guid>(name: "EpochId", table: name, type: "TEXT", nullable: false, defaultValue: Guid.Empty);
            migrationBuilder.CreateIndex($"IX_{name}_TenantId_EpochId", name, EpochIdentity);
        }

        migrationBuilder.CreateTable(
            name: "content_store_epochs",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "TEXT", nullable: false),
                EpochId = table.Column<Guid>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_content_store_epochs", row => new { row.TenantId, row.EpochId }));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "content_store_epochs");
        foreach (var name in ItemTables)
        {
            migrationBuilder.DropIndex(name: $"IX_{name}_TenantId_EpochId", table: name);
            migrationBuilder.DropColumn(name: "EpochId", table: name);
        }
    }
}
