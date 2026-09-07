using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Search.Migrations;

/// <inheritdoc />
public partial class _20260902202756_TenantScopedSearchProjectionKeys : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // One raw operation is intentional. The SQLite provider groups/reorders its synthetic table-rebuild
        // operations ahead of adjacent SqlOperations; that would create these triggers and then immediately
        // drop them during the rebuild. Keeping the rebuild + FTS recreation in one command preserves order.
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS search_nodes_au;
            DROP TRIGGER IF EXISTS search_nodes_ad;
            DROP TRIGGER IF EXISTS search_nodes_ai;
            DROP TABLE IF EXISTS search_fts;

            CREATE TABLE search_nodes_tenant_key (
                tenant_id TEXT NOT NULL,
                record_id TEXT NOT NULL,
                node_type TEXT NOT NULL,
                title TEXT NOT NULL,
                body TEXT NOT NULL,
                residency INTEGER NOT NULL,
                CONSTRAINT PK_search_nodes PRIMARY KEY (tenant_id, record_id));
            INSERT INTO search_nodes_tenant_key (tenant_id, record_id, node_type, title, body, residency)
                SELECT tenant_id, record_id, node_type, title, body, residency FROM search_nodes;
            DROP TABLE search_nodes;
            ALTER TABLE search_nodes_tenant_key RENAME TO search_nodes;
            CREATE INDEX IX_search_nodes_tenant_id ON search_nodes (tenant_id);
            CREATE INDEX IX_search_nodes_tenant_id_node_type ON search_nodes (tenant_id, node_type);

            CREATE TABLE search_vec_rows_tenant_key (
                tenant_id TEXT NOT NULL,
                record_id TEXT NOT NULL,
                subject_id TEXT NOT NULL,
                model TEXT NOT NULL,
                model_version TEXT NOT NULL,
                dimension INTEGER NOT NULL,
                encrypted_embedding BLOB NOT NULL,
                embedding_nonce BLOB NOT NULL,
                key_version INTEGER NOT NULL,
                residency INTEGER NOT NULL,
                CONSTRAINT PK_search_vec_rows PRIMARY KEY (tenant_id, record_id));
            INSERT INTO search_vec_rows_tenant_key
                (tenant_id, record_id, subject_id, model, model_version, dimension,
                 encrypted_embedding, embedding_nonce, key_version, residency)
                SELECT tenant_id, record_id, subject_id, model, model_version, dimension,
                       encrypted_embedding, embedding_nonce, key_version, residency FROM search_vec_rows;
            DROP TABLE search_vec_rows;
            ALTER TABLE search_vec_rows_tenant_key RENAME TO search_vec_rows;
            CREATE INDEX IX_search_vec_rows_tenant_id ON search_vec_rows (tenant_id);
            CREATE INDEX IX_search_vec_rows_tenant_id_subject_id ON search_vec_rows (tenant_id, subject_id);

            CREATE VIRTUAL TABLE search_fts USING fts5(
                tenant_id UNINDEXED, record_id UNINDEXED, title, body, tokenize = 'trigram');
            INSERT INTO search_fts(tenant_id, record_id, title, body)
                SELECT tenant_id, record_id, title, body FROM search_nodes;
            CREATE TRIGGER search_nodes_ai AFTER INSERT ON search_nodes BEGIN
                INSERT INTO search_fts(tenant_id, record_id, title, body)
                VALUES (new.tenant_id, new.record_id, new.title, new.body); END;
            CREATE TRIGGER search_nodes_ad AFTER DELETE ON search_nodes BEGIN
                DELETE FROM search_fts
                WHERE tenant_id = old.tenant_id AND record_id = old.record_id; END;
            CREATE TRIGGER search_nodes_au AFTER UPDATE ON search_nodes BEGIN
                DELETE FROM search_fts
                WHERE tenant_id = old.tenant_id AND record_id = old.record_id;
                INSERT INTO search_fts(tenant_id, record_id, title, body)
                VALUES (new.tenant_id, new.record_id, new.title, new.body); END;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // This downgrade fails safely on duplicate record ids rather than discarding either tenant's row.
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS search_nodes_au;
            DROP TRIGGER IF EXISTS search_nodes_ad;
            DROP TRIGGER IF EXISTS search_nodes_ai;
            DROP TABLE IF EXISTS search_fts;

            CREATE TABLE search_nodes_record_key (
                record_id TEXT NOT NULL CONSTRAINT PK_search_nodes PRIMARY KEY,
                tenant_id TEXT NOT NULL,
                node_type TEXT NOT NULL,
                title TEXT NOT NULL,
                body TEXT NOT NULL,
                residency INTEGER NOT NULL);
            INSERT INTO search_nodes_record_key (record_id, tenant_id, node_type, title, body, residency)
                SELECT record_id, tenant_id, node_type, title, body, residency FROM search_nodes;
            DROP TABLE search_nodes;
            ALTER TABLE search_nodes_record_key RENAME TO search_nodes;
            CREATE INDEX IX_search_nodes_tenant_id ON search_nodes (tenant_id);
            CREATE INDEX IX_search_nodes_tenant_id_node_type ON search_nodes (tenant_id, node_type);

            CREATE TABLE search_vec_rows_record_key (
                record_id TEXT NOT NULL CONSTRAINT PK_search_vec_rows PRIMARY KEY,
                tenant_id TEXT NOT NULL,
                subject_id TEXT NOT NULL,
                model TEXT NOT NULL,
                model_version TEXT NOT NULL,
                dimension INTEGER NOT NULL,
                encrypted_embedding BLOB NOT NULL,
                embedding_nonce BLOB NOT NULL,
                key_version INTEGER NOT NULL,
                residency INTEGER NOT NULL);
            INSERT INTO search_vec_rows_record_key
                (record_id, tenant_id, subject_id, model, model_version, dimension,
                 encrypted_embedding, embedding_nonce, key_version, residency)
                SELECT record_id, tenant_id, subject_id, model, model_version, dimension,
                       encrypted_embedding, embedding_nonce, key_version, residency FROM search_vec_rows;
            DROP TABLE search_vec_rows;
            ALTER TABLE search_vec_rows_record_key RENAME TO search_vec_rows;
            CREATE INDEX IX_search_vec_rows_tenant_id ON search_vec_rows (tenant_id);
            CREATE INDEX IX_search_vec_rows_tenant_id_subject_id ON search_vec_rows (tenant_id, subject_id);

            CREATE VIRTUAL TABLE search_fts USING fts5(
                record_id UNINDEXED, title, body, tokenize = 'trigram');
            INSERT INTO search_fts(record_id, title, body)
                SELECT record_id, title, body FROM search_nodes;
            CREATE TRIGGER search_nodes_ai AFTER INSERT ON search_nodes BEGIN
                INSERT INTO search_fts(record_id, title, body)
                VALUES (new.record_id, new.title, new.body); END;
            CREATE TRIGGER search_nodes_ad AFTER DELETE ON search_nodes BEGIN
                DELETE FROM search_fts WHERE record_id = old.record_id; END;
            CREATE TRIGGER search_nodes_au AFTER UPDATE ON search_nodes BEGIN
                DELETE FROM search_fts WHERE record_id = old.record_id;
                INSERT INTO search_fts(record_id, title, body)
                VALUES (new.record_id, new.title, new.body); END;
            """);
    }
}
