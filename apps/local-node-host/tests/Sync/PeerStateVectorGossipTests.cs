using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Sync.Application;
using Harborline.Api.Kernel.Sync.Gossip;
using Harborline.Api.Kernel.Sync.Handshake;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Protocol;

namespace Harborline.Api.LocalNodeHost.Tests.Sync;

public sealed class PeerStateVectorGossipTests
{
    [Fact]
    public async Task SecondRound_AfterOneOperation_SendsOperationSizedDelta()
    {
        var endpoint = $"state-vector-gossip-{Guid.NewGuid():N}";
        await using var transportA = new InMemorySyncDaemonTransport($"{endpoint}-initiator");
        await using var transportB = new InMemorySyncDaemonTransport(endpoint);
        await using var replicaA = new CrdtReplica();
        await using var replicaB = new CrdtReplica();
        var signer = new Ed25519Signer();
        var frames = new List<string>();
        await using var daemonA = BuildDaemon(transportA, Identity(signer, 'a'), signer, replicaA, frames);
        await using var daemonB = BuildDaemon(transportB, Identity(signer, 'b'), signer, replicaB, frames);
        daemonA.FrameReceived += (_, frame) => frames.Add($"A:{frame.FrameType}:{frame.Summary}");
        daemonB.FrameReceived += (_, frame) => frames.Add($"B:{frame.FrameType}:{frame.Summary}");

        for (var i = 0; i < 1_000; i++)
        {
            replicaA.Text.Insert(replicaA.Text.Length, $"history-entry-{i:D4};");
        }

        await daemonB.StartListeningAsync(CancellationToken.None);
        daemonA.AddPeer(endpoint, Array.Empty<byte>());
        await daemonA.TriggerPushAsync(OutboundSyncLane.Background, CancellationToken.None);

        Assert.True(replicaA.SentDeltas.Count > 0, string.Join(Environment.NewLine, frames));
        Assert.True(replicaA.SentDeltas[0].Length > 4_096,
            $"First delta was {replicaA.SentDeltas[0].Length} bytes.{Environment.NewLine}{string.Join(Environment.NewLine, frames)}");
        await WaitForAsync(
            () => replicaB.Text.Value == replicaA.Text.Value,
            () => $"A={replicaA.Text.Length}, B={replicaB.Text.Length}, sent={string.Join(',', replicaA.SentDeltas.Select(d => d.Length))}{Environment.NewLine}{string.Join(Environment.NewLine, frames)}");
        Assert.Equal(replicaA.Text.Value, replicaB.Text.Value);
        var firstRoundBytes = Assert.Single(replicaA.SentDeltas).Length;
        Assert.True(firstRoundBytes > 4_096, $"Expected a substantial first history transfer, got {firstRoundBytes} bytes.");

        replicaA.Text.Insert(replicaA.Text.Length, "one-new-operation");
        await daemonA.TriggerPushAsync(OutboundSyncLane.Background, CancellationToken.None);

        await WaitForAsync(
            () => replicaB.Text.Value == replicaA.Text.Value,
            () => $"A={replicaA.Text.Length}, B={replicaB.Text.Length}, sent={string.Join(',', replicaA.SentDeltas.Select(d => d.Length))}{Environment.NewLine}{string.Join(Environment.NewLine, frames)}");
        Assert.Equal(replicaA.Text.Value, replicaB.Text.Value);
        Assert.Equal(2, replicaA.SentDeltas.Count);
        var secondRoundBytes = replicaA.SentDeltas[1].Length;
        Assert.True(secondRoundBytes < 256, $"Expected one-operation delta below 256 bytes, got {secondRoundBytes}.");
        Assert.True(secondRoundBytes * 16 < firstRoundBytes,
            $"Expected operation-sized second delta; first={firstRoundBytes}, second={secondRoundBytes}.");
    }

    [Fact]
    public async Task PeerWithoutCapability_ReceivesFullHistoryAndSyncs()
    {
        var endpoint = $"legacy-full-history-{Guid.NewGuid():N}";
        await using var transportA = new InMemorySyncDaemonTransport($"{endpoint}-initiator");
        await using var transportB = new InMemorySyncDaemonTransport(endpoint);
        await using var replicaA = new CrdtReplica();
        await using var replicaB = new CrdtReplica();
        var signer = new Ed25519Signer();
        var identityA = Identity(signer, 'c');
        var identityB = Identity(signer, 'd');
        var legacyRound = RunLegacyRoundAsync(
            transportB,
            new LocalIdentity(
                identityB.NodeIdBytes,
                identityB.PublicKey,
                signer,
                identityB.PrivateKey,
                HandshakeProtocol.DefaultSchemaVersion,
                HandshakeProtocol.DefaultSupportedVersions),
            replicaB);
        var log = new List<string>();
        await using var daemonA = BuildDaemon(transportA, identityA, signer, replicaA, log);

        for (var i = 0; i < 1_000; i++)
        {
            replicaA.Text.Insert(replicaA.Text.Length, $"legacy-history-{i:D4};");
        }

        daemonA.AddPeer(endpoint, identityB.PublicKey);
        await daemonA.TriggerPushAsync(OutboundSyncLane.Background, CancellationToken.None);
        var received = await legacyRound.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(received.Ping.StateVectors);
        Assert.True(received.Delta.CrdtOps.Length > 4_096,
            $"Expected full history for the legacy peer, got {received.Delta.CrdtOps.Length} bytes.");
        Assert.Equal(replicaA.Text.Value, replicaB.Text.Value);
    }

    private static GossipDaemon BuildDaemon(
        ISyncDaemonTransport transport,
        NodeIdentity identity,
        IEd25519Signer signer,
        CrdtReplica replica,
        IList<string> log) =>
        new(
            transport,
            new VectorClock(),
            Options.Create(new GossipDaemonOptions
            {
                PeerPickCount = 1,
                ConnectTimeoutSeconds = 5,
                DeadPeerBackoffSeconds = 1,
            }),
            new InMemoryNodeIdentityProvider(identity),
            signer,
            replica,
            replica,
            logger: new CaptureLogger(log), timeProvider: TimeProvider.System);

    private static NodeIdentity Identity(IEd25519Signer signer, char nodeIdDigit)
    {
        var (publicKey, privateKey) = signer.GenerateKeyPair();
        return new NodeIdentity(new string(nodeIdDigit, 32), publicKey, privateKey);
    }

    private static async Task<(GossipPingMessage Ping, DeltaStreamMessage Delta)> RunLegacyRoundAsync(
        InMemorySyncDaemonTransport transport,
        LocalIdentity identity,
        CrdtReplica replica)
    {
        await foreach (var connection in transport.ListenAsync(CancellationToken.None))
        {
            await using (connection)
            {
                await HandshakeProtocol.RespondAsync(
                    connection,
                    identity,
                    proposal => new AckMessage(proposal.ProposedStreams, Array.Empty<Rejection>()),
                    CancellationToken.None, timeProvider: TimeProvider.System);
                var ping = Assert.IsType<GossipPingMessage>(
                    await connection.ReceiveAsync(CancellationToken.None));
                var delta = Assert.IsType<DeltaStreamMessage>(
                    await connection.ReceiveAsync(CancellationToken.None));
                await replica.ApplyInboundDeltaAsync(
                    delta.StreamId,
                    delta.OpSequence,
                    delta.CrdtOps,
                    CancellationToken.None);
                await connection.SendAsync(
                    new GossipPingMessage(
                        new Dictionary<string, ulong>(),
                        new MembershipDelta(Array.Empty<byte[]>(), Array.Empty<byte[]>()),
                        MonotonicNonce: 1),
                    CancellationToken.None);
                await connection.SendAsync(
                    new DeltaStreamMessage("default", 1, Array.Empty<byte>()),
                    CancellationToken.None);
                return (ping, delta);
            }
        }

        throw new InvalidOperationException("Legacy responder did not receive a connection.");
    }

    private static async Task WaitForAsync(Func<bool> condition, Func<string> timeoutMessage)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (!condition())
            {
                await Task.Delay(20, timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Fail(timeoutMessage());
        }
    }

    private sealed class CrdtReplica : IDeltaProducer, IDeltaStateVectorProvider, IDeltaSink, IAsyncDisposable
    {
        private readonly ICrdtDocument _document = new YDotNetCrdtEngine().CreateDocument("notes");

        public CrdtReplica()
        {
            Text = _document.GetText("body");
        }

        public ICrdtText Text { get; }

        public List<byte[]> SentDeltas { get; } = new();

        public ValueTask<ReadOnlyMemory<byte>?> EncodeOutboundDeltaAsync(
            string documentId,
            ReadOnlyMemory<byte> peerVectorClock,
            CancellationToken ct)
        {
            var delta = _document.EncodeDelta(peerVectorClock);
            SentDeltas.Add(delta.ToArray());
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(delta);
        }

        public ValueTask<ReadOnlyMemory<byte>> GetCurrentStateVectorAsync(
            string documentId,
            CancellationToken ct) =>
            ValueTask.FromResult(_document.VectorClock);

        public ValueTask ApplyInboundDeltaAsync(
            string documentId,
            ulong opSequence,
            ReadOnlyMemory<byte> delta,
            CancellationToken ct)
        {
            _document.ApplyDelta(delta);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => _document.DisposeAsync();
    }

    private sealed class CaptureLogger(IList<string> messages) : ILogger<GossipDaemon>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (messages)
            {
                messages.Add($"LOG:{logLevel}:{formatter(state, exception)}:{exception}");
            }
        }
    }
}
