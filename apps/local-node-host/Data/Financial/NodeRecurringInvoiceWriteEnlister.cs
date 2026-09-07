using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Coordination;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// Default <see cref="INodeRecurringInvoiceWriteEnlister"/>. Stages the schedule's idempotency-map update
/// for the ambient in-flight occurrence onto the JE write's <see cref="LocalNodeDbContext"/> so the two
/// commit in one SQLite transaction (bug-1337 / ADR 0135 SC1 = no double-post on crash-resume).
/// </summary>
/// <remarks>
/// <para>
/// <b>Match-by-source-reference.</b> The enlister only acts when an ambient
/// <see cref="RecurringInvoiceWriteScope"/> is active AND its pending record's
/// <see cref="PendingRecurringIdempotencyRecord.SourceReference"/> equals the JE's
/// <see cref="JournalEntry.SourceReference"/>. That guards against staging an idempotency update for the
/// wrong JE if the posting pipeline ever posts more than one entry under a single scope (it does not
/// today — one issue == one JE — but the guard keeps the enlister correct by construction).
/// </para>
/// <para>
/// <b>Loads + stages on the SAME ctx.</b> It loads the schedule on <paramref name="ctx"/> (tracked,
/// <c>IgnoreQueryFilters</c> + explicit tenant predicate — the node's defence-in-depth boundary), applies
/// <see cref="RecurringInvoiceSchedule.RecordGeneratedInvoice"/>, and explicitly marks the converted
/// <c>generated_invoices_json</c> property modified (a value-converted reference property is not flagged
/// by reference-equality change detection). It does NOT call <c>SaveChangesAsync</c> — the caller's single
/// save in <see cref="NodeEfJournalStore.SaveAtomicAsync"/> commits the JE row, the audit row, and this
/// schedule update together.
/// </para>
/// </remarks>
public sealed class NodeRecurringInvoiceWriteEnlister : INodeRecurringInvoiceWriteEnlister, IWriteEnlistment
{
    /// <inheritdoc />
    public WriteInvariant Invariant => NodeWriteInvariants.RecurringInvoice;

    /// <inheritdoc />
    public async ValueTask<WriteEnlistmentOutcome> EnlistAsync(
        StagedWriteUnitOfWork unitOfWork,
        CancellationToken cancellationToken = default)
    {
        if (unitOfWork is not NodeJournalWriteUnitOfWork nodeWrite)
        {
            return WriteEnlistmentOutcome.NotApplicable;
        }

        var pending = RecurringInvoiceWriteScope.Current;
        if (pending is null || !string.Equals(
                pending.SourceReference,
                nodeWrite.Entry.SourceReference,
                StringComparison.Ordinal))
        {
            return WriteEnlistmentOutcome.NotApplicable;
        }

        await EnlistRecurringIdempotencyAsync(nodeWrite.Context, nodeWrite.Entry, cancellationToken)
            .ConfigureAwait(false);
        return WriteEnlistmentOutcome.Enlisted;
    }

    /// <inheritdoc />
    public async Task EnlistRecurringIdempotencyAsync(
        LocalNodeDbContext ctx,
        JournalEntry entry,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(entry);

        var pending = RecurringInvoiceWriteScope.Current;
        if (pending is null)
            return; // no recurring generation in flight — every non-recurring JE post (manual/bills/etc.)

        // Match the pending record to THIS journal entry by the stable source reference. A mismatch
        // means the scope belongs to a different JE — never stage the wrong idempotency update.
        if (!string.Equals(pending.SourceReference, entry.SourceReference, StringComparison.Ordinal))
            return;

        // Load the schedule on the SAME context (tracked). IgnoreQueryFilters + explicit tenant WHERE is
        // the node's per-org isolation predicate (no ambient query filter).
        var schedule = await ctx.Set<RecurringInvoiceSchedule>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                s => s.TenantId == pending.TenantId && s.Id == pending.ScheduleId, ct)
            .ConfigureAwait(false);

        if (schedule is null)
        {
            // The schedule must exist — the generation step loaded it moments ago. A missing row here is
            // a real invariant breach; fail the whole transaction so the JE rolls back too (fail-closed:
            // never commit a JE whose idempotency record we cannot write).
            throw new InvalidOperationException(
                $"RecurringInvoiceSchedule '{pending.ScheduleId}' not found while enlisting the idempotency " +
                $"record for occurrence {pending.OccurrenceDate:yyyy-MM-dd}; refusing to commit an orphan JE.");
        }

        // Idempotent: if the occurrence is already recorded (a benign double-stage), nothing to do — the
        // single save still commits the JE; the JE unique index is the backstop against an actual re-post.
        if (schedule.GeneratedInvoices.ContainsKey(pending.OccurrenceDate))
            return;

        schedule.RecordGeneratedInvoice(pending.OccurrenceDate, pending.InvoiceId, pending.ActAt);

        // Mark GeneratedInvoices + LastGeneratedAtUtc modified explicitly as a guard that is INDEPENDENT of
        // the configured value-comparer — NOT because change detection would otherwise miss them. EF's
        // DetectChanges would in fact catch the change on its own: GeneratedInvoices has a ValueComparer
        // (generatedInvoicesComparer in ArEntityModule, JSON-serialize equality) and LastGeneratedAtUtc is a
        // plain tracked property. This explicit IsModified is belt-and-suspenders so the UPDATE still emits
        // if that comparer is ever changed or removed (bug-1337 deep-review F2).
        var schedEntry = ctx.Entry(schedule);
        schedEntry.Property(nameof(RecurringInvoiceSchedule.GeneratedInvoices)).IsModified = true;
        schedEntry.Property(nameof(RecurringInvoiceSchedule.LastGeneratedAtUtc)).IsModified = true;

        // STAGE only — the caller's single SaveChangesAsync commits this with the JE row atomically.
    }
}
