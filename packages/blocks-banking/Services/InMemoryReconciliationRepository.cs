using System.Collections.Concurrent;
using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Banking.Services;

/// <summary>
/// In-memory <see cref="IReconciliationRepository"/> for testing and development.
/// </summary>
public sealed class InMemoryReconciliationRepository : IReconciliationRepository
{
    private readonly ConcurrentDictionary<(TenantId, ReconciliationId), Reconciliation> _store = new();

    /// <inheritdoc />
    public Task<Reconciliation?> GetByIdAsync(TenantId tenantId, ReconciliationId id, CancellationToken ct = default)
        => Task.FromResult<Reconciliation?>(_store.TryGetValue((tenantId, id), out var r) ? r : null);

    /// <inheritdoc />
    public Task<Reconciliation?> GetByAccountPeriodAsync(
        TenantId tenantId, BankAccountId accountId, FiscalPeriodId periodId, CancellationToken ct = default)
    {
        var result = _store.Values.FirstOrDefault(r =>
            r.TenantId == tenantId &&
            r.AccountId == accountId &&
            r.PeriodId == periodId);
        return Task.FromResult<Reconciliation?>(result);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Reconciliation>> ListByAccountAsync(
        TenantId tenantId, BankAccountId accountId, CancellationToken ct = default)
    {
        IReadOnlyList<Reconciliation> result = _store.Values
            .Where(r => r.TenantId == tenantId && r.AccountId == accountId)
            .ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task AddAsync(Reconciliation reconciliation, CancellationToken ct = default)
    {
        _store[(reconciliation.TenantId, reconciliation.Id)] = reconciliation;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> UpdateAsync(Reconciliation reconciliation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reconciliation);
        var key = (reconciliation.TenantId, reconciliation.Id);

        while (true)
        {
            if (!_store.TryGetValue(key, out var current))
                return Task.FromResult(false);
            if (reconciliation.Version != current.Version + 1)
                return Task.FromResult(false);
            if (_store.TryUpdate(key, reconciliation, current))
                return Task.FromResult(true);
        }
    }
}
