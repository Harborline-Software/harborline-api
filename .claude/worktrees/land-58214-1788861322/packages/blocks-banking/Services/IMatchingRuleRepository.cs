using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Banking.Services;

/// <summary>
/// Tenant-keyed repository for <see cref="MatchingRule"/> masters.
/// Per ADR 0092 tenant-keyed repository seam + ADR 0112 Part 1 §5.
/// </summary>
/// <remarks>
/// Default list queries MUST exclude archived rules (non-null
/// <see cref="MatchingRule.ArchivedAt"/>) unless <c>includeArchived</c>
/// is explicitly set — load-bearing ADR 0108 invariant.
/// </remarks>
public interface IMatchingRuleRepository
{
    /// <summary>Returns a matching rule by id (tenant-scoped), or null if not found.</summary>
    Task<MatchingRule?> GetByIdAsync(TenantId tenantId, MatchingRuleId id, CancellationToken ct = default);

    /// <summary>
    /// Returns all matching rules for the tenant ordered by <see cref="MatchingRule.Priority"/> ascending.
    /// Excludes archived rules by default; pass <c>includeArchived = true</c> to include them.
    /// </summary>
    Task<IReadOnlyList<MatchingRule>> ListAsync(
        TenantId tenantId, bool includeArchived = false, CancellationToken ct = default);

    /// <summary>Persists a new matching rule.</summary>
    Task AddAsync(MatchingRule rule, CancellationToken ct = default);

    /// <summary>Updates a matching rule (full record replacement).</summary>
    Task UpdateAsync(MatchingRule rule, CancellationToken ct = default);
}
