using System.Formats.Cbor;

using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Sync.Protocol;

namespace Harborline.Api.Kernel.Sync.Handshake;

/// <summary>
/// Sync-daemon-protocol §4 handshake ladder:
/// <c>HELLO ⇄ HELLO → CAPABILITY_NEG → ACK</c>.
/// </summary>
/// <remarks>
/// <para>
/// Wave 2.5 closed the spec §8 hardening gap: HELLO now carries a real
/// Ed25519 <c>hello_sig</c> over a canonical signing payload, and both sides
/// verify the peer's signature against the claimed <see cref="HelloMessage.PublicKey"/>
/// before trusting any handshake state. The ±30 s replay window
/// (sync-daemon-protocol §8 "Replay protection") is enforced against the
/// local wall-clock on HELLO receipt.
/// </para>
/// <para>
/// The signing payload is a canonical CBOR array
/// <c>[node_id, schema_version, public_key, sent_at]</c>. Canonical mode
/// (length-then-lex key ordering, definite-length types) keeps the bytes
/// deterministic across platforms, which is required for any detached
/// signature over structured data. Both sides rebuild the payload
/// independently and sign/verify against the rebuilt bytes — HELLO's wire
/// <c>hello_sig</c> is never trusted as pre-computed bytes.
/// </para>
/// <para>
/// <b>Clock-skew policy.</b> A HELLO whose <c>sent_at</c> falls outside the
/// ±30-second window is rejected with
/// <see cref="ErrorCode.HelloTimestampStale"/> (spec §8). This is a hard
/// reject, not a warn-then-reject at ±120 s — the protocol design already
/// assumes NTP-synchronized peers, and a longer window expands the replay
/// surface without materially helping real-world deployments. Nodes that
/// persistently fail this check have a clock-sync problem and should surface
/// it to operator UX rather than silently accept drift.
/// </para>
/// <para>
/// <b>Concurrent initiate.</b> Two peers that INITIATE against each other
/// simultaneously each send their HELLO first and each read the other's
/// HELLO second — there is no race because HELLO is symmetric and neither
/// side is privileged during it. The CAPABILITY_NEG / ACK step is
/// asymmetric (initiator sends, responder acks), but since each session is
/// a pair of TCP streams (one per direction, or one bidi stream), the
/// out-of-order arrival of a concurrent HELLO does not confuse the state
/// machine — we simply read whichever CAPABILITY_NEG arrives first and
/// respond to it.
/// </para>
/// </remarks>
public static class HandshakeProtocol
{
    /// <summary>Default protocol semver announced in HELLO.</summary>
    public const string DefaultSchemaVersion = "1.0.0";

    /// <summary>
    /// N=2 compatibility window announced in <c>supported_versions</c>:
    /// the current protocol plus its immediate predecessor.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultSupportedVersions = new[] { "1.0.0", "0.9.0" };

    /// <summary>
    /// Replay-protection window for HELLO (sync-daemon-protocol §8).
    /// A receiver rejects any HELLO whose <c>sent_at</c> falls outside
    /// <c>±HelloTimestampSkewSeconds</c> of its own wall-clock.
    /// </summary>
    public const int HelloTimestampSkewSeconds = 30;

    /// <summary>
    /// Initiator side of the handshake. Sends HELLO, reads the peer's HELLO,
    /// verifies the peer's Ed25519 signature + timestamp, sends
    /// CAPABILITY_NEG, reads ACK, returns the negotiated
    /// <see cref="CapabilityResult"/>.
    /// </summary>
    public static async Task<CapabilityResult> InitiateAsync(
        ISyncDaemonConnection connection,
        LocalIdentity localIdentity,
        CancellationToken ct,
        IPeerTrustPolicy? trustPolicy = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(localIdentity);
        var clock = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        // Step 1: initiator sends HELLO (signed).
        var hello = BuildHello(localIdentity, clock.GetUtcNow());
        await connection.SendAsync(hello, ct).ConfigureAwait(false);

        // Step 2: receive peer's HELLO.
        var inbound = await connection.ReceiveAsync(ct).ConfigureAwait(false);
        if (inbound is not HelloMessage peerHello)
        {
            throw new InvalidOperationException(
                $"Expected HELLO from peer, got {inbound.GetType().Name}.");
        }

        // Step 2a: replay-window gate. Stale or future-dated HELLOs are
        // rejected before we even attempt signature verification so a
        // replayed-but-validly-signed HELLO cannot burn CPU on the receiver.
        if (!IsTimestampWithinWindow(peerHello.Timestamp, clock, out var nowTs))
        {
            var err = new ErrorMessage(
                ErrorCode.HelloTimestampStale,
                $"Peer sent_at={peerHello.Timestamp} outside ±{HelloTimestampSkewSeconds}s of local clock {nowTs}.",
                Recoverable: false);
            await connection.SendAsync(err, ct).ConfigureAwait(false);
            throw new InvalidOperationException("HELLO replay window exceeded; session closed.");
        }

        // Step 2b: verify peer HELLO signature.
        if (!VerifyHelloSignature(peerHello, localIdentity.Signer))
        {
            var err = new ErrorMessage(
                ErrorCode.HelloSignatureInvalid,
                "Peer HELLO signature did not verify against the claimed public key.",
                Recoverable: false);
            await connection.SendAsync(err, ct).ConfigureAwait(false);
            throw new InvalidOperationException("HELLO signature invalid; session closed.");
        }

        // Step 2b': trust gate (INC-3). Signature verification proved the peer
        // owns the key it presented; this gate decides whether that key is one
        // we trust (shared-root TOFU). Fail-closed — an untrusted peer is
        // rejected before we commit any further session state.
        if (!IsPeerTrusted(peerHello, trustPolicy))
        {
            var err = new ErrorMessage(
                ErrorCode.PeerUntrusted,
                "Peer HELLO public key is not trusted (does not chain to the shared root).",
                Recoverable: false);
            await connection.SendAsync(err, ct).ConfigureAwait(false);
            throw new InvalidOperationException("Peer untrusted; session closed.");
        }

        // Step 2c: schema-version screen; full negotiation is Wave 3.
        if (!NegotiateSchemaVersion(peerHello, localIdentity, out var agreedVersion))
        {
            var err = new ErrorMessage(
                ErrorCode.SchemaVersionIncompatible,
                $"Peer announced '{peerHello.SchemaVersion}', we support '{string.Join(",", localIdentity.SupportedVersions)}'.",
                Recoverable: false);
            await connection.SendAsync(err, ct).ConfigureAwait(false);
            throw new InvalidOperationException("Schema-version mismatch; session closed.");
        }

        // Step 3: initiator sends CAPABILITY_NEG.
        var proposal = new CapabilityNegMessage(
            ProposedStreams: localIdentity.ProposedStreams ?? Array.Empty<string>(),
            CpLeases: localIdentity.CpLeases ?? Array.Empty<string>(),
            BucketSubscriptions: localIdentity.BucketSubscriptions ?? Array.Empty<BucketRequest>(),
            Capabilities: localIdentity.Capabilities ?? Array.Empty<string>());
        await connection.SendAsync(proposal, ct).ConfigureAwait(false);

        // Step 4: receive ACK.
        var ackInbound = await connection.ReceiveAsync(ct).ConfigureAwait(false);
        if (ackInbound is not AckMessage ack)
        {
            throw new InvalidOperationException(
                $"Expected ACK from peer, got {ackInbound.GetType().Name}.");
        }

        return new CapabilityResult(
            PeerNodeId: peerHello.NodeId,
            PeerPublicKey: peerHello.PublicKey,
            AgreedSchemaVersion: agreedVersion,
            Granted: ack.GrantedSubscriptions,
            Rejected: ack.Rejected,
            TickIntervalSeconds: ack.TickIntervalSeconds,
            MaxDeltasPerSecond: ack.MaxDeltasPerSecond)
        {
            GrantedCapabilities = ack.GrantedCapabilities,
        };
    }

    /// <summary>
    /// Responder side of the handshake. Reads peer's HELLO, verifies its
    /// signature + timestamp, sends our HELLO, reads CAPABILITY_NEG,
    /// evaluates <paramref name="policy"/>, sends ACK, returns the resulting
    /// <see cref="CapabilityResult"/>.
    /// </summary>
    public static async Task<CapabilityResult> RespondAsync(
        ISyncDaemonConnection connection,
        LocalIdentity localIdentity,
        Func<CapabilityNegMessage, AckMessage> policy,
        CancellationToken ct,
        IPeerTrustPolicy? trustPolicy = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(localIdentity);
        ArgumentNullException.ThrowIfNull(policy);

        // Step 1: receive peer's HELLO.
        var inbound = await connection.ReceiveAsync(ct).ConfigureAwait(false);
        if (inbound is not HelloMessage peerHello)
        {
            throw new InvalidOperationException(
                $"Expected HELLO from initiator, got {inbound.GetType().Name}.");
        }

        return await RespondToHelloAsync(connection, localIdentity, peerHello, policy, ct, trustPolicy, timeProvider)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Responder side of the handshake when the initiator's HELLO has ALREADY been read off the wire by the
    /// caller (#1301 F-1). The accept loop reads the FIRST frame itself to discriminate a PRE-TRUST
    /// <c>ENROLL_REQUEST</c> from a trusted <c>HELLO</c>; when it is a HELLO it hands the already-read message
    /// here so the responder ladder does NOT double-read the first frame. Identical behavior to
    /// <see cref="RespondAsync"/> from Step 1a onward — the only difference is who performed the initial receive.
    /// </summary>
    public static async Task<CapabilityResult> RespondToHelloAsync(
        ISyncDaemonConnection connection,
        LocalIdentity localIdentity,
        HelloMessage peerHello,
        Func<CapabilityNegMessage, AckMessage> policy,
        CancellationToken ct,
        IPeerTrustPolicy? trustPolicy = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(localIdentity);
        ArgumentNullException.ThrowIfNull(peerHello);
        ArgumentNullException.ThrowIfNull(policy);
        var clock = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        // Step 1a: replay-window gate before sending anything that commits
        //          state to the session.
        if (!IsTimestampWithinWindow(peerHello.Timestamp, clock, out var nowTs))
        {
            var err = new ErrorMessage(
                ErrorCode.HelloTimestampStale,
                $"Initiator sent_at={peerHello.Timestamp} outside ±{HelloTimestampSkewSeconds}s of local clock {nowTs}.",
                Recoverable: false);
            await connection.SendAsync(err, ct).ConfigureAwait(false);
            throw new InvalidOperationException("HELLO replay window exceeded; session closed.");
        }

        // Step 1b: verify the initiator's HELLO signature before emitting
        //          any of our own state onto the wire.
        if (!VerifyHelloSignature(peerHello, localIdentity.Signer))
        {
            var err = new ErrorMessage(
                ErrorCode.HelloSignatureInvalid,
                "Initiator HELLO signature did not verify against the claimed public key.",
                Recoverable: false);
            await connection.SendAsync(err, ct).ConfigureAwait(false);
            throw new InvalidOperationException("HELLO signature invalid; session closed.");
        }

        // Step 1b': trust gate (INC-3). Reject an untrusted initiator before we
        // emit our own HELLO — fail-closed, no state leaked to a stranger on
        // the LAN that merely presented a self-consistent HELLO.
        if (!IsPeerTrusted(peerHello, trustPolicy))
        {
            var err = new ErrorMessage(
                ErrorCode.PeerUntrusted,
                "Initiator HELLO public key is not trusted (does not chain to the shared root).",
                Recoverable: false);
            await connection.SendAsync(err, ct).ConfigureAwait(false);
            throw new InvalidOperationException("Peer untrusted; session closed.");
        }

        // Step 2: schema-version screen before sending anything that commits
        //         us to a session.
        if (!NegotiateSchemaVersion(peerHello, localIdentity, out var agreedVersion))
        {
            var err = new ErrorMessage(
                ErrorCode.SchemaVersionIncompatible,
                $"Initiator announced '{peerHello.SchemaVersion}', we support '{string.Join(",", localIdentity.SupportedVersions)}'.",
                Recoverable: false);
            await connection.SendAsync(err, ct).ConfigureAwait(false);
            throw new InvalidOperationException("Schema-version mismatch; session closed.");
        }

        // Step 3: responder sends HELLO (signed).
        var hello = BuildHello(localIdentity, clock.GetUtcNow());
        await connection.SendAsync(hello, ct).ConfigureAwait(false);

        // Step 4: receive CAPABILITY_NEG.
        var neg = await connection.ReceiveAsync(ct).ConfigureAwait(false);
        if (neg is not CapabilityNegMessage proposal)
        {
            throw new InvalidOperationException(
                $"Expected CAPABILITY_NEG from initiator, got {neg.GetType().Name}.");
        }

        // Step 5: evaluate policy → build ACK.
        var ack = policy(proposal);
        await connection.SendAsync(ack, ct).ConfigureAwait(false);

        return new CapabilityResult(
            PeerNodeId: peerHello.NodeId,
            PeerPublicKey: peerHello.PublicKey,
            AgreedSchemaVersion: agreedVersion,
            Granted: ack.GrantedSubscriptions,
            Rejected: ack.Rejected,
            TickIntervalSeconds: ack.TickIntervalSeconds,
            MaxDeltasPerSecond: ack.MaxDeltasPerSecond)
        {
            GrantedCapabilities = ack.GrantedCapabilities,
        };
    }

    /// <summary>
    /// Build a HELLO message for the given local identity. <c>hello_sig</c>
    /// is a real detached Ed25519 signature over the canonical signing
    /// payload — the Wave 2.1 zero-byte stub has been removed. Callers
    /// must supply both a <see cref="LocalIdentity.Signer"/> and
    /// <see cref="LocalIdentity.PrivateKey"/>; missing either throws
    /// <see cref="InvalidOperationException"/> so bootstrap paths cannot
    /// accidentally ship unsigned HELLOs.
    /// </summary>
    internal static HelloMessage BuildHello(LocalIdentity identity, DateTimeOffset at)
    {
        if (identity.Signer is null)
        {
            throw new InvalidOperationException(
                "LocalIdentity.Signer is required to build a HELLO — wire an IEd25519Signer via DI.");
        }
        if (identity.PrivateKey is null || identity.PrivateKey.Length == 0)
        {
            throw new InvalidOperationException(
                "LocalIdentity.PrivateKey is required to build a HELLO — load the Ed25519 seed from the node identity provider.");
        }

        var ts = (ulong)at.ToUnixTimeSeconds();
        var payload = BuildSigningPayload(
            identity.NodeId, identity.SchemaVersion, identity.PublicKey, ts);
        var signature = identity.Signer.Sign(payload, identity.PrivateKey);

        return new HelloMessage(
            NodeId: identity.NodeId,
            SchemaVersion: identity.SchemaVersion,
            SupportedVersions: identity.SupportedVersions,
            PublicKey: identity.PublicKey,
            Timestamp: ts,
            Signature: signature);
    }

    /// <summary>
    /// Canonical signing payload used to produce and verify
    /// <see cref="HelloMessage.Signature"/>. Encoded as a canonical CBOR
    /// 4-element array <c>[node_id, schema_version, public_key, sent_at]</c>
    /// so the bytes are deterministic across implementations.
    /// </summary>
    internal static byte[] BuildSigningPayload(
        byte[] nodeId, string schemaVersion, byte[] publicKey, ulong sentAt)
    {
        var writer = new CborWriter(CborConformanceMode.Canonical, convertIndefiniteLengthEncodings: true);
        writer.WriteStartArray(4);
        writer.WriteByteString(nodeId);
        writer.WriteTextString(schemaVersion);
        writer.WriteByteString(publicKey);
        writer.WriteUInt64(sentAt);
        writer.WriteEndArray();
        return writer.Encode();
    }

    /// <summary>
    /// Verify a received HELLO's <c>hello_sig</c> against the payload
    /// rebuilt from its fields and the public key it itself announced.
    /// Returns <c>false</c> on any cryptographic failure, malformed key
    /// material, or absent signer — a valid sync-daemon deployment always
    /// wires an <see cref="IEd25519Signer"/> via DI.
    /// </summary>
    internal static bool VerifyHelloSignature(HelloMessage peerHello, IEd25519Signer? signer)
    {
        if (signer is null)
        {
            return false;
        }

        if (peerHello.Signature is null || peerHello.Signature.Length != signer.SignatureLength)
        {
            return false;
        }
        if (peerHello.PublicKey is null || peerHello.PublicKey.Length != signer.PublicKeyLength)
        {
            return false;
        }

        var payload = BuildSigningPayload(
            peerHello.NodeId, peerHello.SchemaVersion, peerHello.PublicKey, peerHello.Timestamp);
        return signer.Verify(payload, peerHello.Signature, peerHello.PublicKey);
    }

    /// <summary>
    /// Consult the trust gate (INC-3). A <c>null</c> <paramref name="trustPolicy"/>
    /// preserves the pre-INC-3 behavior — every cryptographically-valid peer is
    /// accepted, suitable for deployments with their own out-of-band trust
    /// boundary (local sockets, a team VPN). A non-null policy (e.g.
    /// <see cref="SharedRootTrustPolicy"/>) decides per peer; a rejection
    /// fails the session closed.
    /// </summary>
    internal static bool IsPeerTrusted(HelloMessage peerHello, IPeerTrustPolicy? trustPolicy)
    {
        if (trustPolicy is null)
        {
            return true;
        }
        return trustPolicy.IsTrusted(peerHello);
    }

    /// <summary>
    /// Check whether <paramref name="peerTimestamp"/> falls within
    /// <see cref="HelloTimestampSkewSeconds"/> of the local wall-clock.
    /// The clock is read from <paramref name="timeProvider"/> so callers
    /// (and tests) can pin "now" deterministically; production paths pass
    /// <see cref="TimeProvider.System"/>. Out-parameter <paramref name="localNow"/>
    /// is the local Unix-seconds used for the comparison (exposed for ERROR
    /// message detail).
    /// </summary>
    internal static bool IsTimestampWithinWindow(ulong peerTimestamp, TimeProvider timeProvider, out ulong localNow)
    {
        localNow = (ulong)timeProvider.GetUtcNow().ToUnixTimeSeconds();
        // Cast to long to allow signed-difference calculation; the peer
        // timestamp is unsigned seconds since epoch and will not exceed
        // long.MaxValue for any reasonable calendar date this millennium.
        var delta = (long)peerTimestamp - (long)localNow;
        return Math.Abs(delta) <= HelloTimestampSkewSeconds;
    }

    /// <summary>
    /// Pick the highest common schema version between the peer's offer and
    /// our local support list. Wave 2.1 uses string equality over
    /// <c>SupportedVersions</c> ∩ peer's <c>SupportedVersions</c>; semver
    /// range negotiation is a follow-up.
    /// </summary>
    private static bool NegotiateSchemaVersion(
        HelloMessage peer,
        LocalIdentity local,
        out string agreed)
    {
        agreed = string.Empty;
        var common = local.SupportedVersions
            .Intersect(peer.SupportedVersions, StringComparer.Ordinal)
            .ToList();
        if (common.Count == 0) return false;
        // "Highest" is stringwise-sorted-descending for Wave 2.1 — good enough
        // for matching 1.0.0 entries. Replace with real semver compare when
        // minor/patch axes show up.
        common.Sort(StringComparer.Ordinal);
        agreed = common[^1];
        return true;
    }
}

/// <summary>
/// Local node's identity for handshake purposes. Encapsulates the
/// node-id / public-key pair, the Ed25519 private-key material for
/// signing HELLO, and the local policy for what to propose in CAPABILITY_NEG.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Signer"/> + <see cref="PrivateKey"/> together drive the real
/// crypto path. Either may be <c>null</c> in bootstrap / test-harness
/// scenarios; in that case <see cref="HandshakeProtocol.BuildHello"/>
/// emits the legacy 64-zero-byte stub and the verifier accepts only that
/// stub. Production callers always register a real <see cref="Ed25519Signer"/>
/// and keypair.
/// </para>
/// </remarks>
public sealed record LocalIdentity(
    byte[] NodeId,
    byte[] PublicKey,
    IEd25519Signer? Signer,
    byte[]? PrivateKey,
    string SchemaVersion,
    IReadOnlyList<string> SupportedVersions,
    IReadOnlyList<string>? ProposedStreams = null,
    IReadOnlyList<string>? CpLeases = null,
    IReadOnlyList<BucketRequest>? BucketSubscriptions = null,
    IReadOnlyList<string>? Capabilities = null);

/// <summary>
/// Outcome of a successful handshake. Consumed by the gossip scheduler to
/// know which streams are allowed to flow over this session.
/// </summary>
public sealed record CapabilityResult(
    byte[] PeerNodeId,
    byte[] PeerPublicKey,
    string AgreedSchemaVersion,
    IReadOnlyList<string> Granted,
    IReadOnlyList<Rejection> Rejected,
    uint TickIntervalSeconds,
    uint MaxDeltasPerSecond)
{
    /// <summary>The named protocol capabilities granted for this session.</summary>
    public IReadOnlyList<string> GrantedCapabilities { get; init; } = Array.Empty<string>();
}

/// <summary>Stable names for additive sync protocol capabilities.</summary>
public static class SyncCapabilities
{
    /// <summary>Peers exchange per-document CRDT state vectors and send state-relative deltas.</summary>
    public const string DeltaStateVector = "delta-state-vector";
}
