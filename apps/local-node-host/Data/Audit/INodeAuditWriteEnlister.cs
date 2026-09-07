using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.LocalNodeHost.Data.Audit;

/// <summary>
/// Stages an audit row onto an in-flight <see cref="LocalNodeDbContext"/> so the audit append commits
/// in the SAME transaction as the financial write it records (ADR 0126 §D2 / OQ2 = ATOMIC).
/// </summary>
/// <remarks>
/// <para>
/// <b>Atomicity by shared unit-of-work.</b> The enlister does NOT call <c>SaveChangesAsync</c>; it only
/// <c>ctx.Add</c>s the audit row to the SAME context the caller is about to save. The single
/// <c>SaveChangesAsync</c> in <see cref="NodeEfJournalStore.SaveAtomicAsync"/> then commits the JE row
/// and the audit row in one SQLite transaction — a committed posting can never lack its audit row, and
/// an audit-write failure rolls back the posting (OQ2 = atomic, binding CIC ruling).
/// </para>
/// <para>
/// The declared journal operation requires this adapter; an absent registration refuses the save.
/// </para>
/// </remarks>
public interface INodeAuditWriteEnlister
{
    /// <summary>
    /// Stages a <c>Financial.JournalPosted</c> audit row for <paramref name="entry"/> onto
    /// <paramref name="ctx"/>, computing its hash-chain link from the tenant's current chain tip
    /// (read within the same context). Does NOT save — the caller's single <c>SaveChangesAsync</c>
    /// commits both rows atomically.
    /// </summary>
    /// <param name="ctx">The in-flight context the JE write is staged on.</param>
    /// <param name="entry">The posted journal entry being recorded.</param>
    /// <param name="ct">Cancellation token.</param>
    Task EnlistJournalPostedAsync(
        LocalNodeDbContext ctx,
        JournalEntry entry,
        AuthorizationDecision decision,
        CancellationToken ct = default);
}
