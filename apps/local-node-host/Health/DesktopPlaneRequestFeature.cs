namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Request-local proof that listener admission authenticated the caller as the desktop operator.
/// </summary>
/// <remarks>
/// This is a positive plane assertion, not an identity context holder. The bootstrap bearer and the legacy
/// founder credential are the only listener branches allowed to publish it. Selected-session and
/// device principals never do, and a request that bypasses listener attribution has no marker.
/// </remarks>
internal sealed class DesktopPlaneRequestFeature
{
    internal static DesktopPlaneRequestFeature Instance { get; } = new();

    private DesktopPlaneRequestFeature()
    {
    }
}
