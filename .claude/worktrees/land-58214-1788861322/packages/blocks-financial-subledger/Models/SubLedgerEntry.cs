using Harborline.Api.Blocks.FinancialSubLedger.Services;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.FinancialSubLedger.Models;

/// <summary>
/// One event in the chronological history of a <see cref="SubLedgerAccount"/>.
/// Returned as part of <see cref="ISubLedgerReadModel.GetHistoryAsync"/>.
///
/// <para>
/// This is a <b>VIEW</b> over source records (Invoices/Bills +
/// PaymentApplications + adjustment JEs) — no new stored entry type exists.
/// The projection assembly (PR-B) materialises these from the existing
/// domain records; this interface type lives in the LOW identity assembly so
/// packs can reference it without pulling the heavy AR/AP/Payments references.
/// </para>
/// </summary>
/// <param name="EntryDate">Date of the underlying event (issue date, payment date, etc.).</param>
/// <param name="Kind">The type of event contributing this entry.</param>
/// <param name="SourceId">
/// Opaque string id of the source record — an <c>InvoiceId</c>, <c>BillId</c>,
/// <c>PaymentId</c>, <c>JournalEntryId</c>, or similar. Cast at the call site
/// using the expected kind.
/// </param>
/// <param name="Reference">Human-readable label (invoice number, payment ref, etc.).</param>
/// <param name="Amount">
/// Gross amount of the event. Positive = debit to the sub-ledger (charge / payment
/// reduces credits). Sign follows the position convention of the parent
/// <see cref="SubLedgerAccount.Kind"/>.
/// </param>
/// <param name="RunningBalance">
/// Running balance of the sub-ledger account after this entry, in chronological order.
/// </param>
public sealed record SubLedgerEntry(
    DateOnly EntryDate,
    SubLedgerEntryKind Kind,
    string SourceId,
    string Reference,
    decimal Amount,
    decimal RunningBalance);
