using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.FinancialLedger.Models;

/// <summary>
/// A legal entity (LLC / corporation / partnership / sole-prop) that owns one
/// or more charts of accounts. Promoted to a first-class master per ADR 0104
/// §2.1 so the chart-per-entity dimension and the consolidation/combination
/// ownership graph have a stable anchor.
/// </summary>
/// <remarks>
/// Per ADR 0104 §7 (0104-2) every legal entity is tenant-keyed by contract:
/// the master implements <see cref="IMustHaveTenant"/> rather than relying on
/// <see cref="ChartOfAccounts"/>'s soft precedent. Persistence adapters reject
/// writes with a default <see cref="TenantId"/>.
///
/// Audit envelope (ADR 0104 §7 X-AUDIT): this record carries only the
/// tenant key and lifecycle timestamps inline — NO inline CRDT / signed-event
/// payload — matching the cluster precedent set by <see cref="JournalEntry"/>
/// and <see cref="ChartOfAccounts"/>. Emission of the signed-domain-event +
/// version-vector audit envelope is a durable mutation-layer concern, deferred
/// to the financial persistence hand-off exactly as it is deferred for those
/// two masters. See the W0-1 PR description for the on-the-record rationale.
/// </remarks>
public sealed record LegalEntity(
    LegalEntityId Id,
    TenantId TenantId,
    string LegalName,
    EntityKind Kind,
    TaxClassification TaxClassification,
    string? CommonControlGroupId,
    Instant CreatedAtUtc,
    Instant UpdatedAtUtc) : IMustHaveTenant;
