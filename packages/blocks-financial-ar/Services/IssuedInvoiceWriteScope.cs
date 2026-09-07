using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.FinancialAr.Services;

/// <summary>
/// Ambient (<see cref="AsyncLocal{T}"/>) context holder for the single pending invoice <c>Draft → Issued</c>
/// status update across the <c>IssueAsync → JournalPostingService.PostAsync → IJournalStore.SaveAtomicAsync</c>
/// call chain (ADR 0135 F3 — the residual JE↔AR non-atomic window).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists (ADR 0135 F3).</b> <see cref="InvoicePostingService.IssueAsync"/> historically did
/// two separate writes: it posted the issue JE (transaction T1, atomic with the audit row + the recurring
/// idempotency record), then SEPARATELY upserted the invoice with <c>Status = Issued</c> (transaction T2).
/// A crash between T1 and T2 left a posted JE + a STRANDED <c>Draft</c> invoice — no double-post, but a
/// ledger↔AR consistency edge the de-reviews on earlier repository ticket #1346/#1348 flagged as F3. This scope lets a
/// host that owns the JE-write transaction (the local node) co-commit the <c>Draft → Issued</c> update IN
/// the JE transaction, mirroring exactly the bug-1337 <c>RecurringInvoiceWriteScope</c> mechanism for the
/// schedule idempotency record.
/// </para>
/// <para>
/// <b>Hand-off.</b> <see cref="InvoicePostingService.IssueAsync"/> opens a scope carrying
/// the fully-built <see cref="PendingIssuedInvoiceUpdate"/> (the <c>issued</c> record + its tenant + the
/// stable JE <see cref="PendingIssuedInvoiceUpdate.SourceReference"/>) immediately before calling
/// <c>PostAsync</c>. A host enlister, invoked deep inside <c>SaveAtomicAsync</c> (which carries only the
/// <see cref="Models.Invoice"/>-free <c>JournalEntry</c>), reads <see cref="Current"/>, MATCHES it to the
/// JE by source reference and stages the invoice <c>Update</c> onto the JE write's context. The declared
/// journal operation refuses its save if that adapter is absent, so no post-commit recovery signal exists.
/// </para>
/// <para>
/// The scope is <see cref="AsyncLocal{T}"/>-backed so it flows down the single logical async operation and
/// is safe under the posting service's singleton lifetime. It is a NO-OP for every store that does not wire
/// the matching enlister.
/// </para>
/// </remarks>
public sealed class IssuedInvoiceWriteScope : IDisposable
{
    private static readonly AsyncLocal<PendingIssuedInvoiceUpdate?> _current = new();

    private readonly PendingIssuedInvoiceUpdate? _previous;
    private bool _disposed;

    private IssuedInvoiceWriteScope(PendingIssuedInvoiceUpdate pending)
    {
        _previous = _current.Value;
        _current.Value = pending;
    }

    /// <summary>The pending issued-invoice update for the in-flight issue, or <see langword="null"/> when none is active.</summary>
    public static PendingIssuedInvoiceUpdate? Current => _current.Value;

    /// <summary>
    /// Opens an ambient scope carrying <paramref name="pending"/>. Dispose restores the prior value
    /// (nested scopes are well-behaved; in practice one is opened per <c>IssueAsync</c> call, itself
    /// possibly nested inside a <c>RecurringInvoiceWriteScope</c>).
    /// </summary>
    public static IssuedInvoiceWriteScope Enter(PendingIssuedInvoiceUpdate pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        return new IssuedInvoiceWriteScope(pending);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _current.Value = _previous;
    }
}

/// <summary>
/// The pending invoice <c>Draft → Issued</c> update an in-flight <c>IssueAsync</c> wants co-committed with
/// its issue JE (ADR 0135 F3). Matched to the JE by <see cref="SourceReference"/> (<c>invoice:{Id}</c>).
/// </summary>
/// <param name="TenantId">The invoice's tenant (defence-in-depth load/assert predicate at the store).</param>
/// <param name="Issued">The fully-built issued invoice (Status = Issued, JE id, number, totals) to persist.</param>
/// <param name="SourceReference">The issue JE's source reference (<c>invoice:{Id}</c>) used to match this
/// pending update to the JE the posting service emits.</param>
public sealed record PendingIssuedInvoiceUpdate(
    TenantId TenantId,
    Invoice Issued,
    string SourceReference);
