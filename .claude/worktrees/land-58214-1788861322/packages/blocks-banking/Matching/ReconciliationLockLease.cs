using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Banking.Matching;

/// <summary>
/// Evaluates the bounded lease carried by a reconciliation lock.
/// </summary>
/// <remarks>
/// <see cref="Reconciliation.LockedAt"/> is the lease start. A locked row with no
/// timestamp, or one whose lease has expired, is not held and may be recovered by
/// the next caller without an authorization lookup or database edit.
/// </remarks>
public sealed class ReconciliationLockLease
{
    /// <summary>Default lifetime of a reconciliation lock.</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _time;

    /// <summary>Creates a lease evaluator with an injectable clock and duration.</summary>
    public ReconciliationLockLease(
        TimeProvider? time = null,
        TimeSpan? duration = null)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        Duration = duration ?? DefaultDuration;
        if (Duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "Lease duration must be positive.");
    }

    /// <summary>Configured lease duration.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Current instant from the injected clock.</summary>
    public Instant Now => new(_time.GetUtcNow());

    /// <summary>
    /// Returns <c>true</c> only while the reconciliation has a non-expired lock lease.
    /// </summary>
    public bool IsHeld(Reconciliation reconciliation)
    {
        ArgumentNullException.ThrowIfNull(reconciliation);

        if (reconciliation.LockState != BankReconciliationLockState.Locked
            || reconciliation.LockedAt is not { } lockedAt)
        {
            return false;
        }

        return _time.GetUtcNow() - lockedAt.Value < Duration;
    }
}
