using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Kernel.Sync.Discovery;
using Harborline.Api.Kernel.Sync.Protocol;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost.Tests.Installation;

public sealed class RosterScopedPeerDiscoveryTests
{
    [Fact]
    public async Task CoResidentInstalls_InDifferentRosters_DoNotDiscoverEachOther()
    {
        var broker = new InMemoryPeerDiscoveryBroker();
        await using IPeerDiscovery tenant1Dev = new InMemoryPeerDiscovery(broker, new PeerDiscoveryOptions());
        await using IPeerDiscovery tenant1Qa = new InMemoryPeerDiscovery(broker, new PeerDiscoveryOptions());
        var devAdvertisement = Advertisement("dev-node", "roster-dev");
        var qaAdvertisement = Advertisement("qa-node", "roster-qa");

        await tenant1Dev.StartAsync(devAdvertisement, CancellationToken.None);
        await tenant1Qa.StartAsync(qaAdvertisement, CancellationToken.None);

        Assert.Empty(tenant1Dev.KnownPeers);
        Assert.Empty(tenant1Qa.KnownPeers);
    }

    [Fact]
    public async Task CrossMachineLanPeers_InSameRoster_DiscoverEachOther()
    {
        var anchor = new TeamTrustAnchor(
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            "founder",
            "known-genesis-public-key");
        var broker = new InMemoryPeerDiscoveryBroker();
        await using IPeerDiscovery firstMachine = new InMemoryPeerDiscovery(broker, new PeerDiscoveryOptions());
        await using IPeerDiscovery secondMachine = new InMemoryPeerDiscovery(broker, new PeerDiscoveryOptions());

        await firstMachine.StartAsync(Advertisement("machine-a", anchor.RosterId), CancellationToken.None);
        await secondMachine.StartAsync(Advertisement("machine-b", anchor.RosterId), CancellationToken.None);

        Assert.Equal(
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa:known-genesis-public-key",
            anchor.RosterId);
        Assert.Equal("machine-b", Assert.Single(firstMachine.KnownPeers).NodeId);
        Assert.Equal("machine-a", Assert.Single(secondMachine.KnownPeers).NodeId);
    }

    [Fact]
    public void ProductionRosterAdvertisement_CarriesTheImmutableRosterScope()
    {
        var keyPair = KeyPair.Generate();
        var signer = new Harborline.Api.Foundation.Crypto.Ed25519Signer(keyPair);
        var memberRoster = MemberRoster.Genesis(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "founder",
            signer,
            new Ed25519Verifier(),
            DateTimeOffset.UnixEpoch,
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        var roster = new NodeTeamRoster(memberRoster);
        var transport = new TcpSyncDaemonTransport("tcp://127.0.0.1:7473");

        var advertisement = TcpPeerAdvertisement.ForTransport(
            transport,
            nodeId: "node-a",
            teamPublicKey: new byte[32],
            teamId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            schemaVersion: "1",
            rosterId: roster.DiscoveryRosterId);

        Assert.NotEmpty(advertisement.RosterId);
        Assert.Equal(roster.DiscoveryRosterId, advertisement.RosterId);
    }

    private static PeerAdvertisement Advertisement(string nodeId, string rosterId) =>
        new(
            NodeId: nodeId,
            Endpoint: $"tcp://{nodeId}:7473",
            PublicKey: new byte[32],
            TeamId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            SchemaVersion: "1",
            Metadata: new Dictionary<string, string>(),
            RosterId: rosterId);
}
