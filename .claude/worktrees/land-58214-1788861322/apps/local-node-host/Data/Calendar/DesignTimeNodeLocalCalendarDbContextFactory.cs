using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Harborline.Api.LocalNodeHost.Data.Calendar;

/// <summary>
/// Design-time factory for <see cref="NodeLocalCalendarDbContext"/>. Used by
/// <c>dotnet ef migrations add</c> to scaffold the node-local calendar schema without a running host.
/// Targets an in-memory SQLite database; pins the dedicated migration-history table so the scaffolded
/// migration records into <c>__CalendarMigrationsHistory</c> (not the shared financial-store table).
/// Mirrors <c>DesignTimeNodeLocalRosterDbContextFactory</c>.
/// </summary>
/// <remarks>
/// Generate the migration with:
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project apps/local-node-host --context NodeLocalCalendarDbContext
/// </code>
/// </remarks>
internal sealed class DesignTimeNodeLocalCalendarDbContextFactory
    : IDesignTimeDbContextFactory<NodeLocalCalendarDbContext>
{
    /// <inheritdoc />
    public NodeLocalCalendarDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NodeLocalCalendarDbContext>()
            .UseSqlite("Data Source=:memory:", sqlite =>
                sqlite.MigrationsHistoryTable(
                    NodeLocalCalendarDbContext.MigrationsHistoryTableName))
            .Options;

        return new NodeLocalCalendarDbContext(options);
    }
}
