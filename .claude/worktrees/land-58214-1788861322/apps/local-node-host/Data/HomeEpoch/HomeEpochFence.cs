using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.HomeEpoch;

/// <summary>
/// The single source of truth for the IN-TRANSACTION fence check (security verdict G-4): "read the
/// tenant's current home epoch ON THIS CONTEXT and reject a stale assertion." Shared by
/// <see cref="HomeEpochFenceEnlister"/> (the JE-post site) and the node invoice-numbering service (the
/// sequence-allocation site, Gap-2b) so EVERY fenced site applies byte-identical fence logic against the
/// SAME context the effect commits on — no second source to drift (anti-pattern A4).
/// </summary>
public static class HomeEpochFence
{
    /// <summary>
    /// TEST-ONLY interleave hook. When set (only by the interleaved-concurrency test), it is awaited
    /// immediately AFTER the fence read materializes and BEFORE the caller's stage/save runs — i.e. exactly
    /// in the read-through-write window the G-4 atomicity claim is about. It lets a test deterministically
    /// inject a concurrent home-epoch promotion (a real <c>HomeEfHomeEpochStore.AdvanceAsync</c> on a
    /// SEPARATE connection) into that window:
    /// <list type="bullet">
    /// <item>On the PRE-FIX code (this read ran as its own SQLite autocommit statement, releasing its SHARED
    /// lock before the write lock is taken) the injected promotion COMMITS a higher epoch in the gap, and
    /// the stale home's effect then commits anyway — a split-brain double-commit. The bug is observable.</item>
    /// <item>On the FIXED code (this read runs inside the caller's <c>BEGIN IMMEDIATE</c> transaction, so the
    /// write/RESERVED lock is already held) the injected promotion gets <c>SQLITE_BUSY</c> and CANNOT commit
    /// in the gap — it is serialized strictly after the stale write's transaction. The two are mutually
    /// exclusive in the window; no double-commit occurs.</item>
    /// </list>
    /// The fence still reads the epoch exactly ONCE (production behaviour); the hook does not alter the read.
    /// Null in every production and normal-test path (the hook is a no-op).
    /// </summary>
    internal static Func<Task>? AfterReadHookForTests;

    /// <summary>
    /// If <paramref name="assertion"/> is non-null, reads the tenant's current home epoch (highest
    /// <see cref="HomeEpochRecord.EpochNumber"/>) ON <paramref name="ctx"/> — the in-flight context the
    /// effect is staged on — and throws <see cref="StaleHomeEpochException"/> if the asserted epoch is
    /// below it. A no-op when <paramref name="assertion"/> is null (no multi-home scope active) or when the
    /// tenant has no home epoch yet (genesis single-device state — nothing to be stale against).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The read is <c>AsNoTracking</c> over the SAME context the caller stages and saves the effect on. For
    /// the read and the effect commit to be ATOMIC (no TOCTOU), the caller MUST run this read + its
    /// <c>SaveChangesAsync</c> inside an explicit <c>BEGIN IMMEDIATE</c> transaction
    /// (<see cref="HomeEpochFenceTransaction"/>): that takes the write/RESERVED lock BEFORE this read, so a
    /// concurrent promotion cannot commit a higher epoch in the window. A plain read with no held write lock
    /// runs as its own autocommit statement and releases its SHARED lock before the write lock is taken —
    /// the TOCTOU this fence is meant to close (security verdict earlier repository ticket #1365 Finding 1). This method does
    /// the comparison only; <see cref="HomeEpochFenceTransaction"/> supplies the lock.
    /// </para>
    /// <para>
    /// Outcome: a superseded home whose <c>BEGIN IMMEDIATE</c> read sees a higher committed epoch is
    /// rejected and its effect rolls back; a promotion that races a stale write in progress is serialized
    /// (it gets the write lock only after the stale transaction commits or rolls back). Either way the stale
    /// home cannot double-commit.
    /// </para>
    /// </remarks>
    public static async Task AssertNotStaleAsync(
        LocalNodeDbContext ctx,
        PendingHomeEpochAssertion? assertion,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (assertion is null)
        {
            // No multi-home write in flight — every single-device write today. Fence is a no-op.
            return;
        }

        // Read the current home-epoch tip for the tenant on THIS context (the in-flight transaction).
        var currentEpoch = await ctx.Set<HomeEpochRecord>()
            .Where(r => r.TenantId == assertion.TenantId)
            .OrderByDescending(r => r.EpochNumber)
            .Select(r => (long?)r.EpochNumber)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // TEST-ONLY: deterministically interleave a concurrent promotion into the read-through-write window.
        // No-op in production (the hook is null). The hook attempts a real promotion on a SEPARATE
        // connection; on the fixed code the held BEGIN IMMEDIATE write lock makes that attempt fail-fast
        // (SQLITE_BUSY), on the pre-fix code it commits in the gap. We do NOT re-read here — the fence reads
        // the epoch exactly once, as in production. The test asserts the END STATE (mutual exclusion / no
        // double-commit), which the lock — not a re-read — is what enforces. See AfterReadHookForTests.
        var hook = AfterReadHookForTests;
        if (hook is not null)
        {
            await hook().ConfigureAwait(false);
        }

        if (currentEpoch is null)
        {
            // No home epoch established yet (genesis single-device state) — nothing to be stale against.
            // The fence only ever REJECTS; it never grants authority a write did not otherwise have.
            return;
        }

        if (assertion.AssertedEpoch < currentEpoch.Value)
        {
            // A superseded home. Throwing here, BEFORE the caller's single SaveChangesAsync, aborts the
            // whole transaction — the invariant-bearing effect rolls back. The stale home cannot commit.
            throw new StaleHomeEpochException(assertion.TenantId, assertion.AssertedEpoch, currentEpoch.Value);
        }
    }
}
