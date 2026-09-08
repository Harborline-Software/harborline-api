using Microsoft.Extensions.Options;

using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Sync.Application;
using Harborline.Api.Kernel.Sync.Gossip;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Protocol;

namespace Harborline.Api.LocalNodeHost.Tests.Sync;

public sealed class OutboundSyncLaneIsolationTests
{
    [Fact]
    public void DefaultLaneBudgets_PreserveSinglePushConcurrency()
    {
        var options = new GossipDaemonOptions();

        Assert.Equal(1, options.ForegroundPushConcurrency);
        Assert.Equal(1, options.BackgroundPushConcurrency);
    }

    [Fact]
    public void TriggerPush_RequiresExplicitLaneAssignment()
    {
        var method = Assert.Single(
            typeof(IGossipDaemon).GetMethods(),
            candidate => candidate.Name == nameof(IGossipDaemon.TriggerPushAsync));

        Assert.Equal(
            [typeof(OutboundSyncLane), typeof(CancellationToken)],
            method.GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public async Task ConfiguredBackgroundBudget_AdmitsTwoBackgroundPushes()
    {
        var endpoint = $"outbound-budget-{Guid.NewGuid():N}";
        await using var initiatorTransport = new InMemorySyncDaemonTransport($"{endpoint}-initiator");
        await using var responderTransport = new InMemorySyncDaemonTransport(endpoint);
        var signer = new Ed25519Signer();
        var blockingProducer = new FirstTwoCallsBlockingDeltaProducer();
        await using var initiator = BuildDaemon(
            initiatorTransport,
            Identity(signer, '3'),
            signer,
            blockingProducer,
            options => options.BackgroundPushConcurrency = 2);
        await using var responder = BuildDaemon(
            responderTransport,
            Identity(signer, '4'),
            signer,
            new NoopDeltaProducer());
        await responder.StartListeningAsync(CancellationToken.None);
        initiator.AddPeer(endpoint, Array.Empty<byte>());

        var first = initiator.TriggerPushAsync(
            OutboundSyncLane.Background,
            CancellationToken.None);
        await blockingProducer.FirstCallEntered.WaitAsync(TimeSpan.FromSeconds(2));
        var second = initiator.TriggerPushAsync(
            OutboundSyncLane.Background,
            CancellationToken.None);

        try
        {
            await blockingProducer.SecondCallEntered.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            blockingProducer.ReleaseCalls();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task ScheduledRound_ConsumesBackgroundBudget()
    {
        var endpoint = $"scheduled-background-{Guid.NewGuid():N}";
        await using var initiatorTransport = new InMemorySyncDaemonTransport($"{endpoint}-initiator");
        await using var responderTransport = new InMemorySyncDaemonTransport(endpoint);
        var signer = new Ed25519Signer();
        var blockingProducer = new FirstTwoCallsBlockingDeltaProducer();
        await using var initiator = BuildDaemon(
            initiatorTransport,
            Identity(signer, '5'),
            signer,
            blockingProducer);
        await using var responder = BuildDaemon(
            responderTransport,
            Identity(signer, '6'),
            signer,
            new NoopDeltaProducer());
        await responder.StartListeningAsync(CancellationToken.None);
        initiator.AddPeer(endpoint, Array.Empty<byte>());

        await initiator.StartAsync(CancellationToken.None);
        await blockingProducer.FirstCallEntered.WaitAsync(TimeSpan.FromSeconds(2));
        var background = initiator.TriggerPushAsync(
            OutboundSyncLane.Background,
            CancellationToken.None);

        try
        {
            await background.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(blockingProducer.SecondCallEntered.IsCompleted);
        }
        finally
        {
            blockingProducer.ReleaseCalls();
            await initiator.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SaturatedBackgroundLane_DoesNotDelayForegroundPush()
    {
        var endpoint = $"outbound-lanes-{Guid.NewGuid():N}";
        await using var initiatorTransport = new InMemorySyncDaemonTransport($"{endpoint}-initiator");
        await using var responderTransport = new InMemorySyncDaemonTransport(endpoint);
        var signer = new Ed25519Signer();
        var blockingProducer = new FirstCallBlockingDeltaProducer();
        await using var initiator = BuildDaemon(
            initiatorTransport,
            Identity(signer, '1'),
            signer,
            blockingProducer);
        await using var responder = BuildDaemon(
            responderTransport,
            Identity(signer, '2'),
            signer,
            new NoopDeltaProducer());
        await responder.StartListeningAsync(CancellationToken.None);
        initiator.AddPeer(endpoint, Array.Empty<byte>());

        var background = initiator.TriggerPushAsync(
            OutboundSyncLane.Background,
            CancellationToken.None);
        await blockingProducer.FirstCallEntered.WaitAsync(TimeSpan.FromSeconds(2));

        var foreground = initiator.TriggerPushAsync(
            OutboundSyncLane.Foreground,
            CancellationToken.None);
        await foreground.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(background.IsCompleted);
        blockingProducer.ReleaseFirstCall();
        await background.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static GossipDaemon BuildDaemon(
        ISyncDaemonTransport transport,
        NodeIdentity identity,
        IEd25519Signer signer,
        IDeltaProducer producer,
        Action<GossipDaemonOptions>? configure = null)
    {
        var options = new GossipDaemonOptions
        {
            PeerPickCount = 1,
            ConnectTimeoutSeconds = 5,
            DeltaStreamDeadlineSeconds = 5,
        };
        configure?.Invoke(options);
        return new GossipDaemon(
            transport,
            new VectorClock(),
            Options.Create(options),
            new InMemoryNodeIdentityProvider(identity),
            signer,
            producer, timeProvider: TimeProvider.System);
    }

    private static NodeIdentity Identity(IEd25519Signer signer, char digit)
    {
        var (publicKey, privateKey) = signer.GenerateKeyPair();
        return new NodeIdentity(new string(digit, 32), publicKey, privateKey);
    }

    private sealed class FirstCallBlockingDeltaProducer : IDeltaProducer
    {
        private readonly TaskCompletionSource _firstCallEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstCall = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task FirstCallEntered => _firstCallEntered.Task;

        public async ValueTask<ReadOnlyMemory<byte>?> EncodeOutboundDeltaAsync(
            string documentId,
            ReadOnlyMemory<byte> peerVectorClock,
            CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                _firstCallEntered.TrySetResult();
                await _releaseFirstCall.Task.WaitAsync(ct);
            }

            return null;
        }

        public void ReleaseFirstCall() => _releaseFirstCall.TrySetResult();
    }

    private sealed class FirstTwoCallsBlockingDeltaProducer : IDeltaProducer
    {
        private readonly TaskCompletionSource _firstCallEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondCallEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseCalls = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task FirstCallEntered => _firstCallEntered.Task;

        public Task SecondCallEntered => _secondCallEntered.Task;

        public async ValueTask<ReadOnlyMemory<byte>?> EncodeOutboundDeltaAsync(
            string documentId,
            ReadOnlyMemory<byte> peerVectorClock,
            CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                _firstCallEntered.TrySetResult();
            }
            else if (call == 2)
            {
                _secondCallEntered.TrySetResult();
            }

            await _releaseCalls.Task.WaitAsync(ct);
            return null;
        }

        public void ReleaseCalls() => _releaseCalls.TrySetResult();
    }
}
