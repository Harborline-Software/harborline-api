using Harborline.Api.Kernel.Sync.Protocol;

namespace Harborline.Api.Kernel.Sync.Handshake;

/// <summary>
/// The trust gate the handshake consults <b>after</b> verifying a peer's HELLO
/// signature — sync-daemon-protocol §4, multi-device INC-3. Signature
/// verification proves a peer <em>owns the key it presented</em>; this policy
/// answers the orthogonal question "is that key one I trust?".
/// </summary>
/// <remarks>
/// <para>
/// Without a trust gate, an mDNS-discovered stranger on the same LAN segment
/// that presents a self-consistent (validly self-signed) HELLO is accepted —
/// unacceptable even for a demo. The gate is consulted symmetrically on both
/// the initiator (<see cref="HandshakeProtocol.InitiateAsync"/>) and the
/// responder (<see cref="HandshakeProtocol.RespondAsync"/>) sides so neither
/// end commits session state to an untrusted peer.
/// </para>
/// <para>
/// <b>Fail-closed.</b> A peer that is not affirmatively trusted is rejected
/// with <see cref="ErrorCode.PeerUntrusted"/> and the session is closed.
/// </para>
/// </remarks>
public interface IPeerTrustPolicy
{
    /// <summary>
    /// Decide whether the peer that sent <paramref name="peerHello"/> is
    /// trusted. Called only after the HELLO signature and replay window have
    /// already passed, so <see cref="HelloMessage.PublicKey"/> is known to be
    /// the key the peer actually controls.
    /// </summary>
    /// <returns><c>true</c> to grant the session; <c>false</c> to reject it.</returns>
    bool IsTrusted(HelloMessage peerHello);
}

/// <summary>
/// Trust policy that accepts every cryptographically-valid peer. This is the
/// pre-INC-3 behavior — kept as the explicit default for deployments that have
/// their own out-of-band trust boundary (single-machine local sockets, a
/// team VPN segment) and do not need the shared-root gate.
/// </summary>
public sealed class AllowAllPeerTrustPolicy : IPeerTrustPolicy
{
    /// <summary>Shared stateless instance.</summary>
    public static readonly AllowAllPeerTrustPolicy Instance = new();

    public bool IsTrusted(HelloMessage peerHello) => true;
}

/// <summary>
/// Shared-root TOFU trust policy for single-user-multi-device — multi-device
/// INC-3. A peer is trusted iff the public key it presents in HELLO is one of
/// the team-scoped public keys derived from the same shared root identity.
/// </summary>
/// <remarks>
/// <para>
/// In the single-user-multi-device topology every device shares one root
/// Ed25519 seed and runs the same deterministic per-team subkey derivation
/// (<c>ITeamSubkeyDerivation.DeriveTeamKeypair(rootSeed, teamId)</c>,
/// HKDF-SHA256, ADR 0032). The derivation is deterministic, so two devices on
/// the same (root, team) pair derive the <b>identical</b> team keypair — and
/// therefore the identical team public key. A device thus knows, locally and
/// with no out-of-band exchange, exactly which public key a legitimate sibling
/// device must present: its own team public key.
/// </para>
/// <para>
/// A peer whose root seed differs (a different user / a stranger on the LAN)
/// derives a different team subkey, so its HELLO public key will not be in the
/// trusted set and the handshake rejects it (<see cref="ErrorCode.PeerUntrusted"/>).
/// The general pairing UX (QR / 6-digit code, 0061 Mesh enrollment) for
/// cross-user / cross-team trust is a tracked follow-on, not v1.
/// </para>
/// <para>
/// The trusted set is supplied as raw 32-byte Ed25519 public keys. The typical
/// caller passes exactly one entry — the locally-derived team public key — but
/// the set form leaves room for key rotation (old + new) without a contract
/// change.
/// </para>
/// </remarks>
public sealed class SharedRootTrustPolicy : IPeerTrustPolicy
{
    private readonly IReadOnlyList<byte[]> _trustedPublicKeys;

    /// <summary>
    /// Construct a policy over a single trusted team public key — the common
    /// single-user-multi-device case (this device's own team public key, which
    /// a legitimate sibling derives identically from the shared root).
    /// </summary>
    public SharedRootTrustPolicy(byte[] trustedTeamPublicKey)
        : this(new[] { trustedTeamPublicKey ?? throw new ArgumentNullException(nameof(trustedTeamPublicKey)) })
    {
    }

    /// <summary>
    /// Construct a policy over a set of trusted team public keys (e.g. to span
    /// a key rotation). Each entry is a raw Ed25519 public key.
    /// </summary>
    public SharedRootTrustPolicy(IEnumerable<byte[]> trustedTeamPublicKeys)
    {
        ArgumentNullException.ThrowIfNull(trustedTeamPublicKeys);
        var snapshot = trustedTeamPublicKeys
            .Where(k => k is { Length: > 0 })
            .Select(k => (byte[])k.Clone())
            .ToList();
        if (snapshot.Count == 0)
        {
            throw new ArgumentException(
                "At least one trusted team public key is required for shared-root TOFU.",
                nameof(trustedTeamPublicKeys));
        }
        _trustedPublicKeys = snapshot;
    }

    public bool IsTrusted(HelloMessage peerHello)
    {
        ArgumentNullException.ThrowIfNull(peerHello);
        if (peerHello.PublicKey is not { Length: > 0 } peerKey)
        {
            return false;
        }

        foreach (var trusted in _trustedPublicKeys)
        {
            // Fixed-length, public-value comparison — constant-time is not
            // required (public keys, not secrets) but FixedTimeEquals is a
            // clean length-checked equality and avoids early-out surprises.
            if (trusted.Length == peerKey.Length &&
                System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(trusted, peerKey))
            {
                return true;
            }
        }
        return false;
    }
}
