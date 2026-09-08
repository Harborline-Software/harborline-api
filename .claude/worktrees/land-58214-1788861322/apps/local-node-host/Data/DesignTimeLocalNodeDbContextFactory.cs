using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Harborline.Api.Foundation.Persistence;

namespace Harborline.Api.LocalNodeHost.Data;

/// <summary>
/// Design-time factory for <see cref="LocalNodeDbContext"/>.  Used by
/// <c>dotnet ef migrations add</c> to create a context instance without a running
/// application host.  The factory targets an in-memory SQLite database so
/// migration scaffolding does not require a real database file.
/// </summary>
/// <remarks>
/// When generating a SQLite migration:
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project apps/local-node-host --context LocalNodeDbContext
/// </code>
/// EF resolves this factory automatically via the
/// <see cref="IDesignTimeDbContextFactory{TContext}"/> interface.  The
/// factory constructs the complete Pattern-A module collection explicitly so the generated
/// migration model matches the runtime <see cref="LocalNodeDbContext"/> model.
/// </remarks>
internal sealed class DesignTimeLocalNodeDbContextFactory : IDesignTimeDbContextFactory<LocalNodeDbContext>
{
    /// <inheritdoc />
    public LocalNodeDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LocalNodeDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        return new LocalNodeDbContext(options, CreateMigrationModules());
    }

    /// <summary>
    /// The complete Pattern-A module set whose runtime entities must be represented by committed
    /// <see cref="LocalNodeDbContext"/> migrations. Migration-path tests consume this same source so
    /// a copied list cannot silently drift from production scaffolding.
    /// </summary>
    internal static IHarborlineEntityModule[] CreateMigrationModules() =>
        LocalNodePatternAModuleCatalog.CreateModules();
}
