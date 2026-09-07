using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Harborline.Api.LocalNodeHost.Data.Drafts;

/// <summary>
/// Design-time factory for <see cref="NodeLocalDraftsDbContext"/>. Used by
/// <c>dotnet ef migrations add</c> to scaffold the D2 <c>form_drafts</c> schema without a
/// running host. Targets an in-memory SQLite database; pins the dedicated migration-history
/// table so the scaffolded migration records into <c>__DraftsMigrationsHistory</c> (not the
/// shared financial-store table). Mirrors <c>DesignTimeNodeLocalPayrollDbContextFactory</c>.
/// </summary>
/// <remarks>
/// Generate the migration with:
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project apps/local-node-host --context NodeLocalDraftsDbContext
/// </code>
/// </remarks>
internal sealed class DesignTimeNodeLocalDraftsDbContextFactory
    : IDesignTimeDbContextFactory<NodeLocalDraftsDbContext>
{
    /// <inheritdoc />
    public NodeLocalDraftsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NodeLocalDraftsDbContext>()
            .UseSqlite("Data Source=:memory:", sqlite =>
                sqlite.MigrationsHistoryTable(
                    NodeLocalDraftsDbContext.MigrationsHistoryTableName))
            .Options;

        return new NodeLocalDraftsDbContext(options);
    }
}
