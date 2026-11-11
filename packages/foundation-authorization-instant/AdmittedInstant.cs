namespace Harborline.Api.Foundation.Authorization;

/// <summary>
/// The instant an authority decision is taken at, admitted by the server (T-1015, DES-0029 ck-5 and ck-9,
/// board finding F2). It is minted only from the kernel clock, read once per act (T-650), or from a stored
/// act's recorded instant (L143). No caller can supply a raw <see cref="DateTimeOffset"/>, so no caller can
/// choose the time a decision rests on. <c>AuthorizationGateRequest</c> and
/// <c>AuthorizationWriteContext</c> take only this type.
/// </summary>
public sealed record AdmittedInstant
{
    private AdmittedInstant(DateTimeOffset value) => Value = value;

    public DateTimeOffset Value { get; }

    /// <summary>A live act: one read of the kernel clock. Production holds only the host's clock (ticket 216).</summary>
    public static AdmittedInstant Read(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (clock is IDeclaredInstantClock)
            throw new ArgumentException("A declared-instant clock reports a chosen time; it cannot admit a decision instant.", nameof(clock));
        return new(clock.GetUtcNow());
    }

    /// <summary>
    /// A past act, decided as of the instant the server recorded for it (L143), read back from the stored act.
    /// Internal: only this assembly's reviewed friends can reach it, and <c>AdmittedInstantArchTests</c> pins every
    /// production call site, so a new one is reviewed.
    /// </summary>
    internal static AdmittedInstant FromRecordedAct(DateTimeOffset recordedAt) => new(recordedAt);

    /// <summary>
    /// Caps replicated roster authority at the caller's admitted act plus its reviewed skew window.
    /// Internal; AdmittedInstantArchTests pins the sole host caller that supplies the handshake window.
    /// </summary>
    internal static AdmittedInstant ForRosterAuthority(
        DateTimeOffset boundedOrderTime, AdmittedInstant admittedAt, TimeSpan skew)
    {
        ArgumentNullException.ThrowIfNull(admittedAt);
        var cap = admittedAt.Value + skew;
        return new(boundedOrderTime < cap ? boundedOrderTime : cap);
    }

    /// <summary>
    /// An instant the server did NOT admit, taken anyway at a named, owed site (T-1015 slice 3). Each
    /// <see cref="AdmittedInstantExemption"/> is pinned to its production callers by <c>AdmittedInstantArchTests</c>.
    /// </summary>
    /// <summary>
    /// T-1056 (R-0149 decision 2, ruled R2): the one place a rehearsal instant becomes a decision instant, so the
    /// isolated candidate world can run the real authority over its declared fixture time. Internal, and
    /// <c>AdmittedInstantArchTests</c> pins its only callers to <c>VerificationCandidateWorld</c> and
    /// <c>VerificationRunner</c>; the server clock stays the only authority for every other decision.
    /// </summary>
    internal static AdmittedInstant ForRehearsal(RehearsalInstant rehearsal)
    {
        ArgumentNullException.ThrowIfNull(rehearsal);
        return new(rehearsal.Value);
    }

    internal static AdmittedInstant Exempt(DateTimeOffset at, AdmittedInstantExemption exemption) =>
        Enum.IsDefined(exemption) ? new(at) : throw new ArgumentOutOfRangeException(nameof(exemption));

    public override string ToString() => Value.ToString("O");
}

/// <summary>The decision instants T-1015 slice 1 leaves owed. Each is reclassified or removed by slice 3.</summary>
public enum AdmittedInstantExemption
{
}

/// <summary>
/// A clock that reports a declared instant instead of reading time, such as a verification fixture's or a form
/// rule evaluation's pinned clock. <see cref="AdmittedInstant.Read"/> refuses one, so a declared time can never
/// become a decision instant, and <c>AdmittedInstantArchTests</c> requires every production clock to carry this.
/// </summary>
public interface IDeclaredInstantClock
{
    /// <summary>
    /// The instant this clock was declared at. Reading it is not a clock read: a declared clock reads no ambient time
    /// and never advances (T-1056, so a rehearsal instant is taken from it without a kernel-clock read, T-650/T-690).
    /// </summary>
    DateTimeOffset DeclaredInstant { get; }
}

/// <summary>
/// A configuration rehearsal's declared time (T-1056, R-0149 decision 2, ruled R2: "a rehearsal-only instant type that only
/// the isolated candidate world can create and accept; the server clock stays the only authority for real decisions").
/// It is read only from an injected declared clock (<see cref="IDeclaredInstantClock"/>, the host's
/// <c>DeclaredInstantTimeProvider</c>), never from the kernel clock, so TimeProvider injection is kept and the type is
/// the boundary on top of it (DES-0044 governance-ck-4, "declared deterministic simulators").
/// </summary>
public sealed record RehearsalInstant
{
    private RehearsalInstant(DateTimeOffset value) => Value = value;

    public DateTimeOffset Value { get; }

    /// <summary>
    /// Reads the declared clock the composition injected for this rehearsal. A clock that is not declared (the kernel
    /// clock) is refused: real time cannot pose as a rehearsal. Internal; <c>AdmittedInstantArchTests</c> pins the caller.
    /// </summary>
    internal static RehearsalInstant Read(TimeProvider declaredClock)
    {
        ArgumentNullException.ThrowIfNull(declaredClock);
        return declaredClock is IDeclaredInstantClock declared
            ? new(declared.DeclaredInstant)
            : throw new ArgumentException("A rehearsal instant is read only from a declared-instant clock.", nameof(declaredClock));
    }
}
