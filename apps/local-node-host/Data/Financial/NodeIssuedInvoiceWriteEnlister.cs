using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Coordination;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// Default <see cref="INodeIssuedInvoiceWriteEnlister"/>. Stages the in-flight invoice's
/// <c>Draft → Issued</c> update onto the JE write's <see cref="LocalNodeDbContext"/> so the two commit in
/// one SQLite transaction (ADR 0135 F3 = no posted-JE-with-stranded-Draft on crash-resume).
/// </summary>
/// <remarks>
/// <para>
/// <b>Match-by-source-reference.</b> The enlister only acts when an ambient
/// <see cref="IssuedInvoiceWriteScope"/> is active AND its pending update's
/// <see cref="PendingIssuedInvoiceUpdate.SourceReference"/> equals the JE's
/// <see cref="JournalEntry.SourceReference"/> (both <c>invoice:{invoiceId}</c>). That guards against
/// staging the wrong invoice if the posting pipeline ever posts more than one entry under a single scope
/// (it does not today — one issue == one JE — but the guard keeps the enlister correct by construction).
/// </para>
/// <para>
/// <b>Stages on the SAME ctx.</b> It loads the existing (tracked) invoice row on
/// <paramref name="ctx"/> — the node has no ambient query filter, so the explicit tenant predicate is the
/// per-org isolation boundary (ADR 0092 §A3) — and copies the issued snapshot's mutable fields onto it.
/// It does NOT call <c>SaveChangesAsync</c> — the caller's single save in
/// <see cref="NodeEfJournalStore.SaveAtomicAsync"/> commits the JE row, the audit row, the recurring
/// idempotency record, and this invoice-status update together.
/// </para>
/// <para>
/// <b>Tenant guard (ADR 0092).</b> A pending tenant ≠ the JE's tenant is an invariant breach — the enlister
/// throws so the whole transaction rolls back (never co-commit a JE with a foreign-tenant invoice update).
/// </para>
/// </remarks>
public sealed class NodeIssuedInvoiceWriteEnlister : INodeIssuedInvoiceWriteEnlister, IWriteEnlistment
{
    /// <inheritdoc />
    public WriteInvariant Invariant => NodeWriteInvariants.IssuedInvoice;

    /// <inheritdoc />
    public async ValueTask<WriteEnlistmentOutcome> EnlistAsync(
        StagedWriteUnitOfWork unitOfWork,
        CancellationToken cancellationToken = default)
    {
        if (unitOfWork is not NodeJournalWriteUnitOfWork nodeWrite)
        {
            return WriteEnlistmentOutcome.NotApplicable;
        }

        var pending = IssuedInvoiceWriteScope.Current;
        if (pending is null || !string.Equals(
                pending.SourceReference,
                nodeWrite.Entry.SourceReference,
                StringComparison.Ordinal))
        {
            return WriteEnlistmentOutcome.NotApplicable;
        }

        await EnlistIssuedInvoiceAsync(nodeWrite.Context, nodeWrite.Entry, cancellationToken)
            .ConfigureAwait(false);
        return WriteEnlistmentOutcome.Enlisted;
    }

    /// <inheritdoc />
    public async Task EnlistIssuedInvoiceAsync(
        LocalNodeDbContext ctx,
        JournalEntry entry,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(entry);

        var pending = IssuedInvoiceWriteScope.Current;
        if (pending is null)
            return; // no invoice issue in flight — every non-issue JE post (manual/bills/payments/etc.)

        // Match the pending update to THIS journal entry by the stable source reference. A mismatch means
        // the scope belongs to a different JE (e.g. a void/write-off JE posted while an issue scope is also
        // somehow active) — never stage the wrong invoice update.
        if (!string.Equals(pending.SourceReference, entry.SourceReference, StringComparison.Ordinal))
            return;

        // Defence-in-depth: the pending invoice's tenant must equal the JE's tenant. A mismatch is a caller
        // bug; fail the whole transaction so the JE rolls back too (fail-closed).
        if (!pending.TenantId.Equals(entry.TenantId))
        {
            throw new InvalidOperationException(
                $"IssuedInvoiceWriteScope tenant '{pending.TenantId.Value}' does not match the issue JE tenant " +
                $"'{entry.TenantId.Value}' for source reference '{pending.SourceReference}'; refusing to co-commit.");
        }

        var issued = pending.Issued;

        // Load the tracked Draft row on the SAME context (explicit tenant WHERE is the node's per-org
        // isolation predicate — no ambient query filter). The generation/issue path created this Draft
        // moments ago, so it must exist; a missing row is a real invariant breach → fail the transaction.
        var existing = await ctx.Set<Invoice>()
            .FirstOrDefaultAsync(i => i.Id == issued.Id && i.TenantId == pending.TenantId, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            throw new InvalidOperationException(
                $"Invoice '{issued.Id.Value}' not found while enlisting the Draft → Issued update for " +
                $"source reference '{pending.SourceReference}'; refusing to commit an issue JE with a missing invoice.");
        }

        if (existing.DeletedAtUtc is not null)
        {
            throw new InvalidOperationException(
                $"Invoice '{issued.Id.Value}' is tombstoned; cannot co-commit a Draft → Issued update with its issue JE.");
        }

        // Idempotent: if the row is already Issued (a benign double-stage / a re-drive that still reached
        // SaveAtomic) there is nothing to change.
        if (existing.Status == InvoiceStatus.Issued && existing.JournalEntryId is not null)
        {
            return;
        }

        // Copy the issued snapshot's mutable fields onto the tracked row so EF emits an UPDATE that commits
        // with the JE. Setting CurrentValues from the issued record updates every scalar/owned property in
        // one call and keeps the row identity (PK) stable.
        ctx.Entry(existing).CurrentValues.SetValues(issued);

        // STAGE only — the caller's single SaveChangesAsync commits this with the JE row atomically.
    }
}
