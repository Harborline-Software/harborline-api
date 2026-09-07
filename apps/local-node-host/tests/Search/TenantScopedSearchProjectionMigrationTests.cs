using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Tests.Search;

public sealed class TenantScopedSearchProjectionMigrationTests
{
    private const string PreviousMigration = "20260902120000_AuthorizationClosureOwnerVersion";

    [Fact]
    public async Task Tenant_key_upgrade_preserves_rows_backfills_fts_and_keeps_triggers_in_sync()
    {
        await using var store = await SearchTestStore.CreateAtMigrationAsync(PreviousMigration);
        await using (var before = store.CreateContext())
        {
            await before.Database.ExecuteSqlRawAsync("""
                INSERT INTO search_nodes(record_id, tenant_id, node_type, title, body, residency)
                VALUES ('record-a', 'tenant-a', 'calendar', 'Alpha harbor', 'first migration token', 0),
                       ('record-b', 'tenant-b', 'calendar', 'Bravo harbor', 'second migration token', 1);
                INSERT INTO search_vec_rows
                    (record_id, tenant_id, subject_id, model, model_version, dimension,
                     encrypted_embedding, embedding_nonce, key_version, residency)
                VALUES ('vec-a', 'tenant-a', 'subject-a', 'model-a', 'v1', 1, X'01', X'02', 1, 0),
                       ('vec-b', 'tenant-b', 'subject-b', 'model-b', 'v1', 1, X'03', X'04', 1, 1);
                """);
        }

        await using var after = store.CreateContext();
        await after.Database.MigrateAsync();

        var nodes = await after.Nodes.AsNoTracking()
            .OrderBy(row => row.TenantId)
            .Select(row => new { row.TenantId, row.RecordId, row.Title })
            .ToArrayAsync();
        Assert.Equal(2, nodes.Length);
        Assert.Contains(nodes, row => row.TenantId == "tenant-a" && row.RecordId == "record-a");
        Assert.Contains(nodes, row => row.TenantId == "tenant-b" && row.RecordId == "record-b");

        var vectors = await after.VecRows.AsNoTracking()
            .OrderBy(row => row.TenantId)
            .Select(row => new { row.TenantId, row.RecordId, row.SubjectId })
            .ToArrayAsync();
        Assert.Equal(2, vectors.Length);
        Assert.Contains(vectors, row => row.TenantId == "tenant-a" && row.RecordId == "vec-a");
        Assert.Contains(vectors, row => row.TenantId == "tenant-b" && row.RecordId == "vec-b");

        Assert.Equal(1, await FtsCountAsync(after, "tenant-a", "alpha"));
        Assert.Equal(1, await FtsCountAsync(after, "tenant-b", "bravo"));
        Assert.Equal(0, await FtsCountAsync(after, "tenant-a", "bravo"));

        await after.Database.ExecuteSqlRawAsync("""
            UPDATE search_nodes SET title = 'Charlie harbor', body = 'updated trigger token'
            WHERE tenant_id = 'tenant-a' AND record_id = 'record-a';
            DELETE FROM search_nodes
            WHERE tenant_id = 'tenant-b' AND record_id = 'record-b';
            """);

        Assert.Equal(0, await FtsCountAsync(after, "tenant-a", "alpha"));
        Assert.Equal(1, await FtsCountAsync(after, "tenant-a", "charlie"));
        Assert.Equal(0, await FtsCountAsync(after, "tenant-b", "bravo"));
        Assert.Single(await after.Nodes.AsNoTracking().ToArrayAsync());
    }

    private static async Task<long> FtsCountAsync(
        Data.Search.NodeLocalSearchDbContext context,
        string tenant,
        string query)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM search_fts WHERE tenant_id = $tenant AND search_fts MATCH $query;";
        var tenantParameter = command.CreateParameter();
        tenantParameter.ParameterName = "$tenant";
        tenantParameter.Value = tenant;
        command.Parameters.Add(tenantParameter);
        var queryParameter = command.CreateParameter();
        queryParameter.ParameterName = "$query";
        queryParameter.Value = query;
        command.Parameters.Add(queryParameter);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
