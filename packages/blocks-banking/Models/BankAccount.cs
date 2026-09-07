using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Integrations.Payments;
using Harborline.Api.Foundation.Lifecycle;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.Banking.Models;

/// <summary>
/// A bank, credit-card, or cash account master.
/// Per ADR 0112 Part 1 §1 — account model.
/// </summary>
/// <remarks>
/// <para>
/// <strong>IArchivable placement (ADR 0108 Option 1 — marker on record).</strong>
/// <c>BankAccount</c> is net-new and naturally carries a real "closed-at" timestamp,
/// so the canonical per-entity rule (admiral-ruling-2026-06-03T1455Z) places
/// <see cref="IArchivable"/> + stored <see cref="ArchivedAt"/> directly on the
/// domain record — NOT the boundary-adapter form.  A closed account is archived
/// (recoverable, history intact), never hard-deleted.
/// </para>
/// <para>
/// <strong>IArchivable.ArchivedAt bridge note:</strong>
/// <see cref="IArchivable"/> declares <c>DateTimeOffset? ArchivedAt</c> (BCL,
/// dependency-free per ADR 0108 F1). This record stores the same concept as
/// <see cref="Instant"/>? — the internal <c>Instant</c> type wraps
/// <see cref="DateTimeOffset"/> and converts implicitly, so the interface is
/// satisfied via the explicit interface member below without a second field.
/// </para>
/// <para>
/// <strong>Stored-timestamp convention:</strong> all timestamps on this record
/// use <see cref="Instant"/> (the fleet's DateTimeOffset-backed value type) to
/// match the financial-cluster convention established by
/// <c>blocks-financial-periods</c> (ADR 0112 net-arch C3).
/// </para>
/// <para>
/// <strong>Audit envelope:</strong> no inline CRDT / signed-event payload.
/// Audit is satisfied at the durable mutation layer (ADR 0104 §7 / ADR 0112).
/// </para>
/// </remarks>
public sealed record BankAccount(
    BankAccountId Id,
    TenantId TenantId,
    EntityId EntityId,
    BankAccountKind Kind,
    string DisplayName,
    string? InstitutionName,
    CurrencyCode Currency,
    LedgerAccountRef LinkedLedgerAccount,
    decimal OpeningBalance,
    Instant CutoverAsOf,
    Instant? ArchivedAt,
    Instant CreatedAtUtc,
    Instant UpdatedAtUtc) : IMustHaveTenant, IArchivable
{
    // IArchivable.ArchivedAt is DateTimeOffset? — Instant implicitly converts.
    DateTimeOffset? IArchivable.ArchivedAt => ArchivedAt.HasValue
        ? (DateTimeOffset)ArchivedAt.Value
        : null;
}
