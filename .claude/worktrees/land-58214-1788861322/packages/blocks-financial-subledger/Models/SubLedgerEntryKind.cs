namespace Harborline.Api.Blocks.FinancialSubLedger.Models;

/// <summary>
/// Discriminates the type of event contributing a <see cref="SubLedgerEntry"/>
/// to a sub-ledger's history.
/// </summary>
public enum SubLedgerEntryKind
{
    /// <summary>An invoice (AR) or bill (AP) was issued — opens an item.</summary>
    Charge,

    /// <summary>A payment was applied against an open item — reduces the balance.</summary>
    Payment,

    /// <summary>An invoice/bill was voided — the item leaves the open set; GL reversal keeps R1 balanced.</summary>
    Void,

    /// <summary>An invoice/bill was written off (bad-debt JE) — the item leaves the open set; GL bad-debt JE keeps R1 balanced.</summary>
    WriteOff,

    /// <summary>A credit note or adjustment was issued against an open item.</summary>
    Adjustment,
}
