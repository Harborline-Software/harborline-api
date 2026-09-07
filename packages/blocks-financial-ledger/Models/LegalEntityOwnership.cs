using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.FinancialLedger.Models;

/// <summary>
/// A directed ownership edge in the entity graph: <see cref="ParentEntityId"/>
/// owns <see cref="OwnershipPercent"/> of <see cref="OwnedEntityId"/>. The graph
/// drives consolidation scope (ADR 0104 §5): transitive ownership from a root
/// yields the consolidated subsidiaries.
/// </summary>
/// <remarks>
/// Per ADR 0104 §7 (0104-2) every ownership edge is tenant-keyed by contract
/// (<see cref="IMustHaveTenant"/>); both endpoints must belong to the same
/// tenant. The audit-envelope posture matches <see cref="LegalEntity"/>:
/// tenant key + timestamps inline, signed-event/CRDT emission deferred to the
/// durable mutation layer.
/// </remarks>
public sealed record LegalEntityOwnership(
    LegalEntityOwnershipId Id,
    TenantId TenantId,
    LegalEntityId ParentEntityId,
    LegalEntityId OwnedEntityId,
    decimal OwnershipPercent,
    Instant CreatedAtUtc,
    Instant UpdatedAtUtc) : IMustHaveTenant;
