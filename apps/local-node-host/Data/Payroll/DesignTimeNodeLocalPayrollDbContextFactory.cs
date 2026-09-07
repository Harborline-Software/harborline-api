using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Harborline.Api.LocalNodeHost.Data.Payroll;

/// <summary>
/// Design-time factory for <see cref="NodeLocalPayrollDbContext"/>. Used by
/// <c>dotnet ef migrations add</c> to scaffold the node-local payroll schema without a running host.
/// Targets an in-memory SQLite database; pins the dedicated migration-history table so the
/// scaffolded migration records into <c>__PayrollMigrationsHistory</c> (not the shared financial-store
/// table). Mirrors <c>DesignTimeNodeLocalBankFeedDbContextFactory</c>.
/// </summary>
/// <remarks>
/// Generate the migration with:
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project apps/local-node-host --context NodeLocalPayrollDbContext
/// </code>
/// </remarks>
internal sealed class DesignTimeNodeLocalPayrollDbContextFactory
    : IDesignTimeDbContextFactory<NodeLocalPayrollDbContext>
{
    /// <inheritdoc />
    public NodeLocalPayrollDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NodeLocalPayrollDbContext>()
            .UseSqlite("Data Source=:memory:", sqlite =>
                sqlite.MigrationsHistoryTable(
                    NodeLocalPayrollDbContext.MigrationsHistoryTableName))
            .Options;

        return new NodeLocalPayrollDbContext(options);
    }
}
