using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Harborline.Api.LocalNodeHost.Data.Scheduling;

/// <summary>
/// Design-time factory for <see cref="NodeLocalSchedulingDbContext"/>. It keeps
/// migration scaffolding on Scheduling's dedicated history table without
/// composing or starting the local-node host.
/// </summary>
internal sealed class DesignTimeNodeLocalSchedulingDbContextFactory
    : IDesignTimeDbContextFactory<NodeLocalSchedulingDbContext>
{
    /// <inheritdoc />
    public NodeLocalSchedulingDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NodeLocalSchedulingDbContext>()
            .UseSqlite("Data Source=:memory:", sqlite =>
                sqlite.MigrationsHistoryTable(
                    NodeLocalSchedulingDbContext.MigrationsHistoryTableName))
            .Options;

        return new NodeLocalSchedulingDbContext(options);
    }
}
