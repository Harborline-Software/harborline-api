using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// Stages an invoice <c>Draft → Issued</c> status update (the
/// <see cref="Harborline.Api.Blocks.FinancialAr.Services.PendingIssuedInvoiceUpdate"/> carried by the ambient
/// <see cref="Harborline.Api.Blocks.FinancialAr.Services.IssuedInvoiceWriteScope"/>) onto an in-flight
/// <see cref="LocalNodeDbContext"/> so it commits in the SAME SQLite transaction as the issue journal-entry
/// it records (ADR 0135 F3 — the residual JE↔AR non-atomic window).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists (ADR 0135 F3).</b> <c>InvoicePostingService.IssueAsync</c> formerly posted the issue
/// JE and then, in a separate transaction, upserted the invoice with <c>Status = Issued</c>. A crash in
/// that window left a posted JE + a stranded <c>Draft</c> invoice (no double-post, but a ledger↔AR
/// consistency edge). This enlister rides the SAME shared-unit-of-work
/// chokepoint the audit-write and recurring-idempotency enlisters use (ADR 0126 §D2): it stages the
/// invoice's <c>Draft → Issued</c> update onto the JE write's context BEFORE the single
/// <c>SaveChangesAsync</c>, so the JE row and the invoice-status row commit (or roll back) atomically — a
/// committed issue JE can never coexist with a stranded Draft. Zero signature change to
/// <c>IJournalStore</c> / <c>JournalPostingService</c> / <c>IInvoicePostingService</c>.
/// </para>
/// <para>
/// <b>Ambient hand-off (<see cref="Harborline.Api.Blocks.FinancialAr.Services.IssuedInvoiceWriteScope"/>).</b>
/// The enlister is invoked deep inside the posting pipeline
/// (<c>IssueAsync → JournalPostingService.PostAsync → NodeEfJournalStore.SaveAtomicAsync</c>), which only
/// carries the <see cref="JournalEntry"/> — not the invoice. <c>InvoicePostingService.IssueAsync</c> opens
/// an <c>IssuedInvoiceWriteScope</c> carrying the fully-built issued invoice + the stable JE
/// <c>SourceReference</c> (<c>invoice:{invoiceId}</c>) immediately before <c>PostAsync</c>; the enlister
/// reads it from the ambient scope, MATCHES it to the JE by that source reference, stages the invoice
/// <c>Update</c>; the declared registry refuses the journal save if this adapter is absent.
/// </para>
/// <para>
/// Through the Platform seam, no matching ambient scope is reported explicitly as not applicable.
/// </para>
/// </remarks>
public interface INodeIssuedInvoiceWriteEnlister
{
    /// <summary>
    /// If an ambient <c>IssuedInvoiceWriteScope</c> is active AND its pending update matches
    /// <paramref name="entry"/> (by the stable <c>invoice:{invoiceId}</c> source reference), stages the
    /// issued invoice's <c>Update</c> onto <paramref name="ctx"/> (no save), so the caller's single
    /// <c>SaveChangesAsync</c> commits the <c>Draft → Issued</c> update in
    /// the JE transaction. Otherwise a no-op.
    /// </summary>
    /// <param name="ctx">The in-flight context the JE write is staged on.</param>
    /// <param name="entry">The issue journal entry being posted.</param>
    /// <param name="ct">Cancellation token.</param>
    Task EnlistIssuedInvoiceAsync(
        LocalNodeDbContext ctx,
        JournalEntry entry,
        CancellationToken ct = default);
}
