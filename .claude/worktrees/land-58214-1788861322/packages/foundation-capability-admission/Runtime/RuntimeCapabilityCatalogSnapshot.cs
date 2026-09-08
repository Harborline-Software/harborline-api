namespace Harborline.Api.Foundation.CapabilityAdmission;

/// <summary>One manifest/runtime/readiness join result. It contains no tenant or principal facts.</summary>
public sealed record RuntimeCapabilityState(
    CapabilityKey Key,
    bool Compiled,
    bool Admitted,
    bool Ready,
    IReadOnlyList<string> RefusalCodes);

/// <summary>Applies operator and installation kill switches as vetoes over release admission.</summary>
public static class RuntimeCapabilityDisableFence
{
    /// <summary>
    /// Forces an otherwise effective capability unavailable when either disable control is set.
    /// Clearing both vetoes preserves the catalog verdict; it never manufactures readiness.
    /// </summary>
    public static RuntimeCapabilityState Apply(
        RuntimeCapabilityState catalogState,
        bool operatorDisabled,
        bool globallyDisabled)
    {
        ArgumentNullException.ThrowIfNull(catalogState);
        if (!operatorDisabled && !globallyDisabled)
        {
            return catalogState;
        }

        return catalogState with
        {
            Ready = false,
            RefusalCodes = Array.AsReadOnly(
                catalogState.RefusalCodes
                    .Append(CapabilityRefusalCodes.NotReady)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray()),
        };
    }
}

/// <summary>Deterministic installation-scoped snapshot of current runtime capability admission.</summary>
public sealed record RuntimeCapabilityCatalogSnapshot(
    string ReleaseIdentity,
    string PlatformProfile,
    string CatalogVersion,
    string SnapshotVersion,
    IReadOnlyList<RuntimeCapabilityState> Capabilities)
{
    /// <summary>Finds an exact versioned capability, returning an honest uncompiled state when absent.</summary>
    public RuntimeCapabilityState Find(CapabilityKey key) =>
        Capabilities.FirstOrDefault(x => x.Key == key) ??
        new RuntimeCapabilityState(
            key,
            Compiled: false,
            Admitted: false,
            Ready: false,
            Array.AsReadOnly(new[] { CapabilityRefusalCodes.Uncompiled }));
}

/// <summary>A fail-closed construction error caused by duplicate or smuggled runtime declarations.</summary>
public sealed class CapabilityCatalogAdmissionException : InvalidOperationException
{
    /// <summary>Creates an admission exception carrying a stable refusal code.</summary>
    public CapabilityCatalogAdmissionException(string code, string message) : base(message) => Code = code;

    internal CapabilityCatalogAdmissionException(string code, string message, Exception innerException)
        : base(message, innerException) => Code = code;

    /// <summary>The stable locale-independent refusal code.</summary>
    public string Code { get; }
}
