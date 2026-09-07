using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Harborline.Api.LocalNodeHost.Data.Comms;

/// <summary>
/// Design-time factory for <see cref="NodeLocalCommsDbContext"/>. Used by <c>dotnet ef migrations add</c> to
/// scaffold the node-local comms schema without a running host. Targets an in-memory SQLite database; pins
/// the dedicated migration-history table so the scaffolded migration records into
/// <c>__CommsMigrationsHistory</c> (not the shared financial-store table). Mirrors the property design-time
/// factory.
/// </summary>
/// <remarks>
/// Generate the migration with:
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project apps/local-node-host --context NodeLocalCommsDbContext
/// </code>
/// </remarks>
internal sealed class DesignTimeNodeLocalCommsDbContextFactory
    : IDesignTimeDbContextFactory<NodeLocalCommsDbContext>
{
    /// <inheritdoc />
    public NodeLocalCommsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NodeLocalCommsDbContext>()
            .UseSqlite("Data Source=:memory:", sqlite =>
                sqlite.MigrationsHistoryTable(
                    NodeLocalCommsDbContext.MigrationsHistoryTableName))
            .Options;

        return new NodeLocalCommsDbContext(options);
    }
}
