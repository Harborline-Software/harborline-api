using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Harborline.Api.LocalNodeHost.Data.Leases;

/// <summary>
/// Design-time factory for <see cref="NodeLocalLeaseDbContext"/>. Used by
/// <c>dotnet ef migrations add</c> to scaffold the node-local lease schema
/// without a running host. Targets an in-memory SQLite database; pins the
/// dedicated migration-history table so the scaffolded migration records into
/// <c>__LeaseMigrationsHistory</c>.
/// </summary>
/// <remarks>
/// Generate the migration with:
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project apps/local-node-host --context NodeLocalLeaseDbContext
/// </code>
/// </remarks>
internal sealed class DesignTimeNodeLocalLeaseDbContextFactory
    : IDesignTimeDbContextFactory<NodeLocalLeaseDbContext>
{
    /// <inheritdoc />
    public NodeLocalLeaseDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NodeLocalLeaseDbContext>()
            .UseSqlite("Data Source=:memory:", sqlite =>
                sqlite.MigrationsHistoryTable(
                    NodeLocalLeaseDbContext.MigrationsHistoryTableName))
            .Options;

        return new NodeLocalLeaseDbContext(options);
    }
}
