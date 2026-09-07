using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Coordination;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Data.HomeEpoch;

/// <summary>
/// Default <see cref="IHomeEpochFenceEnlister"/>. Applies the G-4 in-transaction fence at the JE-post site:
/// when an ambient <see cref="HomeEpochWriteScope"/> is active, reads the tenant's current home epoch on
/// the in-flight <see cref="LocalNodeDbContext"/> and throws <see cref="StaleHomeEpochException"/> if the
/// asserted epoch is stale — so a superseded home's JE post rolls back inside its own transaction.
/// </summary>
/// <remarks>
/// Delegates to <see cref="HomeEpochFence.AssertNotStaleAsync"/> — the SAME fence logic the
/// invoice-numbering guard uses at the sequence-allocation site — so the two fenced sites never drift. No
/// save here; the throw aborts the caller's single <c>SaveChangesAsync</c>. Atomicity (no TOCTOU) comes
/// from the caller running this read + the save inside an explicit <c>BEGIN IMMEDIATE</c> transaction
/// (<see cref="HomeEpochFenceTransaction"/>), which holds the write lock across the read; the read on the
/// in-flight context alone does not serialize against a concurrent promotion.
/// </remarks>
public sealed class HomeEpochFenceEnlister : IHomeEpochFenceEnlister, IWriteEnlistment
{
    /// <inheritdoc />
    public WriteInvariant Invariant => NodeWriteInvariants.HomeEpoch;

    /// <inheritdoc />
    public async ValueTask<WriteEnlistmentOutcome> EnlistAsync(
        StagedWriteUnitOfWork unitOfWork,
        CancellationToken cancellationToken = default)
    {
        if (unitOfWork is not NodeJournalWriteUnitOfWork nodeWrite || HomeEpochWriteScope.Current is null)
        {
            return WriteEnlistmentOutcome.NotApplicable;
        }

        await EnlistFenceAsync(nodeWrite.Context, nodeWrite.Entry, cancellationToken).ConfigureAwait(false);
        return WriteEnlistmentOutcome.Enlisted;
    }

    /// <inheritdoc />
    public Task EnlistFenceAsync(
        LocalNodeDbContext ctx,
        JournalEntry entry,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(entry);

        // The assertion carries its own tenant (the active-team-derived tenant the write set on the scope),
        // so the fence read predicate matches the JE's tenant. Reads on the in-flight context = no TOCTOU.
        return HomeEpochFence.AssertNotStaleAsync(ctx, HomeEpochWriteScope.Current, ct);
    }
}
