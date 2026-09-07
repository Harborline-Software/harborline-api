using Harborline.Api.Kernel.Sync.Discovery;
using Harborline.Api.Kernel.Sync.Network;

namespace Harborline.Api.LocalNodeHost.Tests.NetworkPosture;

public sealed class NetworkTrustPeerDiscoveryTests
{
    [Fact]
    public async Task UnknownNetwork_DoesNotStartMdnsAdvertisement()
    {
        var mdns = new RecordingPeerDiscovery();
        await using IPeerDiscovery discovery = new NetworkTrustPeerDiscovery(
            mdns,
            new ConfiguredNetworkTrustState(NetworkTrustLevel.Unknown));

        await discovery.StartAsync(SelfAdvertisement(), CancellationToken.None);

        Assert.Equal(0, mdns.StartCount);
        Assert.Empty(discovery.KnownPeers);
    }

    private static PeerAdvertisement SelfAdvertisement() => new(
        NodeId: "node-a",
        Endpoint: "tcp://192.0.2.10:7473",
        PublicKey: [0x01, 0x02],
        TeamId: "team-a",
        SchemaVersion: "1.0.0",
        Metadata: new Dictionary<string, string>(),
        RosterId: "roster-a");

    private sealed class RecordingPeerDiscovery : IPeerDiscovery
    {
        public int StartCount { get; private set; }

        public IReadOnlyCollection<PeerAdvertisement> KnownPeers => Array.Empty<PeerAdvertisement>();

        public event EventHandler<PeerDiscoveredEventArgs>? PeerDiscovered
        {
            add { }
            remove { }
        }

        public event EventHandler<PeerLostEventArgs>? PeerLost
        {
            add { }
            remove { }
        }

        public Task StartAsync(PeerAdvertisement self, CancellationToken ct)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
