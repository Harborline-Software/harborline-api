using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Drafts;

/// <summary>
/// Applies the <c>form_drafts</c> schema into the keyed (SQLCipher-encrypted) <c>local-node.db</c>
/// via <see cref="NodeLocalDraftsDbContext"/>'s own migration-history table (ADR 0135 amendment
/// 2026-07-01 — D2). A thin, additive migrator (mirrors the other node-exclusive contexts) so the
/// Registered after <c>LocalNodeStoreEncryptionGuard</c>, so the main store key has already been
/// verified. Its exact one-context ownership comes from the immutable exclusive-context catalog.
/// </summary>
internal sealed class NodeDraftsMigrator : IHostedService
{
    private readonly IReadOnlyList<ILocalNodeExclusiveContextMigrator> _migrators;
    private readonly ILogger<NodeDraftsMigrator> _logger;

    /// <summary>Constructs the owner over the catalog-backed migration set.</summary>
    public NodeDraftsMigrator(
        IEnumerable<ILocalNodeExclusiveContextMigrator> migrators,
        ILogger<NodeDraftsMigrator> logger)
    {
        _migrators = LocalNodeExclusiveEfContextCatalog.SelectOwnedMigrators(
            migrators,
            LocalNodeExclusiveMigrationOwner.Drafts);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var migrator in _migrators)
        {
            await migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "D2 drafts migration owner applied {ContextCount} catalog-owned context migration.",
            _migrators.Count);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
