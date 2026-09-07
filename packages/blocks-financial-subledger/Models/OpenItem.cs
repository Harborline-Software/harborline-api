using Harborline.Api.Blocks.FinancialSubLedger.Services;

namespace Harborline.Api.Blocks.FinancialSubLedger.Models;

/// <summary>
/// Represents a single open item (unpaid or partially-paid invoice/bill) on a
/// <see cref="SubLedgerAccount"/>. Contributes to the derived position via
/// its <see cref="Balance"/> field (which already nets discount and write-off
/// through <c>AmountPaid</c> — C-FIN-1).
///
/// <para>
/// Voided and WrittenOff items are NOT open items — they have left the
/// open-item set; the corresponding GL reversal / bad-debt JE keeps the
/// R1 reconciliation invariant balanced on both sides simultaneously (C-FIN-2).
/// </para>
/// </summary>
/// <param name="SourceId">Opaque id of the underlying invoice or bill.</param>
/// <param name="SourceNumber">Human-readable document number (e.g. <c>INV-2026-05-01-AB-0042</c>).</param>
/// <param name="IssueDate">Date the item was issued.</param>
/// <param name="DueDate">Date the item is due.</param>
/// <param name="Total">Original total of the item (Subtotal + Tax).</param>
/// <param name="AmountPaid">
/// Cumulative amount applied — includes cash, discount, and write-off.
/// Mirrors the source record's <c>AmountPaid</c> field.
/// </param>
/// <param name="Balance">
/// Open balance: <c>Total − AmountPaid</c>. This is the value that
/// rolls up into <see cref="SubLedgerPosition.OpenItemBalance"/>.
/// </param>
/// <param name="IsOverdue">
/// <c>true</c> when the item is open and past its <see cref="DueDate"/>
/// relative to the position's <c>asOf</c> date.
/// </param>
public sealed record OpenItem(
    string SourceId,
    string SourceNumber,
    DateOnly IssueDate,
    DateOnly DueDate,
    decimal Total,
    decimal AmountPaid,
    decimal Balance,
    bool IsOverdue);
