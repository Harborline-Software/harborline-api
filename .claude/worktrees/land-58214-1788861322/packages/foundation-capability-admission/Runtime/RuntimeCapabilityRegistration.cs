namespace Harborline.Api.Foundation.CapabilityAdmission;

/// <summary>
/// Host-owned declaration of the executable registrations actually present for one capability version.
/// Registrations are composed at startup; request callers cannot add or mutate them.
/// </summary>
public sealed record RuntimeCapabilityRegistration(
    CapabilityKey Key,
    string ArtifactName,
    ExecutableInventory Executables)
{
    internal RuntimeCapabilityRegistration(CapabilityKey key, ExecutableInventory executables)
        : this(key, "carrier.bundle", executables)
    {
    }
}

/// <summary>The stable result of a capability-owned readiness probe.</summary>
public sealed record CapabilityReadinessResult
{
    private CapabilityReadinessResult(bool ready, IReadOnlyList<string> refusalCodes)
    {
        Ready = ready;
        RefusalCodes = refusalCodes;
    }

    /// <summary>Whether the admitted runtime is currently usable.</summary>
    public bool Ready { get; }
    /// <summary>Stable refusal codes; empty only when ready.</summary>
    public IReadOnlyList<string> RefusalCodes { get; }

    /// <summary>Creates the successful readiness verdict.</summary>
    public static CapabilityReadinessResult Available { get; } = new(true, Array.Empty<string>());

    /// <summary>Creates a fail-closed not-ready verdict.</summary>
    public static CapabilityReadinessResult Unavailable(params string[] refusalCodes)
    {
        ArgumentNullException.ThrowIfNull(refusalCodes);
        var codes = refusalCodes.Length == 0
            ? new[] { CapabilityRefusalCodes.NotReady }
            : refusalCodes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (codes.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Readiness refusal codes must be non-empty.", nameof(refusalCodes));
        }

        return new CapabilityReadinessResult(false, Array.AsReadOnly(codes));
    }
}

/// <summary>Capability-owned, cancellation-aware runtime readiness probe.</summary>
public interface ICapabilityReadinessProbe
{
    /// <summary>The exact compiled capability version this probe evaluates.</summary>
    CapabilityKey Key { get; }

    /// <summary>Returns current usability without mutating runtime, tenant, pack, or permission state.</summary>
    ValueTask<CapabilityReadinessResult> ProbeAsync(CancellationToken cancellationToken = default);
}
