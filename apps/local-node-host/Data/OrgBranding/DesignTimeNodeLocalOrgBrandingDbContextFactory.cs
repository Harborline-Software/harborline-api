using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Harborline.Api.LocalNodeHost.Data.OrgBranding;

/// <summary>
/// Design-time factory for <see cref="NodeLocalOrgBrandingDbContext"/>. Used by <c>dotnet ef migrations add</c>
/// to scaffold the node-local org-branding schema without a running host. Targets an in-memory SQLite
/// database; pins the dedicated migration-history table so the scaffolded migration records into
/// <c>__OrgBrandingMigrationsHistory</c> (not the shared financial-store table). Mirrors the roster / calendar
/// design-time factories.
/// </summary>
/// <remarks>
/// Generate the migration with:
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project apps/local-node-host --context NodeLocalOrgBrandingDbContext
/// </code>
/// </remarks>
internal sealed class DesignTimeNodeLocalOrgBrandingDbContextFactory
    : IDesignTimeDbContextFactory<NodeLocalOrgBrandingDbContext>
{
    /// <inheritdoc />
    public NodeLocalOrgBrandingDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NodeLocalOrgBrandingDbContext>()
            .UseSqlite("Data Source=:memory:", sqlite =>
                sqlite.MigrationsHistoryTable(
                    NodeLocalOrgBrandingDbContext.MigrationsHistoryTableName))
            .Options;

        return new NodeLocalOrgBrandingDbContext(options);
    }
}
