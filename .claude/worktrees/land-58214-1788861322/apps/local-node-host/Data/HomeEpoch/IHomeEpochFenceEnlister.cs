using Harborline.Api.Blocks.FinancialLedger.Models;

namespace Harborline.Api.LocalNodeHost.Data.HomeEpoch;

/// <summary>
/// The <b>G-4 in-transaction home-failover fence.</b> When an ambient <see cref="HomeEpochWriteScope"/> is
/// active (an invariant-bearing write is asserting the epoch it believes it is home for), this enlister
/// reads the tenant's current <see cref="HomeEpochRecord"/> on the IN-FLIGHT
/// <see cref="LocalNodeDbContext"/> — the SAME context the effect is staged on — and throws (fail-closed)
/// if the asserted epoch is stale (lower than the current home epoch). The throw aborts the single
/// <c>SaveChangesAsync</c>, so the invariant-bearing effect (the JE, its audit row, the invoice status, …)
/// rolls back WITH the rejected fence check.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why in the transaction (no TOCTOU).</b> The fence read rides the SAME context as the effect, AND the
/// caller (<c>NodeEfJournalStore.SaveAtomicAsync</c>) runs the whole fenced unit-of-work inside an explicit
/// <c>BEGIN IMMEDIATE</c> transaction (<see cref="HomeEpochFenceTransaction"/>). That takes the
/// write/RESERVED lock BEFORE this fence read, so there is no window between "check the epoch" and "commit
/// the effect" in which a concurrent promotion can commit a higher epoch: a superseded (lower epoch) home
/// and a higher-epoch promotion are serialized by the held write lock, and the stale home's effect cannot
/// commit. A plain read with no held write lock would run as its own autocommit statement and release its
/// SHARED lock before the write lock is taken — the TOCTOU this fence is meant to close (security verdict
/// earlier repository ticket #1365 Finding 1). This is also the explicit rejection of the Gap-2a design ("read the epoch from
/// a separate connection / the roster CRDT projection then commit the effect" — that re-opens the TOCTOU).
/// </para>
/// <para>
/// <b>Same shared-unit-of-work mechanism as the audit + recurring-idempotency enlisters</b>
/// (<c>INodeAuditWriteEnlister</c> / <c>INodeRecurringInvoiceWriteEnlister</c>, ADR 0126 §D2). It does NOT
/// call <c>SaveChangesAsync</c>; it only performs the fence READ on the in-flight context and throws on a
/// stale epoch BEFORE the caller's single save. Riding the same chokepoint
/// (<c>NodeEfJournalStore.SaveAtomicAsync</c>) means the fence covers JE-post automatically.
/// </para>
/// <para>
/// The declared journal operation requires this adapter. With no ambient <see cref="HomeEpochWriteScope"/>,
/// the Platform seam reports not applicable; with no registered adapter, the save is refused.
/// </para>
/// <para>
/// <b>Fail-open ONLY when there is no home epoch yet.</b> If the tenant has NO <see cref="HomeEpochRecord"/>
/// (the genesis single-device state, no failover has ever occurred), there is no "stale" to be — the fence
/// is a no-op and the write proceeds. The fence only ever REJECTS; it never grants authority a write did
/// not otherwise have. Activating a doctype to multi-home (MD-3/MD-4) is what makes a scope present.
/// </para>
/// </remarks>
public interface IHomeEpochFenceEnlister
{
    /// <summary>
    /// If an ambient <see cref="HomeEpochWriteScope"/> is active, reads the tenant's current home epoch on
    /// <paramref name="ctx"/> and throws <see cref="StaleHomeEpochException"/> if the scope's asserted
    /// epoch is below it. Otherwise a no-op. Does NOT save.
    /// </summary>
    /// <param name="ctx">The in-flight context the invariant-bearing effect is staged on.</param>
    /// <param name="entry">The journal entry being posted (the JE-post fence site).</param>
    /// <param name="ct">Cancellation token.</param>
    Task EnlistFenceAsync(
        LocalNodeDbContext ctx,
        JournalEntry entry,
        CancellationToken ct = default);
}

/// <summary>
/// Thrown when a write asserts a home epoch that is STALE (lower than the tenant's current home epoch) —
/// the fence rejects the write INSIDE its own transaction so the invariant-bearing effect rolls back. A
/// superseded home literally cannot commit.
/// </summary>
public sealed class StaleHomeEpochException : Exception
{
    /// <summary>The epoch the rejected writer asserted.</summary>
    public long AssertedEpoch { get; }

    /// <summary>The tenant's actual current home epoch (greater than <see cref="AssertedEpoch"/>).</summary>
    public long CurrentEpoch { get; }

    /// <summary>Constructs the stale-epoch rejection.</summary>
    public StaleHomeEpochException(string tenantId, long assertedEpoch, long currentEpoch)
        : base($"Write for tenant '{tenantId}' asserts home epoch {assertedEpoch} but the current home " +
               $"epoch is {currentEpoch} — this device is no longer home; the write is rejected inside its " +
               $"transaction (split-brain fence).")
    {
        AssertedEpoch = assertedEpoch;
        CurrentEpoch = currentEpoch;
    }
}

/// <summary>
/// The home-epoch a single in-flight invariant-bearing write asserts it is home for. Carried ambiently
/// (<see cref="AsyncLocal{T}"/>) across the <c>…Post → SaveAtomicAsync</c> call chain — the SAME pattern
/// <c>RecurringInvoiceWriteScope</c> / <c>IssuedInvoiceWriteScope</c> use — so the deep enlister + the
/// numbering-service guard can read it without a signature change to the shared posting interfaces.
/// </summary>
/// <param name="TenantId">The tenant of the write (<c>TenantId.Value</c>) — the fence read predicate.</param>
/// <param name="AssertedEpoch">The home epoch this device believes it is — rejected if below the
/// tenant's durable current epoch at point-of-effect.</param>
/// <param name="HomeDeviceId">The asserting device id (diagnostics; the epoch number is the fence key).</param>
public sealed record PendingHomeEpochAssertion(
    string TenantId,
    long AssertedEpoch,
    string HomeDeviceId);

/// <summary>
/// Ambient (<see cref="AsyncLocal{T}"/>) context holder for the single pending home-epoch assertion across the
/// post-effect call chain. A multi-home write opens one with <see cref="Enter"/> in a <c>using</c>
/// immediately before driving the effect; the fence enlister + the numbering guard read
/// <see cref="Current"/> inside the effect's transaction.
/// </summary>
public sealed class HomeEpochWriteScope : IDisposable
{
    private static readonly AsyncLocal<PendingHomeEpochAssertion?> _current = new();

    private readonly PendingHomeEpochAssertion? _previous;
    private bool _disposed;

    private HomeEpochWriteScope(PendingHomeEpochAssertion pending)
    {
        _previous = _current.Value;
        _current.Value = pending;
    }

    /// <summary>The pending assertion for the in-flight write, or <see langword="null"/> when none is
    /// active (every single-device write today — the fence is a no-op).</summary>
    public static PendingHomeEpochAssertion? Current => _current.Value;

    /// <summary>Opens an ambient scope carrying <paramref name="pending"/>. Dispose restores the prior
    /// value (nested scopes are well-behaved).</summary>
    public static HomeEpochWriteScope Enter(PendingHomeEpochAssertion pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        return new HomeEpochWriteScope(pending);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _current.Value = _previous;
    }
}
