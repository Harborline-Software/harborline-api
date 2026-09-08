using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

internal sealed class DesignTimeNodeLocalWebSessionDbContextFactory
    : IDesignTimeDbContextFactory<NodeLocalWebSessionDbContext>
{
    public NodeLocalWebSessionDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NodeLocalWebSessionDbContext>()
            .UseSqlite("Data Source=:memory:", sqlite =>
                sqlite.MigrationsHistoryTable(NodeLocalWebSessionDbContext.MigrationsHistoryTableName))
            .Options;

        return new NodeLocalWebSessionDbContext(options);
    }
}
