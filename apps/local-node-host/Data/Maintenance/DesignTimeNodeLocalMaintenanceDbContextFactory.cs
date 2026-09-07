using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Harborline.Api.LocalNodeHost.Data.Maintenance;

/// <summary>
/// Design-time factory for <see cref="NodeLocalMaintenanceDbContext"/>. Used by
/// <c>dotnet ef migrations add</c> to scaffold the node-local maintenance schema
/// without a running host. Targets an in-memory SQLite database; pins the
/// dedicated migration-history table so the scaffolded migration records into
/// <c>__MaintenanceMigrationsHistory</c> (not the shared financial-store table).
/// </summary>
/// <remarks>
/// Generate the migration with:
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project apps/local-node-host --context NodeLocalMaintenanceDbContext
/// </code>
/// </remarks>
internal sealed class DesignTimeNodeLocalMaintenanceDbContextFactory
    : IDesignTimeDbContextFactory<NodeLocalMaintenanceDbContext>
{
    /// <inheritdoc />
    public NodeLocalMaintenanceDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NodeLocalMaintenanceDbContext>()
            .UseSqlite("Data Source=:memory:", sqlite =>
                sqlite.MigrationsHistoryTable(
                    NodeLocalMaintenanceDbContext.MigrationsHistoryTableName))
            .Options;

        return new NodeLocalMaintenanceDbContext(options);
    }
}
