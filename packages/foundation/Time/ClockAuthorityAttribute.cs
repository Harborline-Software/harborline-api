namespace Harborline.Api.Foundation.Time;

/// <summary>
/// Classifies a persistence type as a clock authority: it decides something itself and dates its own decision or
/// processing rows from its own clock. A persistence type without this classification is a store, which persists
/// what the act hands it, so it holds no clock and stamps every row with the instant its caller supplies
/// (T-1057, DES-0029 <c>kernel-core-ck-9</c>; <c>KernelStoreClockArchTests</c> enforces it).
/// </summary>
/// <remarks>
/// The classification is a claim about the type, not an exemption: <see cref="Reason"/> says what the type decides
/// and why its instant is its own rather than the act's.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ClockAuthorityAttribute(string reason) : Attribute
{
    /// <summary>What the type decides, and why it dates that decision from its own clock.</summary>
    public string Reason { get; } = reason;
}
