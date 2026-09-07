using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

internal sealed class DesignTimeNodeLocalInstallationIdentityDbContextFactory
    : IDesignTimeDbContextFactory<NodeLocalInstallationIdentityDbContext>
{
    public NodeLocalInstallationIdentityDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NodeLocalInstallationIdentityDbContext>()
            .UseSqlite("Data Source=:memory:", sqlite =>
                sqlite.MigrationsHistoryTable(
                    NodeLocalInstallationIdentityDbContext.MigrationsHistoryTableName))
            .Options;

        return new NodeLocalInstallationIdentityDbContext(options);
    }
}
