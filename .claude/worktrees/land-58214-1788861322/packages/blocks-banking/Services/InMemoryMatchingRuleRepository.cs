using System.Collections.Concurrent;
using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Banking.Services;

/// <summary>
/// In-memory <see cref="IMatchingRuleRepository"/> for testing and development.
/// </summary>
public sealed class InMemoryMatchingRuleRepository : IMatchingRuleRepository
{
    private readonly ConcurrentDictionary<(TenantId, MatchingRuleId), MatchingRule> _store = new();

    /// <inheritdoc />
    public Task<MatchingRule?> GetByIdAsync(TenantId tenantId, MatchingRuleId id, CancellationToken ct = default)
        => Task.FromResult<MatchingRule?>(_store.TryGetValue((tenantId, id), out var r) ? r : null);

    /// <inheritdoc />
    public Task<IReadOnlyList<MatchingRule>> ListAsync(
        TenantId tenantId, bool includeArchived = false, CancellationToken ct = default)
    {
        IReadOnlyList<MatchingRule> result = _store.Values
            .Where(r => r.TenantId == tenantId && (includeArchived || r.ArchivedAt is null))
            .OrderBy(r => r.Priority)
            .ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task AddAsync(MatchingRule rule, CancellationToken ct = default)
    {
        _store[(rule.TenantId, rule.Id)] = rule;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateAsync(MatchingRule rule, CancellationToken ct = default)
    {
        _store[(rule.TenantId, rule.Id)] = rule;
        return Task.CompletedTask;
    }
}
