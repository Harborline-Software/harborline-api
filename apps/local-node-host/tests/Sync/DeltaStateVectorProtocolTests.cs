using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Sync.Handshake;
using Harborline.Api.Kernel.Sync.Protocol;

namespace Harborline.Api.LocalNodeHost.Tests.Sync;

public sealed class DeltaStateVectorProtocolTests
{
    [Fact]
    public void GossipPing_RoundTrips_PerDocumentStateVectors()
    {
        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["contacts"] = [0x01, 0xA4, 0x7F],
            ["comms"] = [0x02, 0x19],
        };
        var message = new GossipPingMessage(
            VectorClock: new Dictionary<string, ulong> { ["node-a"] = 7 },
            PeerMembershipDelta: new MembershipDelta(Array.Empty<byte[]>(), Array.Empty<byte[]>()),
            MonotonicNonce: 11,
            StateVectors: expected);

        var decoded = GossipPingMessage.FromCbor(message.ToCbor());

        Assert.Equal(expected.Keys.Order(), decoded.StateVectors.Keys.Order());
        Assert.Equal(expected["contacts"], decoded.StateVectors["contacts"]);
        Assert.Equal(expected["comms"], decoded.StateVectors["comms"]);
    }

    [Fact]
    public async Task Handshake_Negotiates_DeltaStateVectorCapability()
    {
        var endpoint = $"state-vector-handshake-{Guid.NewGuid():N}";
        await using var responderTransport = new InMemorySyncDaemonTransport(endpoint);
        await using var initiatorTransport = new InMemorySyncDaemonTransport();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var signer = new Ed25519Signer();
        var initiator = Identity(signer, "11111111111111111111111111111111");
        var responder = Identity(signer, "22222222222222222222222222222222");

        var responderTask = Task.Run(async () =>
        {
            await foreach (var connection in responderTransport.ListenAsync(timeout.Token))
            {
                await using (connection)
                {
                    return await HandshakeProtocol.RespondAsync(
                        connection,
                        responder,
                        proposal => new AckMessage(
                            proposal.ProposedStreams,
                            Array.Empty<Rejection>(),
                            GrantedCapabilities: proposal.Capabilities
                                .Where(capability => capability == SyncCapabilities.DeltaStateVector)
                                .ToArray()),
                        timeout.Token, timeProvider: TimeProvider.System);
                }
            }

            throw new InvalidOperationException("Responder did not accept the handshake connection.");
        }, timeout.Token);

        await using var initiatorConnection = await initiatorTransport.ConnectAsync(endpoint, timeout.Token);
        var initiatorResult = await HandshakeProtocol.InitiateAsync(
            initiatorConnection,
            initiator,
            timeout.Token, timeProvider: TimeProvider.System);
        var responderResult = await responderTask;

        Assert.Contains(SyncCapabilities.DeltaStateVector, initiatorResult.GrantedCapabilities);
        Assert.Contains(SyncCapabilities.DeltaStateVector, responderResult.GrantedCapabilities);
    }

    [Fact]
    public void DefaultSupportedVersions_RetainsCurrentAndPreviousVersion()
    {
        Assert.Equal(
            ["1.0.0", "0.9.0"],
            HandshakeProtocol.DefaultSupportedVersions);
    }

    private static LocalIdentity Identity(IEd25519Signer signer, string nodeId)
    {
        var (publicKey, privateKey) = signer.GenerateKeyPair();
        return new LocalIdentity(
            Convert.FromHexString(nodeId),
            publicKey,
            signer,
            privateKey,
            HandshakeProtocol.DefaultSchemaVersion,
            HandshakeProtocol.DefaultSupportedVersions,
            Capabilities: [SyncCapabilities.DeltaStateVector]);
    }
}
