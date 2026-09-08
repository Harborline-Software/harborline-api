using System.Collections.Concurrent;
using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Banking.Services;

/// <summary>
/// In-memory <see cref="IMatchLinkRepository"/> for testing and development.
/// </summary>
public sealed class InMemoryMatchLinkRepository : IMatchLinkRepository
{
    private readonly ConcurrentDictionary<(TenantId, MatchLinkId), MatchLink> _store = new();

    /// <inheritdoc />
    public Task<MatchLink?> GetByIdAsync(TenantId tenantId, MatchLinkId id, CancellationToken ct = default)
        => Task.FromResult<MatchLink?>(_store.TryGetValue((tenantId, id), out var l) ? l : null);

    /// <inheritdoc />
    public Task<IReadOnlyList<MatchLink>> ListByStatementLineAsync(
        TenantId tenantId, StatementLineId statementLineId, CancellationToken ct = default)
    {
        IReadOnlyList<MatchLink> result = _store.Values
            .Where(l => l.TenantId == tenantId && l.StatementLine == statementLineId)
            .ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MatchLink>> ListByLedgerTransactionAsync(
        TenantId tenantId, LedgerTransactionRef ledgerTransaction, CancellationToken ct = default)
    {
        IReadOnlyList<MatchLink> result = _store.Values
            .Where(l => l.TenantId == tenantId && l.LedgerTransaction == ledgerTransaction)
            .ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task AddAsync(MatchLink link, CancellationToken ct = default)
    {
        _store[(link.TenantId, link.Id)] = link;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateAsync(MatchLink link, CancellationToken ct = default)
    {
        _store[(link.TenantId, link.Id)] = link;
        return Task.CompletedTask;
    }
}
