using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Harborline.Api.LocalNodeHost.Data.Roster;

/// <summary>
/// Design-time factory for <see cref="NodeLocalRosterDbContext"/>. Used by <c>dotnet ef migrations add</c> to
/// scaffold the node-local roster schema without a running host. Targets an in-memory SQLite database; pins
/// the dedicated migration-history table so the scaffolded migration records into
/// <c>__RosterMigrationsHistory</c> (not the shared financial-store table). Mirrors the comms design-time
/// factory.
/// </summary>
/// <remarks>
/// Generate the migration with:
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project apps/local-node-host --context NodeLocalRosterDbContext
/// </code>
/// </remarks>
internal sealed class DesignTimeNodeLocalRosterDbContextFactory
    : IDesignTimeDbContextFactory<NodeLocalRosterDbContext>
{
    /// <inheritdoc />
    public NodeLocalRosterDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NodeLocalRosterDbContext>()
            .UseSqlite("Data Source=:memory:", sqlite =>
                sqlite.MigrationsHistoryTable(
                    NodeLocalRosterDbContext.MigrationsHistoryTableName))
            .Options;

        return new NodeLocalRosterDbContext(options);
    }
}
