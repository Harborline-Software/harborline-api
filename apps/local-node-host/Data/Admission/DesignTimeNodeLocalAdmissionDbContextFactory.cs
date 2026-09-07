using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Harborline.Api.LocalNodeHost.Data.Admission;

/// <summary>
/// Design-time factory for <see cref="NodeLocalAdmissionDbContext"/>. Used by <c>dotnet ef migrations add</c> to
/// scaffold the node-local admission-token schema without a running host. Targets an in-memory SQLite database;
/// pins the dedicated migration-history table so the scaffolded migration records into
/// <c>__AdmissionMigrationsHistory</c> (not the shared financial-store table). Mirrors the roster/comms
/// design-time factories.
/// </summary>
/// <remarks>
/// Generate the migration with:
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project apps/local-node-host --context NodeLocalAdmissionDbContext
/// </code>
/// </remarks>
internal sealed class DesignTimeNodeLocalAdmissionDbContextFactory
    : IDesignTimeDbContextFactory<NodeLocalAdmissionDbContext>
{
    /// <inheritdoc />
    public NodeLocalAdmissionDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NodeLocalAdmissionDbContext>()
            .UseSqlite("Data Source=:memory:", sqlite =>
                sqlite.MigrationsHistoryTable(
                    NodeLocalAdmissionDbContext.MigrationsHistoryTableName))
            .Options;

        return new NodeLocalAdmissionDbContext(options);
    }
}
