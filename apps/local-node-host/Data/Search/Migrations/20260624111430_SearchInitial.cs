using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Search.Migrations
{
    /// <inheritdoc />
    public partial class _20260624111430_SearchInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "search_edges",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    source_record_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    target_record_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    edge_type = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_edges", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "search_nodes",
                columns: table => new
                {
                    record_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    node_type = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    body = table.Column<string>(type: "TEXT", nullable: false),
                    residency = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_nodes", x => x.record_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_search_edges_tenant_id_source_record_id",
                table: "search_edges",
                columns: new[] { "tenant_id", "source_record_id" });

            migrationBuilder.CreateIndex(
                name: "IX_search_edges_tenant_id_target_record_id",
                table: "search_edges",
                columns: new[] { "tenant_id", "target_record_id" });

            migrationBuilder.CreateIndex(
                name: "IX_search_nodes_tenant_id",
                table: "search_nodes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_search_nodes_tenant_id_node_type",
                table: "search_nodes",
                columns: new[] { "tenant_id", "node_type" });

            // ── FTS5 virtual table + sync triggers (raw DDL — EF cannot model an FTS5 virtual table) ──
            //
            // The REAL full-text index over the searchable node text. Slice 0 ships REAL FTS5, NOT the
            // in-memory substring scan ADR 0121 used. The `trigram` tokenizer (spike R-3) is REQUIRED for
            // multilingual recall: the default `unicode61` tokenizer gives ZERO CJK recall because CJK text
            // has no whitespace word boundaries; trigram indexes 3-character sequences so CJK substrings
            // match. `record_id` is an UNINDEXED column carried in the FTS row so a MATCH joins back to
            // search_nodes; only `title` + `body` are tokenized/indexed.
            migrationBuilder.Sql(
                "CREATE VIRTUAL TABLE search_fts USING fts5(" +
                "record_id UNINDEXED, title, body, tokenize = 'trigram');");

            // Keep search_fts in lock-step with search_nodes via AFTER triggers (the manual-content sync
            // pattern). The indexer writes only the ordinary search_nodes row; the FTS index follows
            // automatically — there is no path that writes FTS5 text for an unindexed node, and a deleted
            // node (G-3 OnlineOnly / GDPR-shred) leaves no FTS5 row.
            migrationBuilder.Sql(
                "CREATE TRIGGER search_nodes_ai AFTER INSERT ON search_nodes BEGIN " +
                "INSERT INTO search_fts(record_id, title, body) " +
                "VALUES (new.record_id, new.title, new.body); END;");

            migrationBuilder.Sql(
                "CREATE TRIGGER search_nodes_ad AFTER DELETE ON search_nodes BEGIN " +
                "DELETE FROM search_fts WHERE record_id = old.record_id; END;");

            migrationBuilder.Sql(
                "CREATE TRIGGER search_nodes_au AFTER UPDATE ON search_nodes BEGIN " +
                "DELETE FROM search_fts WHERE record_id = old.record_id; " +
                "INSERT INTO search_fts(record_id, title, body) " +
                "VALUES (new.record_id, new.title, new.body); END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS search_nodes_au;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS search_nodes_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS search_nodes_ai;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS search_fts;");

            migrationBuilder.DropTable(
                name: "search_edges");

            migrationBuilder.DropTable(
                name: "search_nodes");
        }
    }
}
