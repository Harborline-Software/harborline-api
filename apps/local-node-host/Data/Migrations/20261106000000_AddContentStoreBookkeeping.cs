using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace Harborline.Api.LocalNodeHost.Data.Migrations;

public partial class AddContentStoreBookkeeping : Migration
{
    private static readonly string[] ItemTables = ["content_store_items", "content_store_staged"];
    private static readonly string[] ItemIdentity = ["TenantId", "ContentId"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var name in ItemTables)
        {
            migrationBuilder.CreateTable(
                name: name,
                columns: table => new
                {
                    ItemKey = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", nullable: false),
                    ContentId = table.Column<string>(type: "TEXT", nullable: false),
                    Length = table.Column<long>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    WrappedDataKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    WrapNonce = table.Column<byte[]>(type: "BLOB", nullable: false),
                    WrapTag = table.Column<byte[]>(type: "BLOB", nullable: false),
                    NoncePrefix = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SegmentSize = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                },
                constraints: table => table.PrimaryKey("PK_" + name, row => row.ItemKey));
        }

        migrationBuilder.CreateIndex("IX_content_store_items_TenantId_ContentId", "content_store_items", ItemIdentity, unique: true);
        migrationBuilder.CreateIndex("IX_content_store_staged_CreatedUtcTicks", "content_store_staged", "CreatedUtcTicks");

        migrationBuilder.CreateTable(
            name: "content_store_owners",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                ItemKey = table.Column<Guid>(type: "TEXT", nullable: false),
                Record = table.Column<string>(type: "TEXT", nullable: false),
                Element = table.Column<string>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_content_store_owners", row => row.Id));
        migrationBuilder.CreateIndex("IX_content_store_owners_ItemKey", "content_store_owners", "ItemKey");

        migrationBuilder.CreateTable(
            name: "content_store_quota",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "TEXT", nullable: false),
                TenantGrain = table.Column<bool>(type: "INTEGER", nullable: false),
                Record = table.Column<string>(type: "TEXT", nullable: false),
                Bytes = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_content_store_quota", row => new { row.TenantId, row.TenantGrain, row.Record }));

        migrationBuilder.CreateTable(
            name: "content_store_reclamations",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                ContentId = table.Column<string>(type: "TEXT", nullable: false),
                TenantId = table.Column<string>(type: "TEXT", nullable: false),
                Cause = table.Column<string>(type: "TEXT", nullable: false),
                Run = table.Column<string>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_content_store_reclamations", row => row.Id));

        migrationBuilder.CreateTable(
            name: "content_store_integrity",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "TEXT", nullable: false),
                ItemKey = table.Column<Guid>(type: "TEXT", nullable: false),
                FirstSeenUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                LastSeenUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                Count = table.Column<long>(type: "INTEGER", nullable: false),
                SegmentIndex = table.Column<int>(type: "INTEGER", nullable: false),
                Active = table.Column<bool>(type: "INTEGER", nullable: false),
                TraceId = table.Column<string>(type: "TEXT", nullable: false),
                OperationId = table.Column<string>(type: "TEXT", nullable: false),
                PrincipalId = table.Column<string>(type: "TEXT", nullable: false),
                OwnerRecord = table.Column<string>(type: "TEXT", nullable: false),
                OwnerElement = table.Column<string>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_content_store_integrity", row => new { row.TenantId, row.ItemKey }));

        migrationBuilder.CreateTable(
            name: "content_store_operations",
            columns: table => new
            {
                IdempotencyKey = table.Column<string>(type: "TEXT", nullable: false),
                CommandId = table.Column<string>(type: "TEXT", nullable: false),
                Fingerprint = table.Column<string>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_content_store_operations", row => row.IdempotencyKey));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var name in ItemTables) migrationBuilder.DropTable(name);
        migrationBuilder.DropTable("content_store_owners");
        migrationBuilder.DropTable("content_store_quota");
        migrationBuilder.DropTable("content_store_reclamations");
        migrationBuilder.DropTable("content_store_integrity");
        migrationBuilder.DropTable("content_store_operations");
    }
}
