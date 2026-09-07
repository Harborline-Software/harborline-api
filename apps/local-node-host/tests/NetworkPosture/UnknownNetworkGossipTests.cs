using Microsoft.Extensions.Options;

using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Sync.Gossip;
using Harborline.Api.Kernel.Sync.Handshake;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Network;
using Harborline.Api.Kernel.Sync.Protocol;

namespace Harborline.Api.LocalNodeHost.Tests.NetworkPosture;

public sealed class UnknownNetworkGossipTests
{
    [Fact]
    public async Task UnknownNetwork_ClosesEnrollmentWhileTrustedPeerStillSyncs()
    {
        var endpoint = $"unknown-network-{Guid.NewGuid():N}";
        var signer = new Ed25519Signer();
        var listenerIdentity = Identity(signer, '1');
        var trustedPeerIdentity = Identity(signer, '2');
        var enrollmentHandler = new RecordingEnrollmentHandler();
        var networkTrust = new ConfiguredNetworkTrustState(NetworkTrustLevel.Unknown);

        await using var listenerTransport = new InMemorySyncDaemonTransport(endpoint);
        await using var listener = BuildDaemon(
            listenerTransport,
            listenerIdentity,
            signer,
            new MemberSetTrustPolicy([listenerIdentity.PublicKey, trustedPeerIdentity.PublicKey]),
            enrollmentHandler,
            networkTrust);
        await listener.StartListeningAsync(CancellationToken.None);

        await using (var enrollmentTransport = new InMemorySyncDaemonTransport())
        await using (var enrollmentConnection = await enrollmentTransport.ConnectAsync(endpoint, CancellationToken.None))
        {
            await enrollmentConnection.SendAsync(new EnrollRequestMessage([0xCA, 0xFE]), CancellationToken.None);
            using var closedDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<Exception>(
                () => enrollmentConnection.ReceiveAsync(closedDeadline.Token));
        }

        Assert.Equal(0, enrollmentHandler.CallCount);

        var trustedPing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.FrameReceived += (_, frame) =>
        {
            if (frame.FrameType is GossipFrameType.GossipPing)
            {
                trustedPing.TrySetResult();
            }
        };

        await using var peerTransport = new InMemorySyncDaemonTransport();
        await using var peer = BuildDaemon(
            peerTransport,
            trustedPeerIdentity,
            signer,
            new MemberSetTrustPolicy([listenerIdentity.PublicKey, trustedPeerIdentity.PublicKey]),
            enrollmentHandler: null,
            networkTrust);
        peer.AddPeer(endpoint, listenerIdentity.PublicKey);

        await peer.TriggerPushAsync(OutboundSyncLane.Foreground, CancellationToken.None);
        await trustedPing.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static GossipDaemon BuildDaemon(
        ISyncDaemonTransport transport,
        NodeIdentity identity,
        IEd25519Signer signer,
        IPeerTrustPolicy trustPolicy,
        IPreTrustEnrollmentHandler? enrollmentHandler,
        INetworkTrustState networkTrust) => new(
            transport,
            new VectorClock(),
            Options.Create(new GossipDaemonOptions
            {
                PeerPickCount = 1,
                ConnectTimeoutSeconds = 2,
                DeltaStreamDeadlineSeconds = 2,
            }),
            new InMemoryNodeIdentityProvider(identity),
            signer,
            trustPolicy: trustPolicy,
            enrollmentHandler: enrollmentHandler,
            networkTrust: networkTrust, timeProvider: TimeProvider.System);

    private static NodeIdentity Identity(IEd25519Signer signer, char nodeIdDigit)
    {
        var (publicKey, privateKey) = signer.GenerateKeyPair();
        return new NodeIdentity(new string(nodeIdDigit, 32), publicKey, privateKey);
    }

    private sealed class RecordingEnrollmentHandler : IPreTrustEnrollmentHandler
    {
        public int CallCount { get; private set; }

        public Task<PreTrustEnrollmentResult> HandleAsync(
            byte[] requestPayload,
            string source,
            CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(PreTrustEnrollmentResult.Accept([0x01]));
        }
    }
}
