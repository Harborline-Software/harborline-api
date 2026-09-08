using System;
using System.Collections.Generic;
using System.Linq;
using Harborline.Api.Kernel.Sync.Protocol;

namespace Harborline.Api.Kernel.Sync.Handshake;

/// <summary>
/// MULTI-USER trust gate (enrollment Phase A) — the generalization of <see cref="SharedRootTrustPolicy"/> from
/// "same shared root" to "any enrolled roster member." A peer is trusted iff the public key it presents in
/// HELLO is in the team's VERIFIED member roster (the genesis-rooted signed membership log that validated to
/// genesis). This is what lets two DISTINCT-root users (the real two-user case the shared-root shortcut could
/// never deliver — cerebrum [2026-06-20]) trust each other: each is a member of the same roster, so each
/// other's pubkey is in the trusted set.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a snapshot provider, not a roster dependency.</b> The verified party→pubkey roster lives in
/// <c>foundation-identity-atlas</c> (<c>MemberRoster</c>), which is NOT — and must not become — a dependency of
/// <c>kernel-sync</c> (the sync kernel stays free of the identity package; same direction
/// <see cref="SharedRootTrustPolicy"/> keeps by taking raw pubkeys). So this policy takes a
/// <see cref="Func{TResult}"/> the COMPOSITION ROOT supplies — it returns the current verified trusted-pubkey
/// set (<c>MemberRoster.TrustedPublicKeys()</c>), re-read on each <see cref="IsTrusted"/> call so a roster
/// change (admit/revoke) takes effect immediately without re-wiring the daemon. The roster's signature-chain
/// validation happens in foundation-identity-atlas BEFORE the keys reach here; this gate only answers the
/// membership question.
/// </para>
/// <para>
/// <b>Fail-closed.</b> A peer whose presented key is not in the current verified set is rejected
/// (<see cref="ErrorCode.PeerUntrusted"/>) — exactly as the shared-root gate rejects a non-derived key. A
/// revoked member's key falls out of the set on the next roster snapshot, so a revoked peer is rejected
/// (subject to the eventual-convergence revocation window — a Phase B concern, flagged there).
/// </para>
/// </remarks>
public sealed class MemberSetTrustPolicy : IPeerTrustPolicy
{
    private readonly Func<IReadOnlyList<byte[]>> _trustedKeysSnapshot;

    /// <summary>
    /// Construct over a snapshot provider that returns the current verified member pubkey set (raw 32-byte
    /// Ed25519 keys). Read on every <see cref="IsTrusted"/> call so roster mutations apply live.
    /// </summary>
    public MemberSetTrustPolicy(Func<IReadOnlyList<byte[]>> trustedKeysSnapshot)
    {
        _trustedKeysSnapshot = trustedKeysSnapshot ?? throw new ArgumentNullException(nameof(trustedKeysSnapshot));
    }

    /// <summary>
    /// Convenience constructor over a fixed set of trusted member pubkeys (a roster snapshot taken once). Use
    /// the <see cref="Func{TResult}"/> overload for a live-updating roster.
    /// </summary>
    public MemberSetTrustPolicy(IEnumerable<byte[]> trustedMemberPublicKeys)
    {
        ArgumentNullException.ThrowIfNull(trustedMemberPublicKeys);
        var snapshot = trustedMemberPublicKeys
            .Where(k => k is { Length: > 0 })
            .Select(k => (byte[])k.Clone())
            .ToArray();
        _trustedKeysSnapshot = () => snapshot;
    }

    /// <inheritdoc />
    public bool IsTrusted(HelloMessage peerHello)
    {
        ArgumentNullException.ThrowIfNull(peerHello);
        if (peerHello.PublicKey is not { Length: > 0 } peerKey)
        {
            return false;
        }

        var trusted = _trustedKeysSnapshot();
        if (trusted is null) return false;

        foreach (var key in trusted)
        {
            if (key is { Length: > 0 } &&
                key.Length == peerKey.Length &&
                System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(key, peerKey))
            {
                return true;
            }
        }
        return false;
    }
}
