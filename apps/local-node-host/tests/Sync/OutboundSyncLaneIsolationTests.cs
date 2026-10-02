using Microsoft.Extensions.Options;

using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Sync.Application;
using Harborline.Api.Kernel.Sync.Gossip;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Protocol;

namespace Harborline.Api.LocalNodeHost.Tests.Sync;

[Collection(OutboundSyncLaneTimingCollection.Name)]
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
    public Task SaturatedBackgroundLane_DoesNotDelayForegroundPush() =>
        AssertIndependentPushAsync(OutboundSyncLane.Foreground);

    [Fact]
    public async Task IsolationAssertion_RejectsPushCoalescedIntoBlockedBackgroundLane()
    {
        // Negative control through the public lane-assignment seam: the occupied
        // background lane coalesces this call instead of executing a second producer.
        var failure = await Assert.ThrowsAsync<Xunit.Sdk.TrueException>(() =>
            AssertIndependentPushAsync(OutboundSyncLane.Background));
        Assert.Contains("Foreground push must enter the producer independently", failure.Message);
    }

    [Fact]
    public async Task IsolationTimeout_CancelsAndDrainsBothPushesBeforeDisposal()
    {
        bool? backgroundCompletedBeforeDisposal = null;
        bool? candidateCompletedBeforeDisposal = null;
        var failure = await Assert.ThrowsAsync<TimeoutException>(() =>
            AssertIndependentPushAsync(OutboundSyncLane.Foreground,
                blockSecondCall: true,
                awaitCandidate: async (_, entered) =>
                {
                    await entered.WaitAsync(TimeSpan.FromSeconds(2));
                    throw new TimeoutException("Injected foreground timeout after producer entry.");
                },
                observeCompletedBeforeDisposal: (first, second) =>
                    (backgroundCompletedBeforeDisposal, candidateCompletedBeforeDisposal) = (first, second)));

        Assert.Equal("Injected foreground timeout after producer entry.", failure.Message);
        Assert.Equal(true, backgroundCompletedBeforeDisposal);
        Assert.Equal(true, candidateCompletedBeforeDisposal);
    }

    [Fact]
    public async Task IsolationCleanupFailure_CannotSatisfyExpectedCoalescingAssertion()
    {
        var failure = await Assert.ThrowsAsync<AggregateException>(() =>
            AssertIndependentPushAsync(OutboundSyncLane.Background,
                injectCleanupFailure: () => throw new TimeoutException("Injected cleanup failure.")));

        Assert.Equal(2, failure.InnerExceptions.Count);
        var primary = Assert.IsType<Xunit.Sdk.TrueException>(failure.InnerExceptions[0]);
        Assert.Contains("Foreground push must enter the producer independently", primary.Message);
        var cleanup = Assert.IsType<TimeoutException>(failure.InnerExceptions[1]);
        Assert.Equal("Injected cleanup failure.", cleanup.Message);
    }

    private static async Task AssertIndependentPushAsync(
        OutboundSyncLane candidateLane,
        bool blockSecondCall = false,
        Func<Task, Task, Task>? awaitCandidate = null,
        Action<bool, bool>? observeCompletedBeforeDisposal = null,
        Action? injectCleanupFailure = null)
    {
        var endpoint = $"outbound-lanes-{Guid.NewGuid():N}";
        await using var initiatorTransport = new InMemorySyncDaemonTransport($"{endpoint}-initiator");
        await using var responderTransport = new InMemorySyncDaemonTransport(endpoint);
        var signer = new Ed25519Signer();
        var blockingProducer = new FirstCallBlockingDeltaProducer(blockSecondCall);
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

        using var pushCancellation = new CancellationTokenSource();
        var background = initiator.TriggerPushAsync(
            OutboundSyncLane.Background,
            pushCancellation.Token);
        Task? candidate = null;
        Exception? primaryFailure = null;
        try
        {
            await blockingProducer.FirstCallEntered.WaitAsync(TimeSpan.FromSeconds(2));

            candidate = initiator.TriggerPushAsync(candidateLane, pushCancellation.Token);
            if (awaitCandidate is null)
            {
                await candidate.WaitAsync(TimeSpan.FromSeconds(1));
            }
            else
            {
                await awaitCandidate(candidate, blockingProducer.SecondCallEntered);
            }

            Assert.False(background.IsCompleted);
            Assert.True(blockingProducer.SecondCallEntered.IsCompleted,
                "Foreground push must enter the producer independently while background is blocked.");
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            blockingProducer.ReleaseFirstCall();
            if (primaryFailure is not null || candidate is { IsCompleted: false })
            {
                pushCancellation.Cancel();
            }

            var pushes = candidate is null ? background : Task.WhenAll(background, candidate);
            try
            {
                await pushes.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.True(background.IsCompleted, "Background push must terminate before disposal.");
                Assert.True(candidate is null || candidate.IsCompleted,
                    "Foreground push must terminate before disposal.");
                observeCompletedBeforeDisposal?.Invoke(background.IsCompleted,
                    candidate is null || candidate.IsCompleted);
                injectCleanupFailure?.Invoke();
            }
            catch (Exception cleanupFailure)
            {
                if (primaryFailure is not null)
                {
                    throw new AggregateException("Push cleanup failed after the primary test failure.",
                        primaryFailure, cleanupFailure);
                }

                throw;
            }
        }
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

    private sealed class FirstCallBlockingDeltaProducer(bool blockSecondCall) : IDeltaProducer
    {
        private readonly TaskCompletionSource _firstCallEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondCallEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstCall = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task FirstCallEntered => _firstCallEntered.Task;
        public Task SecondCallEntered => _secondCallEntered.Task;

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
            else
            {
                _secondCallEntered.TrySetResult();
                if (blockSecondCall)
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
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
