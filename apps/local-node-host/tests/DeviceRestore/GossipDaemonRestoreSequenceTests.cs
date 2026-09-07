using Microsoft.Extensions.Options;

using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Sync.Application;
using Harborline.Api.Kernel.Sync.Gossip;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Protocol;
using Harborline.Api.Kernel.Sync.Restore;

namespace Harborline.Api.LocalNodeHost.Tests.DeviceRestore;

public sealed class GossipDaemonRestoreSequenceTests
{
    [Fact]
    public async Task OutboundDelta_UsesRestoredDurableSequenceAllocator()
    {
        var endpoint = $"restore-sequence-{Guid.NewGuid():N}";
        await using var transportA = new InMemorySyncDaemonTransport($"{endpoint}-initiator");
        await using var transportB = new InMemorySyncDaemonTransport(endpoint);
        var signer = new Ed25519Signer();
        var sink = new CapturingSink();
        var allocator = new FixedSequenceAllocator(77);
        var clockA = new VectorClock();
        await using var daemonA = BuildDaemon(
            transportA,
            Identity(signer, 'a'),
            signer,
            new OneByteDeltaProducer(),
            new NoopDeltaSink(),
            allocator,
            clockA);
        await using var daemonB = BuildDaemon(
            transportB,
            Identity(signer, 'b'),
            signer,
            new NoopDeltaProducer(),
            sink,
            new FixedSequenceAllocator(1));

        await daemonB.StartListeningAsync(CancellationToken.None);
        daemonA.AddPeer(endpoint, Array.Empty<byte>());
        await daemonA.TriggerPushAsync(OutboundSyncLane.Background, CancellationToken.None);

        Assert.Equal(77UL, Assert.Single(sink.Sequences));
        Assert.Equal(new string('a', 32), Assert.Single(allocator.NodeIds));
        Assert.Equal(77UL, clockA.Get(new string('a', 32)));
    }

    private static GossipDaemon BuildDaemon(
        ISyncDaemonTransport transport,
        NodeIdentity identity,
        IEd25519Signer signer,
        IDeltaProducer producer,
        IDeltaSink sink,
        IOutboundSequenceAllocator allocator,
        VectorClock? clock = null) =>
        new(
            transport,
            clock ?? new VectorClock(),
            Options.Create(new GossipDaemonOptions
            {
                PeerPickCount = 1,
                ConnectTimeoutSeconds = 5,
                DeadPeerBackoffSeconds = 1,
            }),
            new InMemoryNodeIdentityProvider(identity),
            signer,
            producer,
            sink,
            outboundSequenceAllocator: allocator, timeProvider: TimeProvider.System);

    private static NodeIdentity Identity(IEd25519Signer signer, char nodeIdDigit)
    {
        var (publicKey, privateKey) = signer.GenerateKeyPair();
        return new NodeIdentity(new string(nodeIdDigit, 32), publicKey, privateKey);
    }

    private sealed class FixedSequenceAllocator(ulong sequence) : IOutboundSequenceAllocator
    {
        public List<string> NodeIds { get; } = new();

        public ValueTask<ulong> ReserveNextAsync(string nodeId, CancellationToken ct = default)
        {
            NodeIds.Add(nodeId);
            return ValueTask.FromResult(sequence);
        }
    }

    private sealed class OneByteDeltaProducer : IDeltaProducer
    {
        public ValueTask<ReadOnlyMemory<byte>?> EncodeOutboundDeltaAsync(
            string documentId,
            ReadOnlyMemory<byte> peerVectorClock,
            CancellationToken ct) =>
            ValueTask.FromResult<ReadOnlyMemory<byte>?>(new byte[] { 1 });
    }

    private sealed class CapturingSink : IDeltaSink
    {
        public List<ulong> Sequences { get; } = new();

        public ValueTask ApplyInboundDeltaAsync(
            string documentId,
            ulong opSequence,
            ReadOnlyMemory<byte> delta,
            CancellationToken ct)
        {
            Sequences.Add(opSequence);
            return ValueTask.CompletedTask;
        }
    }
}
