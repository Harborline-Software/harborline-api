using Microsoft.Extensions.Options;

using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Sync.Gossip;
using Harborline.Api.Kernel.Sync.Handshake;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Protocol;

namespace Harborline.Api.LocalNodeHost.Tests.Resilience;

public sealed class GossipRetryPolicyTests
{
    [Fact]
    public async Task SimultaneouslyRefusedPeers_GetSpreadRetryTimes()
    {
        var now = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var signer = new Ed25519Signer();
        var (publicKey, privateKey) = signer.GenerateKeyPair();
        await using var daemon = new GossipDaemon(
            new RefusingTransport(),
            new VectorClock(),
            Options.Create(new GossipDaemonOptions
            {
                PeerPickCount = 12,
                DeadPeerBackoffSeconds = 60,
            }),
            new InMemoryNodeIdentityProvider(new NodeIdentity(
                new string('1', 32),
                publicKey,
                privateKey)),
            signer,
            timeProvider: clock,
            random: new Random(2749));
        for (var i = 0; i < 12; i++)
        {
            daemon.AddPeer($"refused-{i}", publicKey);
        }

        await daemon.TriggerPushAsync(OutboundSyncLane.Background, CancellationToken.None);

        var retryTimes = daemon.KnownPeers
            .Select(peer => Assert.IsType<DateTimeOffset>(peer.BackoffUntil))
            .Order()
            .ToArray();
        Assert.Equal(12, retryTimes.Distinct().Count());
        Assert.True(
            retryTimes[^1] - retryTimes[0] >= TimeSpan.FromSeconds(20),
            $"Expected at least 20 seconds of retry spread, observed {retryTimes[^1] - retryTimes[0]}.");
    }

    [Fact]
    public async Task LowTrafficFailures_DoNotOpenBreaker_UntilMinimumThroughput()
    {
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        var signer = new Ed25519Signer();
        var (publicKey, privateKey) = signer.GenerateKeyPair();
        await using var daemon = new GossipDaemon(
            new RefusingTransport(),
            new VectorClock(),
            Options.Create(new GossipDaemonOptions
            {
                PeerPickCount = 1,
                DeadPeerBackoffSeconds = 10,
                CircuitBreakerSamplingWindowSeconds = 300,
                CircuitBreakerMinimumThroughput = 4,
                CircuitBreakerFailureRatio = 1.0,
            }),
            new InMemoryNodeIdentityProvider(new NodeIdentity(
                new string('1', 32),
                publicKey,
                privateKey)),
            signer,
            timeProvider: clock,
            random: new Random(2749));
        daemon.AddPeer("refused", publicKey);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await daemon.TriggerPushAsync(OutboundSyncLane.Background, CancellationToken.None);
            var peer = Assert.Single(daemon.KnownPeers);
            Assert.Equal(attempt, peer.ConsecutiveFailures);
            Assert.Equal(PeerCircuitState.Closed, peer.CircuitState);
            clock.Advance(peer.BackoffUntil!.Value - clock.GetUtcNow() + TimeSpan.FromMilliseconds(1));
        }

        clock.Advance(TimeSpan.FromSeconds(301));
        await daemon.TriggerPushAsync(OutboundSyncLane.Background, CancellationToken.None);
        Assert.Equal(PeerCircuitState.Closed, Assert.Single(daemon.KnownPeers).CircuitState);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var peer = Assert.Single(daemon.KnownPeers);
            clock.Advance(peer.BackoffUntil!.Value - clock.GetUtcNow() + TimeSpan.FromMilliseconds(1));
            await daemon.TriggerPushAsync(OutboundSyncLane.Background, CancellationToken.None);
        }
        Assert.Equal(PeerCircuitState.Open, Assert.Single(daemon.KnownPeers).CircuitState);
    }

    [Fact]
    public async Task SuccessfulHalfOpenProbe_ClosesCircuitAndResetsBackoff()
    {
        var endpoint = $"retry-probe-{Guid.NewGuid():N}";
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        var signer = new Ed25519Signer();
        var (localPublicKey, localPrivateKey) = signer.GenerateKeyPair();
        var (peerPublicKey, peerPrivateKey) = signer.GenerateKeyPair();
        await using var transport = new GatedTransport($"{endpoint}-initiator");
        await using var responderTransport = new InMemorySyncDaemonTransport(endpoint);
        await using var daemon = new GossipDaemon(
            transport,
            new VectorClock(),
            Options.Create(new GossipDaemonOptions
            {
                PeerPickCount = 1,
                DeadPeerBackoffSeconds = 10,
                CircuitBreakerSamplingWindowSeconds = 300,
                CircuitBreakerMinimumThroughput = 4,
                CircuitBreakerFailureRatio = 1.0,
            }),
            new InMemoryNodeIdentityProvider(new NodeIdentity(
                new string('1', 32),
                localPublicKey,
                localPrivateKey)),
            signer,
            timeProvider: clock,
            random: new Random(2749));
        daemon.AddPeer(endpoint, peerPublicKey);

        var ceilings = new[] { 10, 20, 40, 40 };
        for (var attempt = 0; attempt < ceilings.Length; attempt++)
        {
            await daemon.TriggerPushAsync(OutboundSyncLane.Background, CancellationToken.None);
            var peer = Assert.Single(daemon.KnownPeers);
            var delay = peer.BackoffUntil!.Value - clock.GetUtcNow();
            Assert.InRange(
                delay,
                TimeSpan.FromSeconds(ceilings[attempt] / 2d),
                TimeSpan.FromSeconds(ceilings[attempt]));
            if (attempt < ceilings.Length - 1)
            {
                clock.Advance(delay + TimeSpan.FromMilliseconds(1));
            }
        }

        var openPeer = Assert.Single(daemon.KnownPeers);
        Assert.Equal(PeerCircuitState.Open, openPeer.CircuitState);
        clock.Advance(openPeer.BackoffUntil!.Value - clock.GetUtcNow() + TimeSpan.FromMilliseconds(1));
        transport.AllowConnections();
        var responder = RespondToOneRoundAsync(
            responderTransport,
            new LocalIdentity(
                new string('2', 32).Chunk(2).Select(pair => Convert.ToByte(new string(pair), 16)).ToArray(),
                peerPublicKey,
                signer,
                peerPrivateKey,
                HandshakeProtocol.DefaultSchemaVersion,
                HandshakeProtocol.DefaultSupportedVersions,
                Capabilities: [SyncCapabilities.DeltaStateVector]),
            clock);

        var probe = daemon.TriggerPushAsync(OutboundSyncLane.Background, CancellationToken.None);
        await transport.ProbeEntered.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(PeerCircuitState.HalfOpen, Assert.Single(daemon.KnownPeers).CircuitState);

        transport.ReleaseProbe();
        await probe;
        await responder;

        var recovered = Assert.Single(daemon.KnownPeers);
        Assert.Equal(PeerCircuitState.Closed, recovered.CircuitState);
        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.Null(recovered.BackoffUntil);
    }

    [Fact]
    public async Task HealthySinglePeer_DefaultsExchangeOnFirstPush()
    {
        var endpoint = $"healthy-default-{Guid.NewGuid():N}";
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        var signer = new Ed25519Signer();
        var (localPublicKey, localPrivateKey) = signer.GenerateKeyPair();
        var (peerPublicKey, peerPrivateKey) = signer.GenerateKeyPair();
        await using var initiatorTransport = new InMemorySyncDaemonTransport($"{endpoint}-initiator");
        await using var responderTransport = new InMemorySyncDaemonTransport(endpoint);
        await using var daemon = new GossipDaemon(
            initiatorTransport,
            new VectorClock(),
            Options.Create(new GossipDaemonOptions()),
            new InMemoryNodeIdentityProvider(new NodeIdentity(
                new string('1', 32),
                localPublicKey,
                localPrivateKey)),
            signer,
            timeProvider: clock);
        daemon.AddPeer(endpoint, peerPublicKey);
        var responder = RespondToOneRoundAsync(
            responderTransport,
            new LocalIdentity(
                new string('2', 32).Chunk(2).Select(pair => Convert.ToByte(new string(pair), 16)).ToArray(),
                peerPublicKey,
                signer,
                peerPrivateKey,
                HandshakeProtocol.DefaultSchemaVersion,
                HandshakeProtocol.DefaultSupportedVersions,
                Capabilities: [SyncCapabilities.DeltaStateVector]),
            clock);
        GossipRoundCompletedEventArgs? completed = null;
        daemon.RoundCompleted += (_, round) => completed = round;

        await daemon.TriggerPushAsync(OutboundSyncLane.Background, CancellationToken.None);
        await responder;

        Assert.NotNull(completed);
        Assert.Equal(1, completed.PeersSelected);
        Assert.Equal(1, completed.DeltasExchanged);
        var peer = Assert.Single(daemon.KnownPeers);
        Assert.Equal(clock.GetUtcNow(), peer.LastSeenAt);
        Assert.Equal(PeerCircuitState.Closed, peer.CircuitState);
        Assert.Equal(0, peer.ConsecutiveFailures);
        Assert.Null(peer.BackoffUntil);
    }

    private static async Task RespondToOneRoundAsync(
        InMemorySyncDaemonTransport transport,
        LocalIdentity identity,
        TimeProvider timeProvider)
    {
        await foreach (var connection in transport.ListenAsync(CancellationToken.None))
        {
            await using (connection)
            {
                await HandshakeProtocol.RespondAsync(
                    connection,
                    identity,
                    proposal => new AckMessage(
                        proposal.ProposedStreams,
                        Array.Empty<Rejection>(),
                        proposal.Capabilities),
                    CancellationToken.None,
                    timeProvider: timeProvider);
                _ = Assert.IsType<GossipPingMessage>(
                    await connection.ReceiveAsync(CancellationToken.None));
                _ = Assert.IsType<DeltaStreamMessage>(
                    await connection.ReceiveAsync(CancellationToken.None));
                await connection.SendAsync(
                    new GossipPingMessage(
                        new Dictionary<string, ulong>(),
                        new MembershipDelta(Array.Empty<byte[]>(), Array.Empty<byte[]>()),
                        MonotonicNonce: 1),
                    CancellationToken.None);
                await connection.SendAsync(
                    new DeltaStreamMessage("default", 1, Array.Empty<byte>()),
                    CancellationToken.None);
                return;
            }
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class RefusingTransport : ISyncDaemonTransport
    {
        public Task<ISyncDaemonConnection> ConnectAsync(string peerEndpoint, CancellationToken ct) =>
            Task.FromException<ISyncDaemonConnection>(new IOException("Connection refused."));

        public async IAsyncEnumerable<ISyncDaemonConnection> ListenAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class GatedTransport(string endpoint) : ISyncDaemonTransport
    {
        private readonly InMemorySyncDaemonTransport _inner = new(endpoint);
        private readonly TaskCompletionSource _probeEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _probeReleased = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _refuse = true;

        public Task ProbeEntered => _probeEntered.Task;

        public void AllowConnections() => _refuse = false;

        public void ReleaseProbe() => _probeReleased.TrySetResult();

        public async Task<ISyncDaemonConnection> ConnectAsync(
            string peerEndpoint,
            CancellationToken ct)
        {
            if (_refuse)
            {
                throw new IOException("Connection refused.");
            }

            _probeEntered.TrySetResult();
            await _probeReleased.Task.WaitAsync(ct);
            return await _inner.ConnectAsync(peerEndpoint, ct);
        }

        public IAsyncEnumerable<ISyncDaemonConnection> ListenAsync(CancellationToken ct) =>
            _inner.ListenAsync(ct);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
