using System;

namespace Harborline.Api.LocalNodeHost.Data.Governance;

/// <summary>
/// The instance lifecycle phase carried on the tenant's <see cref="TenantGovernanceState"/> — the one flag
/// that drives the Harborline App's <b>lifecycle-aware Build fold</b> (first-run + Workshop-IA design ADDENDUM
/// AD.1; ADR 0144). A fresh instance is born in <see cref="Setup"/> (Build/Workshop prominent while the
/// founder shapes the business); the founder <em>declares</em> the transition to <see cref="Operating"/>
/// (Build folds out of the switcher, reachable only via the guarded re-entry).
/// </summary>
/// <remarks>
/// <para>
/// <b>This changes chrome prominence, not authority (ADR 0144 D9 / AD.1).</b> The phase is NOT an
/// authority-topology change — it recomputes no permissions and touches no who-can-do-what. It is
/// low-blast tenant chrome state, one value per tenant.
/// </para>
/// <para>
/// <b>Locking down (setup → operating) is the SAFE direction — unguarded.</b> It reduces surface and is
/// reversible (Build can be re-entered), so the "Start running your business" flip needs no guard. Only the
/// <em>re-entry</em> (unlock) is consequential, and that is gated on the <c>workshop:unlock</c> permission
/// (<see cref="WorkshopUnlock"/>), consumed by the Harborline App's guarded re-entry control (slice B4).
/// </para>
/// </remarks>
public enum SetupPhase
{
    /// <summary>Fresh from genesis — the founder is actively shaping the business; Build/Workshop is
    /// prominent in the switcher. The instance is <em>born</em> in this phase (genesis writes it).</summary>
    Setup = 0,

    /// <summary>The founder has declared setup complete — the day-to-day switcher is operations-only and
    /// Build has folded out, reachable only through the guarded re-entry control (slice B4).</summary>
    Operating = 1,
}

/// <summary>Wire-token mapping for <see cref="SetupPhase"/> — the lowercase strings the Harborline App nav config
/// consumes (matches the FED hook contract, <c>useLifecyclePhase.ts</c>).</summary>
public static class SetupPhaseWire
{
    /// <summary>Wire token for <see cref="SetupPhase.Setup"/>.</summary>
    public const string Setup = "setup";

    /// <summary>Wire token for <see cref="SetupPhase.Operating"/>.</summary>
    public const string Operating = "operating";

    /// <summary>The lowercase wire token for <paramref name="phase"/>.</summary>
    public static string ToWire(SetupPhase phase) => phase switch
    {
        SetupPhase.Setup => Setup,
        SetupPhase.Operating => Operating,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown setup phase."),
    };

    /// <summary>Parses a wire token back to a <see cref="SetupPhase"/>; throws on an unknown token
    /// (fail-closed — a corrupt persisted value must be caught, never silently coerced).</summary>
    public static SetupPhase FromWire(string token) => token switch
    {
        Setup => SetupPhase.Setup,
        Operating => SetupPhase.Operating,
        _ => throw new FormatException($"Unknown setup-phase wire token '{token}'."),
    };
}
