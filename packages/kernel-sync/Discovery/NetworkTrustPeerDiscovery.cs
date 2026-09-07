using Harborline.Api.Kernel.Sync.Network;

namespace Harborline.Api.Kernel.Sync.Discovery;

/// <summary>
/// Prevents a peer-discovery implementation from advertising or browsing while the current network is unknown.
/// </summary>
/// <remarks>
/// The decorator is the network-posture boundary around mDNS. A known network delegates the complete
/// <see cref="IPeerDiscovery"/> contract; an unknown network leaves the inner discovery service unstarted and
/// exposes no peers. Changing the durable posture requires a host restart, matching configuration reload behavior.
/// </remarks>
public sealed class NetworkTrustPeerDiscovery : IPeerDiscovery
{
    private readonly IPeerDiscovery _inner;
    private readonly INetworkTrustState _networkTrust;
    private bool _started;

    /// <summary>Creates a trust-scoped wrapper over <paramref name="inner"/>.</summary>
    public NetworkTrustPeerDiscovery(IPeerDiscovery inner, INetworkTrustState networkTrust)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _networkTrust = networkTrust ?? throw new ArgumentNullException(nameof(networkTrust));
    }

    /// <inheritdoc />
    public IReadOnlyCollection<PeerAdvertisement> KnownPeers =>
        _networkTrust.Current is NetworkTrustLevel.Known
            ? _inner.KnownPeers
            : Array.Empty<PeerAdvertisement>();

    /// <inheritdoc />
    public event EventHandler<PeerDiscoveredEventArgs>? PeerDiscovered
    {
        add => _inner.PeerDiscovered += value;
        remove => _inner.PeerDiscovered -= value;
    }

    /// <inheritdoc />
    public event EventHandler<PeerLostEventArgs>? PeerLost
    {
        add => _inner.PeerLost += value;
        remove => _inner.PeerLost -= value;
    }

    /// <inheritdoc />
    public async Task StartAsync(PeerAdvertisement self, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(self);
        if (_networkTrust.Current is not NetworkTrustLevel.Known)
        {
            return;
        }

        await _inner.StartAsync(self, ct).ConfigureAwait(false);
        _started = true;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct)
    {
        if (!_started)
        {
            return;
        }

        await _inner.StopAsync(ct).ConfigureAwait(false);
        _started = false;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
