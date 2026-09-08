namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Request-local proof that listener admission is operating in the explicitly un-enforced
/// single-host mode. Callers cannot construct or publish this feature.
/// </summary>
/// <remarks>
/// This marker admits selected-session product audiences for dev and Bridge-spawned tenant-child
/// parity. It is deliberately distinct from <see cref="DesktopPlaneRequestFeature"/> and therefore
/// cannot satisfy desktop-only or founder-web-admission fences.
/// </remarks>
internal sealed class SingleHostTrustedRequestFeature
{
    internal static SingleHostTrustedRequestFeature Instance { get; } = new();

    private SingleHostTrustedRequestFeature()
    {
    }
}
