namespace Harborline.Api.Blocks.FinancialPayments.Migration;

/// <summary>
/// Source record from an ERPNext <c>Payment Entry</c> doctype. Field shape
/// mirrors the Frappe REST API so the importer can be fed directly from a
/// Frappe HTTP client OR a hand-built fixture (ADR 0100 C6 — access-mode
/// agnostic; the importer consumes only this DTO, never a live connection).
///
/// <para>
/// The importer does NOT resolve <see cref="Party"/> to a canonical
/// <c>PartyId</c> — the caller (the A4.3 orchestration pass) passes the
/// already-resolved id. This DTO carries the ERPNext party NAME only as a
/// provenance descriptor, never to drive a lookup inside the importer.
/// </para>
/// </summary>
/// <param name="Name">ERPNext <c>name</c> — stable id (e.g. <c>"PE-0001"</c>); the natural key we dedupe on.</param>
/// <param name="Modified">ERPNext <c>modified</c> — version key; an ordinal compare decides Skipped (same/older) vs Updated (newer).</param>
/// <param name="PaymentType">ERPNext <c>payment_type</c>: <c>"Receive"</c> (Inbound) or <c>"Pay"</c> (Outbound). Any other value is rejected.</param>
/// <param name="ModeOfPayment">ERPNext <c>mode_of_payment</c> (e.g. <c>"Cash"</c>, <c>"Cheque"</c>, <c>"Wire Transfer"</c>); mapped to <see cref="Harborline.Api.Blocks.FinancialPayments.Models.PaymentMethod"/>.</param>
/// <param name="Party">ERPNext <c>party</c> — customer/vendor name. Provenance only; the importer does NOT resolve it (caller passes the resolved PartyId).</param>
/// <param name="PostingDate">ERPNext <c>posting_date</c> — the payment date.</param>
/// <param name="PaidAmount">ERPNext <c>paid_amount</c> — gross payment amount. Must be &gt; 0.</param>
/// <param name="UnallocatedAmount">ERPNext <c>unallocated_amount</c> — the portion not yet applied to any invoice/bill. Drives <c>UnappliedAmount</c>; must be in <c>[0, PaidAmount]</c>.</param>
/// <param name="Currency">ISO 4217 currency; defaults to USD when null/blank. Non-USD is rejected (multi-currency deferred to v2 — ADR 0100 §10.2).</param>
/// <param name="ReferenceNo">Optional ERPNext <c>reference_no</c> (cheque number, ACH trace id, etc.); passed through to <c>Payment.Reference</c>.</param>
/// <param name="PaidFromAccount">ERPNext <c>paid_from</c> account name; credited by the source clearing entry.</param>
/// <param name="PaidToAccount">ERPNext <c>paid_to</c> account name; debited by the source clearing entry.</param>
/// <param name="DocStatus">
/// ERPNext submission state: only <c>1</c> is source-cleared and importable. Defaults to <c>0</c>
/// (Draft) so the new <c>docstatus != 1</c> reject gate FAILS CLOSED — a caller or fixture that
/// omits the field must opt in to "already submitted" rather than inherit it. Matches
/// <c>MariaDbDumpExtractor</c>, which already defaults the absent column to <c>0</c>.
/// </param>
public sealed record ErpnextPaymentSource(
    string Name,
    string Modified,
    string PaymentType,
    string? ModeOfPayment,
    string Party,
    DateOnly PostingDate,
    decimal PaidAmount,
    decimal UnallocatedAmount,
    string? Currency,
    string? ReferenceNo,
    string? PaidFromAccount = null,
    string? PaidToAccount = null,
    int DocStatus = 0);
