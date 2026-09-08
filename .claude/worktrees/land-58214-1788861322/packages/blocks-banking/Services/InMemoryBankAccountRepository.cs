using System.Collections.Concurrent;
using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Banking.Services;

/// <summary>
/// In-memory <see cref="IBankAccountRepository"/> for testing and development.
/// </summary>
public sealed class InMemoryBankAccountRepository : IBankAccountRepository, IBankAccountMutationRepository
{
    private readonly ConcurrentDictionary<(TenantId, BankAccountId), BankAccount> _store = new();

    /// <inheritdoc />
    public Task<BankAccount?> GetByIdAsync(TenantId tenantId, BankAccountId id, CancellationToken ct = default)
        => Task.FromResult<BankAccount?>(_store.TryGetValue((tenantId, id), out var a) ? a : null);

    /// <inheritdoc />
    public Task<IReadOnlyList<BankAccount>> ListAsync(
        TenantId tenantId, bool includeArchived = false, CancellationToken ct = default)
    {
        IReadOnlyList<BankAccount> result = _store.Values
            .Where(a => a.TenantId == tenantId && (includeArchived || a.ArchivedAt is null))
            .ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task AddAsync(BankAccount account, CancellationToken ct = default)
    {
        _store[(account.TenantId, account.Id)] = account;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateAsync(BankAccount account, CancellationToken ct = default)
    {
        _store[(account.TenantId, account.Id)] = account;
        return Task.CompletedTask;
    }
}
