using Microsoft.Extensions.Options;

using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Sync.Gossip;
using Harborline.Api.Kernel.Sync.Handshake;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Protocol;

namespace Harborline.Api.LocalNodeHost.Tests.Sync;

public sealed class DeltaStreamDeadlineTests
{
    /// <summary>
    /// T-987: how long the test waits for the named deadline error, under the two-ceiling convention
    /// (<see cref="LoadCeiling"/>). The wait covers the handshake plus the 1 s deadline, which the
    /// daemon arms on the real clock (<c>CancelAfter</c>) once the handshake completes, so a fake clock
    /// cannot drive it. The quiet ceiling keeps the original 3 s. The busy ceiling is derived, not tuned:
    /// a working daemon emits the error no later than the 30 s connect timeout (which bounds connect and
    /// handshake) plus the 1 s deadline, so 45 s lies past every on-time arrival. With the thread pool
    /// starved, the base test timed out 5 of 5 runs; the error's continuation alone reached the test
    /// 20-25 s late; under that load the fixed test passed 20 of 20 in 24.7-28.6 s. The handler takes only <see cref="ErrorCode.DeltaStreamDeadlineExceeded"/>, so no
    /// other failure can satisfy the wait, and a daemon that never names the deadline still fails.
    /// </summary>
    private static readonly TimeSpan DeadlineErrorCeiling = LoadCeiling.Pick(
        quiet: TimeSpan.FromSeconds(3),
        busy: TimeSpan.FromSeconds(45));

    [Fact]
    public async Task StalledDeltaStream_EmitsNamedDeadlineError()
    {
        var endpoint = $"delta-deadline-{Guid.NewGuid():N}";
        await using var initiatorTransport = new InMemorySyncDaemonTransport($"{endpoint}-initiator");
        await using var responderTransport = new InMemorySyncDaemonTransport(endpoint);
        using var responderStop = new CancellationTokenSource();
        var signer = new Ed25519Signer();
        var initiatorIdentity = Identity(signer, '1');
        var responderIdentity = HandshakeIdentity(signer, '2');
        var responderTask = StallAfterHandshakeAsync(
            responderTransport,
            responderIdentity,
            responderStop.Token);

        await using var daemon = new GossipDaemon(
            initiatorTransport,
            new VectorClock(),
            Options.Create(new GossipDaemonOptions
            {
                PeerPickCount = 1,
                ConnectTimeoutSeconds = 30,
                DeltaStreamDeadlineSeconds = 1,
                DeadPeerBackoffSeconds = 1,
            }),
            new InMemoryNodeIdentityProvider(initiatorIdentity),
            signer, timeProvider: TimeProvider.System);
        daemon.AddPeer(endpoint, responderIdentity.PublicKey);
        var deadlineError = new TaskCompletionSource<GossipFrameEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        daemon.FrameReceived += (_, frame) =>
        {
            if (frame.ErrorCode == ErrorCode.DeltaStreamDeadlineExceeded)
            {
                deadlineError.TrySetResult(frame);
            }
        };

        var push = daemon.TriggerPushAsync(OutboundSyncLane.Background, CancellationToken.None);
        var observed = await deadlineError.Task.WaitAsync(DeadlineErrorCeiling);

        Assert.Equal(GossipFrameType.GossipError, observed.FrameType);
        Assert.Contains("delta stream deadline", observed.Summary, StringComparison.OrdinalIgnoreCase);
        await push;

        responderStop.Cancel();
        await responderTask;
    }

    private static async Task StallAfterHandshakeAsync(
        InMemorySyncDaemonTransport transport,
        LocalIdentity identity,
        CancellationToken ct)
    {
        try
        {
            await foreach (var connection in transport.ListenAsync(ct))
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
                        ct, timeProvider: TimeProvider.System);
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private static NodeIdentity Identity(IEd25519Signer signer, char digit)
    {
        var (publicKey, privateKey) = signer.GenerateKeyPair();
        return new NodeIdentity(new string(digit, 32), publicKey, privateKey);
    }

    private static LocalIdentity HandshakeIdentity(IEd25519Signer signer, char digit)
    {
        var identity = Identity(signer, digit);
        return new LocalIdentity(
            identity.NodeIdBytes,
            identity.PublicKey,
            signer,
            identity.PrivateKey,
            HandshakeProtocol.DefaultSchemaVersion,
            HandshakeProtocol.DefaultSupportedVersions,
            Capabilities: [SyncCapabilities.DeltaStateVector]);
    }
}
