using Microsoft.Extensions.Options;

using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Sync.Gossip;
using Harborline.Api.Kernel.Sync.Handshake;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Protocol;

namespace Harborline.Api.LocalNodeHost.Tests.Sync;

public sealed class DeltaStreamDeadlineTests
{
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
        var observed = await deadlineError.Task.WaitAsync(TimeSpan.FromSeconds(3));

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
