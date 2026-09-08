using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Harborline.Api.LocalNodeHost.Data.Banking;

/// <summary>
/// Design-time factory for <see cref="NodeLocalBankFeedDbContext"/>. Used by
/// <c>dotnet ef migrations add</c> to scaffold the node-local bank-feed-connections
/// schema without a running host. Targets an in-memory SQLite database; pins the
/// dedicated migration-history table so the scaffolded migration records into
/// <c>__BankFeedMigrationsHistory</c> (not the shared financial-store table).
/// </summary>
/// <remarks>
/// Generate the migration with:
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project apps/local-node-host --context NodeLocalBankFeedDbContext
/// </code>
/// </remarks>
internal sealed class DesignTimeNodeLocalBankFeedDbContextFactory
    : IDesignTimeDbContextFactory<NodeLocalBankFeedDbContext>
{
    /// <inheritdoc />
    public NodeLocalBankFeedDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NodeLocalBankFeedDbContext>()
            .UseSqlite("Data Source=:memory:", sqlite =>
                sqlite.MigrationsHistoryTable(
                    NodeLocalBankFeedDbContext.MigrationsHistoryTableName))
            .Options;

        return new NodeLocalBankFeedDbContext(options);
    }
}
