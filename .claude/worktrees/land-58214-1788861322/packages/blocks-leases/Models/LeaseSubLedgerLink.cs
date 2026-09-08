using Harborline.Api.Blocks.FinancialSubLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.Leases.Models;

/// <summary>
/// PM-pack–owned link between a lease and its generic subsidiary-ledger account
/// (ADR 0120 PR-D — D3 pack-boundary pattern).
///
/// <para>
/// <b>What this is:</b> the mapping record that says "for tenant T, lease L charges
/// against sub-ledger account S." The financial core (<see cref="SubLedgerAccount"/>)
/// knows nothing about leases; the PM pack maps the domain concept onto the generic
/// primitive here.
/// </para>
///
/// <para>
/// <b>What this is NOT:</b>
/// <list type="bullet">
///   <item>A posting contract — this record carries no financial amounts.</item>
///   <item>A field on any financial block — the FK is here, NOT on
///     <see cref="SubLedgerAccount"/>, <c>Invoice</c>, or <c>Payment</c>.</item>
///   <item>An entry in the generic sub-ledger — it is a PM-pack side table.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Pack boundary:</b> the D3 arch-test in <c>blocks-financial-subledger</c>
/// ensures that the generic identity assembly contains NO <c>LeaseId</c> / <c>UnitId</c>
/// / domain field. This record is the ONE place where the boundary is deliberately
/// crossed — and it crosses in the direction pack→financial (not financial→pack).
/// </para>
///
/// <para>
/// <b>Offline read path:</b>
/// <c>lease → LeaseSubLedgerLink → SubLedgerAccountId → ISubLedgerReadModel.GetPositionAsync</c>
/// — served by the local node, fully offline. This is the read-path the whole ADR 0120
/// effort was built for: per-lease balance/history/aging without the PartyId-overmatch.
/// </para>
///
/// <para>
/// <b>C-FIN-3 (security deposit):</b> security deposits default to an unapplied AR credit.
/// The jurisdiction-segregated deposit-liability path (where segregation is required by law)
/// is a LATER pack feature decision — do not silently net deposits where segregation is
/// required. Document the intent here; the implementation is deferred.
/// </para>
/// </summary>
public sealed record LeaseSubLedgerLink : IMustHaveTenant
{
    /// <summary>Owning tenant.</summary>
    public required TenantId TenantId { get; init; }

    /// <summary>The lease this link belongs to.</summary>
    public required LeaseId LeaseId { get; init; }

    /// <summary>
    /// The generic sub-ledger account that receives this lease's AR charges
    /// (rent invoices, late fees, prorations). Stamped on invoices at creation
    /// time by the PM write-path (ADR 0120 PR-C FK).
    /// </summary>
    public required SubLedgerAccountId SubLedgerAccountId { get; init; }

    /// <summary>When this link was created (for audit + idempotency).</summary>
    public required Instant CreatedAtUtc { get; init; }
}
