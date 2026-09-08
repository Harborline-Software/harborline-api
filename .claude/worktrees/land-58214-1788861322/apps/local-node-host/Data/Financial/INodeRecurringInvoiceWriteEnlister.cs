using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// Stages a recurring-invoice idempotency record (the schedule's
/// <see cref="RecurringInvoiceSchedule.GeneratedInvoices"/> map entry for an occurrence) onto an
/// in-flight <see cref="LocalNodeDbContext"/> so it commits in the SAME SQLite transaction as the
/// issue journal-entry it records (bug-1337 / ADR 0135 SC1 = no double-post on crash-resume).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists (bug-1337).</b> Before this enlister, <c>NodeEfRecurringInvoiceService</c>
/// committed the issue JE in <c>IssueAsync</c>'s own transaction and the idempotency record (the
/// <c>generated_invoices_json</c> update) in a SEPARATE transaction at loop-end. A crash in that window
/// left a posted JE with NO idempotency record — so a resume re-posted the occurrence. This enlister
/// rides the SAME shared-unit-of-work mechanism the audit append uses
/// (<see cref="INodeAuditWriteEnlister"/>, ADR 0126 §D2): it stages the schedule's idempotency mutation
/// onto the JE write's context BEFORE the single <c>SaveChangesAsync</c>, so the JE row and the
/// idempotency record commit (or roll back) atomically — a committed JE can never lack its idempotency
/// record. Zero signature change to <c>IJournalStore</c> / <c>JournalPostingService</c>.
/// </para>
/// <para>
/// <b>Ambient hand-off (<see cref="RecurringInvoiceWriteScope"/>).</b> The enlister is invoked deep
/// inside the posting pipeline (<c>IssueAsync → JournalPostingService.PostAsync →
/// NodeEfJournalStore.SaveAtomicAsync</c>), which only carries the <see cref="JournalEntry"/> — not the
/// schedule. <c>NodeEfRecurringInvoiceService</c> opens a <see cref="RecurringInvoiceWriteScope"/>
/// carrying the pending <c>(scheduleId, occurrenceDate, invoiceId)</c> immediately before calling
/// <c>IssueAsync</c>; the enlister reads it from the ambient scope, MATCHES it to the JE by the stable
/// <c>SourceReference</c> (<c>invoice:{invoiceId}</c>), stages the schedule update, and the single save
/// commits both. The scope is <see cref="AsyncLocal{T}"/>-backed so it flows down the single logical
/// async operation and is safe under the service's singleton lifetime.
/// </para>
/// <para>
/// The declared journal operation requires this adapter. With no matching ambient scope, the Platform
/// seam reports not applicable; an absent registration refuses the save.
/// </para>
/// </remarks>
public interface INodeRecurringInvoiceWriteEnlister
{
    /// <summary>
    /// If an ambient <see cref="RecurringInvoiceWriteScope"/> is active AND its pending record matches
    /// <paramref name="entry"/> (by the stable <c>invoice:{invoiceId}</c> source reference), loads the
    /// schedule on <paramref name="ctx"/>, applies its idempotency-map update for the pending occurrence,
    /// and STAGES it (no save) so the caller's single <c>SaveChangesAsync</c> commits the schedule update
    /// in the JE transaction. Otherwise a no-op.
    /// </summary>
    /// <param name="ctx">The in-flight context the JE write is staged on.</param>
    /// <param name="entry">The issue journal entry being posted.</param>
    /// <param name="ct">Cancellation token.</param>
    Task EnlistRecurringIdempotencyAsync(
        LocalNodeDbContext ctx,
        JournalEntry entry,
        CancellationToken ct = default);
}

/// <summary>
/// The pending recurring-invoice idempotency record an in-flight generation step wants co-committed with
/// its issue JE. Matched to the JE by <see cref="SourceReference"/> (<c>invoice:{InvoiceId}</c>).
/// </summary>
/// <param name="ScheduleId">The schedule whose <c>GeneratedInvoices</c> map gains the occurrence entry.</param>
/// <param name="TenantId">The schedule's tenant (defence-in-depth load predicate).</param>
/// <param name="OccurrenceDate">The occurrence date keyed into the idempotency map.</param>
/// <param name="InvoiceId">The deterministic invoice id for this occurrence (the map value + the
/// stable JE source-reference lever).</param>
/// <param name="SourceReference">The JE source reference (<c>invoice:{InvoiceId}</c>) used to match the
/// pending record to the JE the posting service emits.</param>
/// <param name="ActAt">The originating authorization act instant persisted as the schedule cursor.</param>
public sealed record PendingRecurringIdempotencyRecord(
    RecurringInvoiceScheduleId ScheduleId,
    TenantId TenantId,
    DateOnly OccurrenceDate,
    InvoiceId InvoiceId,
    string SourceReference,
    DateTimeOffset ActAt);

/// <summary>
/// Ambient (<see cref="AsyncLocal{T}"/>) context holder for the single pending recurring-invoice idempotency
/// record across the <c>IssueAsync → PostAsync → SaveAtomicAsync</c> call chain. The recurring service
/// opens one with <see cref="Enter"/> in a <c>using</c> immediately before <c>IssueAsync</c>; the
/// enlister reads <see cref="Current"/> inside <c>SaveAtomicAsync</c>.
/// </summary>
public sealed class RecurringInvoiceWriteScope : IDisposable
{
    private static readonly AsyncLocal<PendingRecurringIdempotencyRecord?> _current = new();

    private readonly PendingRecurringIdempotencyRecord? _previous;
    private bool _disposed;

    private RecurringInvoiceWriteScope(PendingRecurringIdempotencyRecord pending)
    {
        _previous = _current.Value;
        _current.Value = pending;
    }

    /// <summary>The pending record for the in-flight issue, or <see langword="null"/> when none is active.</summary>
    public static PendingRecurringIdempotencyRecord? Current => _current.Value;

    /// <summary>
    /// Opens an ambient scope carrying <paramref name="pending"/>. Dispose restores the prior value
    /// (nested scopes are well-behaved; in practice the recurring service opens one per occurrence).
    /// </summary>
    public static RecurringInvoiceWriteScope Enter(PendingRecurringIdempotencyRecord pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        return new RecurringInvoiceWriteScope(pending);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _current.Value = _previous;
    }
}
