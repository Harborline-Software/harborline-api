namespace Harborline.Api.Kernel.Sync.Network;

/// <summary>
/// The operator-declared trust level of the network currently carrying node traffic.
/// </summary>
public enum NetworkTrustLevel
{
    /// <summary>The network is not known to the operator and must receive the restrictive posture.</summary>
    Unknown,

    /// <summary>The operator has explicitly marked the network as known.</summary>
    Known,
}

/// <summary>
/// Supplies the node's explicit current-network trust posture to network-facing components.
/// </summary>
public interface INetworkTrustState
{
    /// <summary>Gets the current operator-declared network trust level.</summary>
    NetworkTrustLevel Current { get; }
}

/// <summary>An immutable <see cref="INetworkTrustState"/> backed by durable host configuration.</summary>
/// <param name="current">The configured trust level.</param>
public sealed class ConfiguredNetworkTrustState(NetworkTrustLevel current) : INetworkTrustState
{
    /// <inheritdoc />
    public NetworkTrustLevel Current { get; } = current;
}
