using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.FinancialLedger.Services;

/// <summary>
/// Stores the legal-entity master and the ownership graph, and resolves
/// consolidation scope from that graph (ADR 0104 §2.1, §5). All reads and
/// writes are tenant-scoped; cross-tenant access is impossible by construction.
/// </summary>
public interface ILegalEntityRepository
{
    /// <summary>Persists a new legal entity. Rejects a default tenant.</summary>
    Task AddEntityAsync(LegalEntity entity, CancellationToken cancellationToken = default);

    /// <summary>Gets a single entity within the tenant, or null if not found.</summary>
    Task<LegalEntity?> GetEntityAsync(TenantId tenantId, LegalEntityId id, CancellationToken cancellationToken = default);

    /// <summary>Lists all entities in the tenant.</summary>
    Task<IReadOnlyList<LegalEntity>> ListEntitiesAsync(TenantId tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists an ownership edge. Both endpoints must already exist in the same
    /// tenant; self-ownership is rejected; <see cref="LegalEntityOwnership.OwnershipPercent"/>
    /// must be in (0, 100].
    /// </summary>
    Task AddOwnershipAsync(LegalEntityOwnership ownership, CancellationToken cancellationToken = default);

    /// <summary>Lists all ownership edges in the tenant.</summary>
    Task<IReadOnlyList<LegalEntityOwnership>> ListOwnershipsAsync(TenantId tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the consolidation/combination scope rooted at
    /// <paramref name="rootEntityId"/> from the ownership graph (ADR 0104 §5):
    /// transitive owned subsidiaries are <see cref="ConsolidationPresentation.Consolidated"/>;
    /// common-control siblings with no ownership edge are
    /// <see cref="ConsolidationPresentation.Combined"/>. Throws if the root is
    /// not in the tenant.
    /// </summary>
    Task<ConsolidationScope> GetConsolidationScopeAsync(TenantId tenantId, LegalEntityId rootEntityId, CancellationToken cancellationToken = default);
}
