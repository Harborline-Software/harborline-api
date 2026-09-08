using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Harborline.Api.LocalNodeHost.Data.Search;

/// <summary>
/// Design-time factory for <see cref="NodeLocalSearchDbContext"/> so <c>dotnet ef migrations add</c> can
/// construct the context outside the host. Mirrors the other node-exclusive context factories
/// (<c>DesignTimeNodeLocalCommsDbContextFactory</c> et al.). Uses a throwaway SQLite connection string —
/// the design-time tool only needs the model shape, not a real keyed store.
/// </summary>
public sealed class DesignTimeNodeLocalSearchDbContextFactory
    : IDesignTimeDbContextFactory<NodeLocalSearchDbContext>
{
    /// <inheritdoc />
    public NodeLocalSearchDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NodeLocalSearchDbContext>()
            .UseSqlite(
                "Data Source=design-time-search.db",
                sqlite => sqlite.MigrationsHistoryTable(
                    NodeLocalSearchDbContext.MigrationsHistoryTableName))
            .Options;

        return new NodeLocalSearchDbContext(options);
    }
}
