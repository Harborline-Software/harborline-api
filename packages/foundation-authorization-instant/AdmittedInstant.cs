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
        return new(clock.GetUtcNow());
    }

    /// <summary>
    /// A past act, decided as of the instant the server recorded for it (L143), read back from the stored act.
    /// Internal: only this assembly's reviewed friends can reach it, and <c>AdmittedInstantArchTests</c> pins every
    /// production call site, so a new one is reviewed.
    /// </summary>
    internal static AdmittedInstant FromRecordedAct(DateTimeOffset recordedAt) => new(recordedAt);

    /// <summary>
    /// An instant the server did NOT admit, taken anyway at a named, owed site (T-1015 slice 3). Each
    /// <see cref="AdmittedInstantExemption"/> is pinned to its production callers by <c>AdmittedInstantArchTests</c>.
    /// </summary>
    internal static AdmittedInstant Exempt(DateTimeOffset at, AdmittedInstantExemption exemption) =>
        Enum.IsDefined(exemption) ? new(at) : throw new ArgumentOutOfRangeException(nameof(exemption));

    public override string ToString() => Value.ToString("O");
}

/// <summary>The decision instants T-1015 slice 1 leaves owed. Each is reclassified or removed by slice 3.</summary>
public enum AdmittedInstantExemption
{
    /// <summary>Configuration verification decides in its isolated candidate world at the request's fixture instant.</summary>
    ConfigurationRehearsal = 1,
    /// <summary>Roster sync reads install-root authority at the device-signed roster <c>IssuedAt</c> (ADR-0053).</summary>
    SignedRosterIssuedAt = 2,
}
