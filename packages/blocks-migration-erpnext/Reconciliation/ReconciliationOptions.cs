namespace Harborline.Api.Blocks.Migration.Erpnext.Reconciliation;

/// <summary>
/// Configuration for <see cref="ErpnextReconciliationPass"/> (importer Pass 5; spec §4.5).
/// </summary>
/// <remarks>
/// Pass 5 is heuristic-matching unapplied payments to open invoices/bills by
/// (party + date-within-window + exact-amount). The date window is configurable
/// per the spec; a ±7-day default is the spec's stated baseline (§4.5 step 2).
/// </remarks>
public sealed record ReconciliationOptions
{
    /// <summary>
    /// Maximum days between a payment's <c>PaymentDate</c> and an invoice/bill's
    /// <c>IssueDate</c>/<c>BillDate</c> for the row to be a match candidate. The
    /// spec calls for ±7 days as the baseline; CO may widen for slow-settling A/R.
    /// </summary>
    public int DateWindowDays { get; init; } = 7;

    /// <summary>The default options (±7 days) per spec §4.5.</summary>
    public static ReconciliationOptions Default { get; } = new();
}
