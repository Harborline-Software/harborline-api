using System.Collections.Concurrent;
using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Banking.Services;

/// <summary>
/// In-memory <see cref="IStatementLineRepository"/> for testing and development.
/// </summary>
public sealed class InMemoryStatementLineRepository : IStatementLineRepository
{
    private readonly ConcurrentDictionary<(TenantId, StatementLineId), StatementLine> _store = new();

    /// <inheritdoc />
    public Task<StatementLine?> GetByIdAsync(TenantId tenantId, StatementLineId id, CancellationToken ct = default)
        => Task.FromResult<StatementLine?>(_store.TryGetValue((tenantId, id), out var l) ? l : null);

    /// <inheritdoc />
    public Task<IReadOnlyList<StatementLine>> ListByAccountAsync(
        TenantId tenantId, BankAccountId accountId, CancellationToken ct = default)
    {
        IReadOnlyList<StatementLine> result = _store.Values
            .Where(l => l.TenantId == tenantId && l.AccountId == accountId)
            .ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StatementLine>> ListByBatchAsync(
        TenantId tenantId, string batchId, CancellationToken ct = default)
    {
        IReadOnlyList<StatementLine> result = _store.Values
            .Where(l => l.TenantId == tenantId && l.Source.BatchId == batchId)
            .ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<StatementLine?> FindByProviderTxnIdAsync(
        TenantId tenantId, BankAccountId accountId, string providerTxnId, CancellationToken ct = default)
    {
        var result = _store.Values.FirstOrDefault(l =>
            l.TenantId == tenantId &&
            l.AccountId == accountId &&
            l.ProviderTxnId == providerTxnId);
        return Task.FromResult<StatementLine?>(result);
    }

    /// <inheritdoc />
    public Task<StatementLine?> FindByBatchOrdinalAsync(
        TenantId tenantId, BankAccountId accountId, string batchId, int ordinal, CancellationToken ct = default)
    {
        var result = _store.Values.FirstOrDefault(l =>
            l.TenantId == tenantId &&
            l.AccountId == accountId &&
            l.Source.BatchId == batchId &&
            l.Source.OrdinalWithinBatch == ordinal);
        return Task.FromResult<StatementLine?>(result);
    }

    /// <inheritdoc />
    public Task AddAsync(StatementLine line, CancellationToken ct = default)
    {
        _store[(line.TenantId, line.Id)] = line;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task AddRangeAsync(IEnumerable<StatementLine> lines, CancellationToken ct = default)
    {
        foreach (var line in lines)
            _store[(line.TenantId, line.Id)] = line;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateAsync(StatementLine line, CancellationToken ct = default)
    {
        _store[(line.TenantId, line.Id)] = line;
        return Task.CompletedTask;
    }
}
