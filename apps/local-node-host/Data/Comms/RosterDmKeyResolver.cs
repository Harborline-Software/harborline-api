using System;
using System.Security.Cryptography;

using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost.Data.Comms;

/// <summary>
/// C5 — the PRODUCTION roster-bound <see cref="IParticipantDmKeyResolver"/>. This is the increment that delivers
/// DM confidentiality: it derives the active member's DM PRIVATE key from the node's OWN root secret (node-secret,
/// NOT party-id-derivable) scoped to the CURRENT ACTIVE team, and resolves a DM PEER's DM PUBLIC key from the
/// VERIFIED team roster (<see cref="NodeTeamRoster.DmPublicKeyOf"/>) — so the
/// <see cref="DerivedDmConversationKeyProvider"/> can run the X25519 ECDH and derive the per-conversation seal key.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this closes the C4 hole (sec-eng deep-review of PR #1325).</b> The retired C4 stand-in derived every
/// party's DM keypair from a compile-time constant keyed only by PARTY ID, so a non-participant could derive a
/// participant's DM private key by claiming that party's id — the protection was an identity self-check, not
/// crypto. Here:
/// <list type="bullet">
///   <item>the active member's DM PRIVATE key (<see cref="ActiveMemberDmPrivateKey"/>) is
///     <see cref="NodeDmKeyDerivation.DeriveDmPrivateKey">HKDF(node-root-secret, activeTeamId)</see> — a function
///     of the NODE's root secret, NOT of the party id, so it is recoverable ONLY on the owning node;</item>
///   <item>a PEER's DM PUBLIC key comes from the roster's by-association-validated DM-key map
///     (<see cref="NodeTeamRoster.DmPublicKeyOf"/>) — distributed on the enrollment wire / synced roster record;</item>
///   <item>so a non-participant — even a team member who knows both party ids + the dm: hash — holds neither
///     participant's DM PRIVATE key and CANNOT derive the per-conversation key (the real no-leak guarantee, DR-5).
///     A FORGED party id yields a DIFFERENT, useless key (the ECDH is against the wrong private key), unlike the
///     C4 stand-in where it yielded the identical key.</item>
/// </list>
/// </para>
/// <para>
/// <b>The private key FOLLOWS THE ACTIVE TEAM (bug-1332 fix, the bug-1314 family).</b> The resolver was originally
/// pinned at construction to the node's BOOT-GENESIS team id, so a JOINER (a node that enrolls into an admitter's
/// team and switches its active team to the admitter's) kept sealing DMs with its OWN-genesis-team DM private key
/// while publishing — and the admitter recording — its JOINED-team DM PUBLIC key. The two halves of the ECDH then
/// never agreed (<c>ECDH(B_genesis_priv, ·) ≠ ECDH(B_joined_priv, ·)</c>), so neither participant could decrypt the
/// other's DM (both got <c>[unable to decrypt]</c>). It was NOT a leak — too restrictive, not leaky — but it made
/// the exposed cross-team DM feature non-functional for joiners. This resolver now mirrors the
/// <c>LocalNodeWorker</c> DAEMON-REBIND (cerebrum [2026-06-21] gap #3): it subscribes to
/// <see cref="IActiveTeamAccessor.ActiveChanged"/> and re-derives the active member's DM private key for the NOW-ACTIVE
/// team id, so after a joiner adopts the admitter's team its DM private key is HKDF(root, joinedTeamId) — byte-for-byte
/// the private half of the joined-team DM PUBLIC key it published (<c>NodeWireEnrollmentClient.DeriveDmPublicKeyForTeam</c>
/// derives the public half from the SAME root + joined team id) → the ECDH agrees → both participants decrypt.
/// A node that NEVER switches teams (the single-user default) stays bound to its genesis team exactly as before — the
/// rebind path is dormant.
/// </para>
/// <para>
/// <b>The party id is STABLE across a join.</b> Only the team id in the HKDF salt/domain follows the active team; the
/// active member's party id (<see cref="ActiveMemberPartyId"/>) does NOT change on enrollment — the joiner re-uses its
/// own genesis/comms-author party id for the joined team (it publishes <c>SetOwnDmPublicKey(selfPartyId, joinedTeamDmKey)</c>),
/// so "me" in the ECDH routing self-guard is unchanged. Switching the team id can therefore never let a DIFFERENT party
/// derive a shared key: the team id is HKDF domain-separation (salt), not an identity claim; the private key still
/// requires THIS node's root secret, and the peer public key still comes only from the verified roster.
/// </para>
/// <para>
/// <b>The construction is UNCHANGED.</b> The X25519 ECDH + HKDF(salt=conversationId), sign-then-encrypt order,
/// AAD context binding, ChaCha20-Poly1305 fresh-nonce seal, and contributory-check all live in
/// <see cref="DerivedDmConversationKeyProvider"/> / <see cref="DmContentSeal"/> and do NOT change here — only the
/// team id the active member's private key is scoped to (boot-genesis → active). KCI / no-forward-secrecy notes from
/// the prior verdict still apply (static-static ECDH: a node-root-secret compromise lets the holder retro-read that
/// node's DMs).
/// </para>
/// <para>
/// <b>Fail-closed.</b> If the peer's DM public key is not yet in the roster (a legacy member whose record carried
/// no DM key, or a non-member) <see cref="TryResolveDmPublicKey"/> returns null → no key is derived → the body
/// stays sealed/opaque.
/// </para>
/// <para>
/// <b>Private-key lifetime contract.</b> <see cref="ActiveMemberDmPrivateKey"/> returns a span into the resolver's
/// CURRENT cached key array. A consumer must use the span synchronously and not retain it across a team-switch:
/// on <see cref="IActiveTeamAccessor.ActiveChanged"/> the resolver allocates a fresh key for the new team and
/// <see cref="CryptographicOperations.ZeroMemory">zeroes</see> the prior one. <see cref="DerivedDmConversationKeyProvider"/>
/// honours this — it reads the span and runs the ECDH within the same synchronous call (no await across the read). A
/// team-switch is an explicit operator action (a join), not concurrent with an in-flight DM derive on the same node.
/// </para>
/// </remarks>
public sealed class RosterDmKeyResolver : IParticipantDmKeyResolver, IDisposable
{
    private readonly NodeTeamRoster _roster;
    private readonly byte[] _rootSecret;
    private readonly string _bootTeamId;
    private readonly IActiveTeamAccessor? _activeTeam;
    private readonly EventHandler<ActiveTeamChangedEventArgs>? _activeChangedHandler;

    // Serializes the private-key swap (driven from the ActiveChanged event, off the resolve call stack) against a
    // concurrent read/refresh, and guards the (teamId → key) cache coherence. The whole refresh runs under it.
    private readonly object _gate = new();

    // The team id the cached private key is currently scoped to, and the key itself. Refreshed on a team-switch.
    private string _activeKeyTeamId;
    private byte[] _activeDmPrivateKey;

    private bool _disposed;

    /// <inheritdoc />
    public string ActiveMemberPartyId { get; }

    /// <inheritdoc />
    public ReadOnlySpan<byte> ActiveMemberDmPrivateKey
    {
        get
        {
            lock (_gate)
            {
                // Lazily re-key if the active team drifted from the cached key's team (covers a switch that
                // happened before this resolver subscribed, or a host that does not raise ActiveChanged).
                var current = CurrentActiveTeamIdOrBoot();
                if (!string.Equals(current, _activeKeyTeamId, StringComparison.Ordinal))
                {
                    RekeyTo(current);
                }
                return _activeDmPrivateKey;
            }
        }
    }

    /// <summary>
    /// Construct the production resolver: derive the active member's node-secret DM private key for the CURRENT
    /// active team (falling back to <paramref name="bootTeamId"/> until/unless a team is active) from
    /// <paramref name="rootSecret"/>, bind the team roster for peer DM-public-key lookup, and subscribe to
    /// <paramref name="activeTeam"/>'s <see cref="IActiveTeamAccessor.ActiveChanged"/> so the private key REFRESHES
    /// to the joined team on a wire-enrollment join (the bug-1332 fix).
    /// </summary>
    /// <param name="rootSecret">The install's 32-byte root secret (the same IKM the transport subkey uses). The DM
    /// private key derives from it and is never exported. Copied internally — the caller may zero its buffer after.</param>
    /// <param name="bootTeamId">The boot (genesis) team id (string form) — the floor the DM keypair is scoped to until
    /// the active team is materialized / switched.</param>
    /// <param name="activeMemberPartyId">The active (local) member's party id — the "me" in the ECDH. Stable across a
    /// join (only the team id follows the active team).</param>
    /// <param name="roster">The install-level team roster — the source of a DM peer's published DM public key.</param>
    /// <param name="activeTeam">The active-team accessor whose <see cref="IActiveTeamAccessor.ActiveChanged"/> drives
    /// the re-key. Null only in a minimal/test host that pins a single team for the resolver's lifetime.</param>
    public RosterDmKeyResolver(
        ReadOnlySpan<byte> rootSecret,
        string bootTeamId,
        string activeMemberPartyId,
        NodeTeamRoster roster,
        IActiveTeamAccessor? activeTeam = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bootTeamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeMemberPartyId);
        if (rootSecret.Length == 0)
        {
            throw new ArgumentException("Root secret must be non-empty.", nameof(rootSecret));
        }
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));
        ActiveMemberPartyId = activeMemberPartyId;
        _bootTeamId = bootTeamId;
        // Copy the secret so a caller that zeroes its buffer post-construction does not zero ours. The root secret is
        // node-secret (same IKM the transport subkey uses); the resolver re-derives the team-scoped DM private key
        // from it on every team-switch.
        _rootSecret = rootSecret.ToArray();
        _activeTeam = activeTeam;

        // Derive the initial key for whatever team is active NOW (the boot/genesis team in the common case; the
        // already-joined team if the resolver is built after a switch). NODE-SECRET: HKDF(root, teamId) over the DM
        // domain — a function of the root secret, not the party id, so it is recoverable ONLY on this node.
        _activeKeyTeamId = CurrentActiveTeamIdOrBoot();
        _activeDmPrivateKey = NodeDmKeyDerivation.DeriveDmPrivateKey(_rootSecret, _activeKeyTeamId);

        // Mirror the LocalNodeWorker daemon-rebind: a wire-enrollment JOIN switches the active team, and the DM
        // private key must follow it to the joined team (bug-1332). A node that never switches never enters this path.
        if (_activeTeam is not null)
        {
            _activeChangedHandler = OnActiveTeamChanged;
            _activeTeam.ActiveChanged += _activeChangedHandler;
        }
    }

    /// <inheritdoc />
    public byte[]? TryResolveDmPublicKey(string partyId)
    {
        if (string.IsNullOrWhiteSpace(partyId)) return null;
        // The peer's published DM public key from the by-association-validated roster DM-key map. Null (fail-closed)
        // for a non-member or a member whose record carried no DM key yet → no key derivable → body stays sealed.
        return _roster.DmPublicKeyOf(partyId);
    }

    /// <summary>
    /// DM-KEY REBIND on team-switch (bug-1332 — mirrors the LocalNodeWorker gossip daemon-rebind). Fired when
    /// <see cref="IActiveTeamAccessor.Active"/> changes — i.e. a wire-enrollment JOIN switched this node onto the
    /// admitter's team. Re-derives the active member's DM private key for the now-active team so the joiner seals with
    /// HKDF(root, joinedTeamId), matching the joined-team DM PUBLIC key it published → the ECDH agrees → both
    /// participants decrypt. No-op if the team did not actually change.
    /// </summary>
    private void OnActiveTeamChanged(object? sender, ActiveTeamChangedEventArgs e)
    {
        var target = e.Current?.TeamId.ToString() ?? _bootTeamId;
        lock (_gate)
        {
            if (_disposed) return;
            if (string.Equals(target, _activeKeyTeamId, StringComparison.Ordinal)) return;
            RekeyTo(target);
        }
    }

    /// <summary>
    /// Re-derive the cached private key for <paramref name="teamId"/> and zero the prior one. Caller holds <see cref="_gate"/>.
    /// </summary>
    private void RekeyTo(string teamId)
    {
        var next = NodeDmKeyDerivation.DeriveDmPrivateKey(_rootSecret, teamId);
        var prior = _activeDmPrivateKey;
        _activeDmPrivateKey = next;
        _activeKeyTeamId = teamId;
        if (prior is not null) CryptographicOperations.ZeroMemory(prior);
    }

    /// <summary>
    /// The current active team id (string form), or the boot/genesis team id when no team is active yet (the
    /// single-user floor). The string form is the canonical GUID "D" form — byte-identical to the
    /// <c>capturedGenesisTeamId</c> the boot wiring passes and to the <c>inviteAnchor.TeamId</c> the joiner scopes
    /// its published DM public key to (<see cref="TeamId.ToString"/> == <c>Guid.ToString("D")</c>), so the private
    /// and public halves of the ECDH derive over the IDENTICAL team id string.
    /// </summary>
    private string CurrentActiveTeamIdOrBoot()
    {
        var active = _activeTeam?.Active;
        return active is null ? _bootTeamId : active.TeamId.ToString();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_activeTeam is not null && _activeChangedHandler is not null)
            {
                _activeTeam.ActiveChanged -= _activeChangedHandler;
            }
            CryptographicOperations.ZeroMemory(_activeDmPrivateKey);
            CryptographicOperations.ZeroMemory(_rootSecret);
        }
    }
}
