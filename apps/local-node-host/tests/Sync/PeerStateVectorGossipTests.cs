using System.Collections.Concurrent;
using System.Diagnostics;

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
    // These fixtures measure delta contents/size, not handshake latency. Match the existing
    // load-aware deadline convention without changing either production handshake default.
    private static readonly TimeSpan HandshakeBudget = LoadCeiling.Pick(
        quiet: TimeSpan.FromSeconds(5), busy: TimeSpan.FromSeconds(30));
    private static readonly TimeSpan ConvergenceCeiling = LoadCeiling.Pick(
        quiet: TimeSpan.FromSeconds(5), busy: TimeSpan.FromSeconds(45));
    // Handshake (5/30 s), the unchanged 30 s delta deadline, then 10/15 s completion headroom.
    private static readonly TimeSpan RoundCompletionCeiling = LoadCeiling.Pick(
        quiet: TimeSpan.FromSeconds(45), busy: TimeSpan.FromSeconds(75));

    [Fact]
    public async Task SecondRound_AfterOneOperation_SendsOperationSizedDelta()
    {
        var endpoint = $"state-vector-gossip-{Guid.NewGuid():N}";
        await using var transportA = new InMemorySyncDaemonTransport($"{endpoint}-initiator");
        await using var transportB = new InMemorySyncDaemonTransport(endpoint);
        await using var replicaA = new CrdtReplica();
        await using var replicaB = new CrdtReplica();
        var signer = new Ed25519Signer();
        var frames = new ConcurrentQueue<string>();
        await using var daemonA = BuildDaemon(transportA, Identity(signer, 'a'), signer, replicaA, frames);
        await using var daemonB = BuildDaemon(transportB, Identity(signer, 'b'), signer, replicaB, frames);
        daemonA.FrameReceived += (_, frame) => frames.Enqueue($"A:{frame.FrameType}:{frame.Summary}");
        daemonB.FrameReceived += (_, frame) => frames.Enqueue($"B:{frame.FrameType}:{frame.Summary}");

        for (var i = 0; i < 1_000; i++)
        {
            replicaA.Text.Insert(replicaA.Text.Length, $"history-entry-{i:D4};");
        }

        await daemonB.StartListeningAsync(CancellationToken.None);
        daemonA.AddPeer(endpoint, Array.Empty<byte>());
        await RunRoundAsync(
            ct => daemonA.TriggerPushAsync(OutboundSyncLane.Background, ct),
            () => string.Join(Environment.NewLine, frames));

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
        await RunRoundAsync(
            ct => daemonA.TriggerPushAsync(OutboundSyncLane.Background, ct),
            () => string.Join(Environment.NewLine, frames));

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
        using var legacyStop = new CancellationTokenSource();
        var log = new ConcurrentQueue<string>();
        await using var daemonA = BuildDaemon(transportA, identityA, signer, replicaA, log);

        for (var i = 0; i < 1_000; i++)
        {
            replicaA.Text.Insert(replicaA.Text.Length, $"legacy-history-{i:D4};");
        }

        daemonA.AddPeer(endpoint, identityB.PublicKey);
        Task<(GossipPingMessage Ping, DeltaStreamMessage Delta)>? legacyRound = null;
        string? primaryFailure = null;
        try
        {
            // Spawn only inside the protected region, after all daemon/replica setup.
            legacyRound = RunLegacyRoundAsync(
                transportB,
                new LocalIdentity(
                    identityB.NodeIdBytes, identityB.PublicKey, signer, identityB.PrivateKey,
                    HandshakeProtocol.DefaultSchemaVersion, HandshakeProtocol.DefaultSupportedVersions),
                replicaB, legacyStop.Token);
            legacyStop.CancelAfter(RoundCompletionCeiling);
            await RunRoundAsync(
                ct => daemonA.TriggerPushAsync(OutboundSyncLane.Background, ct),
                () => string.Join(Environment.NewLine, log));
            var received = await legacyRound.WaitAsync(RoundCompletionCeiling);

            Assert.Empty(received.Ping.StateVectors);
            Assert.True(received.Delta.CrdtOps.Length > 4_096,
                $"Expected full history for the legacy peer, got {received.Delta.CrdtOps.Length} bytes.");
            Assert.Equal(replicaA.Text.Value, replicaB.Text.Value);
        }
        catch (Xunit.Sdk.XunitException failure)
        {
            primaryFailure = failure.ToString();
            throw;
        }
        catch (TimeoutException failure)
        {
            primaryFailure = failure.ToString();
            throw;
        }
        finally
        {
            await legacyStop.CancelAsync();
            if (legacyRound is not null)
            {
                await ObserveResponderCleanupAsync(legacyRound, primaryFailure);
            }
        }
    }

    private static GossipDaemon BuildDaemon(
        ISyncDaemonTransport transport,
        NodeIdentity identity,
        IEd25519Signer signer,
        CrdtReplica replica,
        ConcurrentQueue<string> log) =>
        new(
            transport,
            new VectorClock(),
            Options.Create(new GossipDaemonOptions
            {
                PeerPickCount = 1,
                ConnectTimeoutSeconds = (int)HandshakeBudget.TotalSeconds,
                InboundHandshakeDeadlineSeconds = (int)HandshakeBudget.TotalSeconds,
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
        CrdtReplica replica, CancellationToken ct)
    {
        await foreach (var connection in transport.ListenAsync(ct))
        {
            await using (connection)
            {
                await HandshakeProtocol.RespondAsync(
                    connection,
                    identity,
                    proposal => new AckMessage(proposal.ProposedStreams, Array.Empty<Rejection>()),
                    ct, timeProvider: TimeProvider.System);
                var ping = Assert.IsType<GossipPingMessage>(
                    await connection.ReceiveAsync(ct));
                var delta = Assert.IsType<DeltaStreamMessage>(
                    await connection.ReceiveAsync(ct));
                await replica.ApplyInboundDeltaAsync(
                    delta.StreamId,
                    delta.OpSequence,
                    delta.CrdtOps,
                    ct);
                await connection.SendAsync(
                    new GossipPingMessage(
                        new Dictionary<string, ulong>(),
                        new MembershipDelta(Array.Empty<byte[]>(), Array.Empty<byte[]>()),
                        MonotonicNonce: 1),
                    ct);
                await connection.SendAsync(
                    new DeltaStreamMessage("default", 1, Array.Empty<byte>()),
                    ct);
                return (ping, delta);
            }
        }

        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Legacy responder did not receive a connection.");
    }

    [Fact]
    public async Task Unfinished_round_is_canceled_and_fails_with_diagnostics()
    {
        CancellationToken roundToken = default;
        Task? unfinished = null;
        var drained = false;
        var failure = await Assert.ThrowsAsync<Xunit.Sdk.FailException>(() => RunRoundAsync(
            ct =>
            {
                roundToken = ct;
                return unfinished = DelayedCleanupAsync(ct);
            },
            () => "planted unfinished round", TimeSpan.FromMilliseconds(100)));

        Assert.Contains("planted unfinished round", failure.Message, StringComparison.Ordinal);
        Assert.True(roundToken.IsCancellationRequested);
        Assert.True(drained);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unfinished!);

        async Task DelayedCleanupAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            finally
            {
                await Task.Delay(50, CancellationToken.None);
                drained = true;
            }
        }
    }

    [Fact]
    public async Task A_round_that_swallows_cancellation_still_fails()
    {
        var completedNormally = false;
        var failure = await Assert.ThrowsAsync<Xunit.Sdk.FailException>(() => RunRoundAsync(
            async ct =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Models TriggerPushAsync returning normally after cancellation.
                }
                completedNormally = true;
            },
            () => "planted swallowed cancellation", TimeSpan.FromMilliseconds(100)));
        Assert.Contains("planted swallowed cancellation", failure.Message, StringComparison.Ordinal);
        Assert.True(completedNormally);
    }

    [Fact]
    public async Task Cancellation_cleanup_fault_preserves_the_round_failure()
    {
        var failure = await Assert.ThrowsAsync<Xunit.Sdk.FailException>(() => RunRoundAsync(
            async ct =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                finally
                {
                    throw new InvalidOperationException("planted cleanup fault");
                }
            },
            () => "planted expired round", TimeSpan.FromMilliseconds(100)));
        Assert.Contains("planted expired round", failure.Message, StringComparison.Ordinal);
        Assert.Contains("planted cleanup fault", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Cleanup fault", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Canceling_before_a_legacy_connection_drains_as_cancellation()
    {
        await using var transport = new InMemorySyncDaemonTransport($"legacy-cancel-{Guid.NewGuid():N}");
        await using var replica = new CrdtReplica();
        var signer = new Ed25519Signer();
        var identity = Identity(signer, 'e');
        using var stop = new CancellationTokenSource();
        var responder = RunLegacyRoundAsync(transport,
            new LocalIdentity(identity.NodeIdBytes, identity.PublicKey, signer, identity.PrivateKey,
                HandshakeProtocol.DefaultSchemaVersion, HandshakeProtocol.DefaultSupportedVersions),
            replica, stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => responder.WaitAsync(ConvergenceCeiling));
    }

    [Fact]
    public async Task Missing_convergence_still_fails_with_diagnostics()
    {
        var failure = await Assert.ThrowsAsync<Xunit.Sdk.FailException>(() => WaitForAsync(
            () => false, () => "planted missing delta", TimeSpan.FromMilliseconds(100)));
        Assert.Contains("planted missing delta", failure.Message, StringComparison.Ordinal);
    }

    private static async Task RunRoundAsync(
        Func<CancellationToken, Task> push, Func<string> diagnostics, TimeSpan? ceiling = null)
    {
        var budget = ceiling ?? RoundCompletionCeiling;
        using var stop = new CancellationTokenSource(budget);
        var elapsed = Stopwatch.StartNew();
        Task? pushTask = null;
        try
        {
            pushTask = push(stop.Token);
            await Task.WhenAny(pushTask, Task.Delay(budget, stop.Token));
            if (pushTask.IsCompletedSuccessfully && !stop.IsCancellationRequested && elapsed.Elapsed < budget)
            {
                return;
            }

            var primary = $"Gossip round did not complete successfully within {budget}";
            if (pushTask.Exception is { } fault)
            {
                primary += $"{Environment.NewLine}Operation fault: {fault}";
            }
            await stop.CancelAsync();
            var cleanup = await DrainAsync(pushTask);
            Assert.Fail($"{primary}: {diagnostics()}{Environment.NewLine}{cleanup}");
        }
        finally
        {
            await stop.CancelAsync();
        }
    }

    private static async Task<string> DrainAsync(Task operation)
    {
        using var drainStop = new CancellationTokenSource();
        try
        {
            await Task.WhenAny(operation, Task.Delay(ConvergenceCeiling, drainStop.Token));
            if (!operation.IsCompleted)
            {
                // The drain timed out, so report the live operation and observe any later fault.
                _ = operation.ContinueWith(static task => { _ = task.Exception; },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return $"Cleanup did not complete within {ConvergenceCeiling}.";
            }

            // Reading Exception observes faults; cancellation is an expected cleanup outcome.
            return operation.Exception is { } fault
                ? $"Cleanup fault: {fault}" : "Cleanup completed.";
        }
        finally
        {
            await drainStop.CancelAsync();
        }
    }

    private static async Task ObserveResponderCleanupAsync(Task responder, string? primaryFailure)
    {
        var cleanup = await DrainAsync(responder);
        if (!responder.IsCompleted || responder.IsFaulted)
        {
            Assert.Fail($"{primaryFailure ?? "Legacy responder cleanup failed."}{Environment.NewLine}{cleanup}");
        }
    }

    private static async Task WaitForAsync(
        Func<bool> condition, Func<string> timeoutMessage, TimeSpan? ceiling = null)
    {
        using var timeout = new CancellationTokenSource(ceiling ?? ConvergenceCeiling);
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

    private sealed class CaptureLogger(ConcurrentQueue<string> messages) : ILogger<GossipDaemon>
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
            messages.Enqueue($"LOG:{logLevel}:{formatter(state, exception)}:{exception}");
        }
    }
}
