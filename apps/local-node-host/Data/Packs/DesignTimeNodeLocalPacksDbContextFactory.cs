using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Harborline.Api.LocalNodeHost.Data.Packs;

/// <summary>
/// Design-time factory for <see cref="NodeLocalPacksDbContext"/>. Used by <c>dotnet ef migrations add</c> to
/// scaffold the durable pack-install schema without a running host. Targets an in-memory SQLite database; pins
/// the dedicated migration-history table so the scaffolded migration records into <c>__PacksMigrationsHistory</c>
/// (not the shared financial-store table). Mirrors the admission / roster / comms design-time factories.
/// </summary>
/// <remarks>
/// Generate the migration with:
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project apps/local-node-host --context NodeLocalPacksDbContext \
///   --output-dir Data/Packs/Migrations
/// </code>
/// </remarks>
internal sealed class DesignTimeNodeLocalPacksDbContextFactory
    : IDesignTimeDbContextFactory<NodeLocalPacksDbContext>
{
    /// <inheritdoc />
    public NodeLocalPacksDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NodeLocalPacksDbContext>()
            .UseSqlite("Data Source=:memory:", sqlite =>
                sqlite.MigrationsHistoryTable(
                    NodeLocalPacksDbContext.MigrationsHistoryTableName))
            .Options;

        return new NodeLocalPacksDbContext(options);
    }
}
