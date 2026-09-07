using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Harborline.Api.LocalNodeHost.Data.Properties;

/// <summary>
/// Design-time factory for <see cref="NodeLocalPropertyDbContext"/>. Used by
/// <c>dotnet ef migrations add</c> to scaffold the node-local property schema
/// without a running host. Targets an in-memory SQLite database; pins the
/// dedicated migration-history table so the scaffolded migration records into
/// <c>__PropertyMigrationsHistory</c> (not the shared financial-store table).
/// </summary>
/// <remarks>
/// Generate the migration with:
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project apps/local-node-host --context NodeLocalPropertyDbContext
/// </code>
/// </remarks>
internal sealed class DesignTimeNodeLocalPropertyDbContextFactory
    : IDesignTimeDbContextFactory<NodeLocalPropertyDbContext>
{
    /// <inheritdoc />
    public NodeLocalPropertyDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NodeLocalPropertyDbContext>()
            .UseSqlite("Data Source=:memory:", sqlite =>
                sqlite.MigrationsHistoryTable(
                    NodeLocalPropertyDbContext.MigrationsHistoryTableName))
            .Options;

        return new NodeLocalPropertyDbContext(options);
    }
}
