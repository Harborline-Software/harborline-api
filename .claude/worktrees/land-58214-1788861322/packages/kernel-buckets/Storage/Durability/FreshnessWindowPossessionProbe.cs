namespace Harborline.Api.Kernel.Buckets.Storage.Durability;

/// <summary>
/// PERS-2 — the MVP-floor <see cref="IReplicaPossessionProbe"/>: a claim counts as a current confirmation only if
/// its <see cref="ConfirmedReplica.ConfirmedAt"/> falls in the CLAMPED window
/// <c>[now - Window, now + ClockSkew]</c>. A claim older than <see cref="Window"/> is UNCONFIRMED (a stale
/// "was replicated once" entry can never by itself authorize shedding the last local copy); a claim dated further
/// than <see cref="ClockSkew"/> in the FUTURE is ALSO rejected (F-Min-2 — an unbounded future timestamp must never
/// count as fresh, a poisoned-gossip hazard once the ledger accepts peer-supplied claims).
/// </summary>
/// <remarks>
/// <para>
/// This is a real — if minimal — verify-before-evict rule: it refuses to trust an aged OR implausibly-future
/// confirmation. It is NOT a full possession challenge (it does not re-contact the destination), which is a
/// later-phase upgrade behind the same <see cref="IReplicaPossessionProbe"/> seam. Time comes from an injected
/// <see cref="TimeProvider"/> so the chaos matrix can drive stale/fresh/future permutations deterministically.
/// </para>
/// </remarks>
public sealed class FreshnessWindowPossessionProbe : IReplicaPossessionProbe
{
    /// <summary>The default freshness window (a confirmation older than this is not trusted).</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(24);

    /// <summary>The default tolerated forward clock skew (a confirmation dated further ahead than this is rejected).</summary>
    public static readonly TimeSpan DefaultClockSkew = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time;

    /// <summary>The maximum age a confirmation may have and still count as current.</summary>
    public TimeSpan Window { get; }

    /// <summary>The maximum future dating (clock-skew tolerance) a confirmation may have and still count as current.</summary>
    public TimeSpan ClockSkew { get; }

    /// <summary>Construct with an explicit window, clock-skew tolerance, and time source.</summary>
    public FreshnessWindowPossessionProbe(TimeSpan window, TimeSpan clockSkew, TimeProvider? time = null)
    {
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "The freshness window must be positive.");
        }
        if (clockSkew < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(clockSkew), "The clock-skew tolerance must be non-negative.");
        }
        Window = window;
        ClockSkew = clockSkew;
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Construct with an explicit window and the <see cref="DefaultClockSkew"/>.</summary>
    public FreshnessWindowPossessionProbe(TimeSpan window, TimeProvider? time = null)
        : this(window, DefaultClockSkew, time) { }

    /// <summary>Construct with the <see cref="DefaultWindow"/> and <see cref="DefaultClockSkew"/>.</summary>
    public FreshnessWindowPossessionProbe(TimeProvider? time = null) : this(DefaultWindow, DefaultClockSkew, time) { }

    /// <inheritdoc />
    public ValueTask<bool> ConfirmAsync(ConfirmedReplica claim, CancellationToken ct)
    {
        var age = _time.GetUtcNow() - claim.ConfirmedAt;
        // Accept iff within the past Window AND not dated more than ClockSkew in the future:
        //   -ClockSkew <= age <= Window.
        var fresh = age <= Window && age >= -ClockSkew;
        return ValueTask.FromResult(fresh);
    }
}
