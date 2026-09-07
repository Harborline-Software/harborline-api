using System;
using System.Collections.Generic;
using System.Linq;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Kernel.Sync.Handshake;

namespace Harborline.Api.LocalNodeHost.Enrollment;

/// <summary>
/// The install-level holder of the active team's <see cref="MemberRoster"/> — the PRODUCTION wiring that closes
/// #1277-B1 end-to-end (enrollment Phase B; cerebrum [2026-06-20] #1288 M1 carry-forward "the forge-proof
/// mechanism was built in Phase A but left UNWIRED in production"). It is seeded at bootstrap with the GENESIS
/// self-admission (the single-office operator admits their own (party, principal-key) pair), so:
/// <list type="bullet">
///   <item>the comms merge gate's <c>rosterBinding</c> (<see cref="ForgeProofBinding"/> = the roster's
///     <see cref="MemberRoster.PublicKeyOf"/>) is now LIVE in production — a forged-foreign-party message is
///     dropped on the real merge path, not just in a unit test;</item>
///   <item>a fresh SINGLE-USER node is NOT bricked — the genesis member (self) is in its own roster, so the
///     operator's own messages forge-prove and the node works out of the box.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Two key types, two gates (the correctness pivot — see <see cref="ITrustedMemberKeyProvider"/>).</b> A
/// member has a PRINCIPAL/author key (what comms messages are signed with — <c>NodePrincipalSigner</c>) and a
/// team-scoped TRANSPORT subkey (what the sync HELLO presents — HKDF(root, teamId)). The roster's party→pubkey
/// binding is on the PRINCIPAL key (forge-proof comms attribution). The trust gate needs TRANSPORT keys, exposed
/// separately via <see cref="TrustedTransportKeys"/>. This holder owns both: the principal-key
/// <see cref="MemberRoster"/> and the admitted-member transport-key set.
/// </para>
/// <para>
/// <b>v1 in-memory + thread-safe.</b> The roster is replaced atomically under a lock on each admit/revoke (the
/// Phase A <see cref="MemberRoster"/> is immutable-by-replacement); the <c>rosterBinding</c> + trust-key Func
/// snapshots read the current roster on each call so an admit/revoke applies live. The durable, synced roster
/// doctype replaces the in-memory state behind the same holder shape (survey #1275 §4). The Harborline App admission UI
/// (QR-scan / invite-entry) that DRIVES admit/revoke through here is a flagged FED follow-on.
/// </para>
/// </remarks>
public sealed class NodeTeamRoster : ITrustedMemberKeyProvider
{
    private readonly object _gate = new();
    private MemberRoster _roster;

    // Admitted-member TRANSPORT subkeys (raw 32-byte Ed25519), keyed by party id. SEPARATE from the principal-key
    // roster: the trust gate consumes these. The genesis/self transport subkey is contributed by the registrar's
    // own-subkey floor (DefaultTeamServiceRegistrar), so this set holds ADMITTED peers' transport keys only — it
    // is empty on a fresh single-user node, which is correct (no peers yet; the registrar floor handles self).
    private readonly Dictionary<string, byte[]> _transportKeysByParty = new(StringComparer.Ordinal);

    // C5 — admitted-member DM-encryption PUBLIC keys (raw 32-byte X25519), keyed by party id. SEPARATE from both
    // the principal-key roster (forge-proof attribution) and the transport-key set (the sync trust gate): the C5
    // roster-bound DM key resolver consumes these to run the X25519 ECDH against a DM peer's public key. Like the
    // transport set, this holds ADMITTED peers' DM public keys (+ the genesis/self, contributed at adopt time) —
    // every party for whom a DM seal key can be derived. A member with no DM key here (a legacy record) cannot have
    // its DMs sealed/unsealed until its record re-emits the key. Public-key-only (leaks nothing).
    private readonly Dictionary<string, byte[]> _dmKeysByParty = new(StringComparer.Ordinal);

    // 2c-iii-b — admitted-member X-Wing (X25519 + ML-KEM-768) PUBLIC keys (raw 1216-byte pk_M ‖ pk_X), keyed by
    // party id. The suite-#3 write-side enabler: a sender reads a recipient's X-Wing pubkey from here to box a
    // suite-#3 tenant-DEK / role-key wrap for it (PR-B's capability gate consumes XWingPublicKeyOf). UNSIGNED-by-
    // association, populated ONLY for parties the genesis-rooted chain validated as live members (filtered at adopt
    // time). A member with no X-Wing key here (a legacy record, or a recipient that has not derived/published one)
    // is NOT X-Wing-capable → a sender boxes it suite #1 (safe degrade). Public-key-only (leaks nothing); the
    // private decapsulation seed is root-seed-derived on the owning node only and never appears here.
    private readonly Dictionary<string, byte[]> _xwingKeysByParty = new(StringComparer.Ordinal);

    /// <summary>
    /// Construct over the genesis roster (the seeded self-admission). Built at bootstrap from the node's
    /// canonical principal signer + the single-office operator party id.
    /// </summary>
    public NodeTeamRoster(MemberRoster genesisRoster)
    {
        _roster = genesisRoster ?? throw new ArgumentNullException(nameof(genesisRoster));
    }

    /// <summary>The current roster (live snapshot). Replaced atomically on admit/revoke.</summary>
    public MemberRoster Current
    {
        get { lock (_gate) { return _roster; } }
    }

    /// <summary>Resolve the current roster only when it is rooted in the exact token anchor supplied.</summary>
    public MemberRoster? Resolve(TeamTrustAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        lock (_gate)
        {
            return TeamTrustAnchor.FromRoster(_roster) == anchor ? _roster : null;
        }
    }

    /// <summary>
    /// Public discovery scope of the current immutable roster root. It changes only when enrollment adopts a
    /// different roster; ordinary admit and revoke operations retain the same genesis root and therefore the
    /// same scope across every machine in the roster.
    /// </summary>
    public string DiscoveryRosterId
    {
        get { lock (_gate) { return TeamTrustAnchor.FromRoster(_roster).RosterId; } }
    }

    /// <summary>
    /// The forge-proof attribution binding the comms projection consumes as its <c>rosterBinding</c> — resolves a
    /// claimed author party id to the roster's bound PRINCIPAL key, or null when the party is not an enrolled
    /// member (fail-closed). Reads the CURRENT roster on each call so an admit/revoke applies live.
    /// </summary>
    public PrincipalId? ForgeProofBinding(string partyId)
    {
        lock (_gate) { return _roster.PublicKeyOf(partyId); }
    }

    /// <inheritdoc />
    /// <remarks>The trust gate's transport-key snapshot — admitted peers' team-scoped subkeys. The genesis/self
    /// subkey is added by the registrar floor, so this returns admitted PEERS only.</remarks>
    public IReadOnlyList<byte[]> TrustedTransportKeys()
    {
        lock (_gate)
        {
            return _transportKeysByParty.Values.Select(k => (byte[])k.Clone()).ToArray();
        }
    }

    /// <summary>
    /// The admitted-PEER transport keys keyed by party id (raw 32-byte Ed25519 each). The two-sided wire-enrollment
    /// RESPONSE builder reads this to populate the per-member transport map it returns to a joiner, so the joiner
    /// trusts EVERY enrolled peer's wire HELLO (not only the admitter's). Excludes the genesis/self subkey (the
    /// registrar floor contributes that separately); the response builder unions A's own team-scoped key in.
    /// </summary>
    public IReadOnlyDictionary<string, byte[]> AdmittedPeerTransportKeys()
    {
        lock (_gate)
        {
            return _transportKeysByParty.ToDictionary(
                kv => kv.Key, kv => (byte[])kv.Value.Clone(), StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// C5 — the team-scoped DM-encryption PUBLIC key bound to <paramref name="partyId"/> (raw 32-byte X25519), or
    /// null if this node holds none for that party (a non-member, or a member whose record carried no DM key yet).
    /// The C5 roster-bound DM key resolver consumes this to run the ECDH against a DM peer. Reads the CURRENT set on
    /// each call so an admit/adopt applies live. Fail-closed: an absent key means no DM seal key can be derived
    /// (the body stays opaque) — the resolver treats null as "cannot derive". Returns a defensive copy.
    /// </summary>
    public byte[]? DmPublicKeyOf(string partyId)
    {
        if (string.IsNullOrWhiteSpace(partyId)) return null;
        lock (_gate)
        {
            return _dmKeysByParty.TryGetValue(partyId, out var k) && k is { Length: > 0 }
                ? (byte[])k.Clone()
                : null;
        }
    }

    /// <summary>
    /// 2c-iii-b — the team-scoped X-Wing PUBLIC key bound to <paramref name="partyId"/> (raw 1216-byte
    /// <c>pk_M ‖ pk_X</c>), or null if this node holds none for that party (a non-member, or a member whose record
    /// carried no X-Wing key — i.e. not X-Wing-capable). PR-B's suite-#3 capability gate consumes this: a non-null
    /// result means the recipient is X-Wing-CAPABLE and a sender MAY box it suite #3; null means box suite #1 (safe
    /// degrade). Reads the CURRENT set on each call so an admit/adopt applies live. Returns a defensive copy.
    /// </summary>
    public byte[]? XWingPublicKeyOf(string partyId)
    {
        if (string.IsNullOrWhiteSpace(partyId)) return null;
        lock (_gate)
        {
            return _xwingKeysByParty.TryGetValue(partyId, out var k) && k is { Length: > 0 }
                ? (byte[])k.Clone()
                : null;
        }
    }

    /// <summary>
    /// C5 — the admitted-member DM-encryption PUBLIC keys keyed by party id (raw 32-byte X25519 each). The
    /// two-sided wire-enrollment RESPONSE builder reads this to populate the per-member DM-key map it returns to a
    /// joiner, so the joiner learns every enrolled peer's DM public key (the DM counterpart of
    /// <see cref="AdmittedPeerTransportKeys"/>). Returns defensive copies.
    /// </summary>
    public IReadOnlyDictionary<string, byte[]> AdmittedPeerDmKeys()
    {
        lock (_gate)
        {
            return _dmKeysByParty.ToDictionary(
                kv => kv.Key, kv => (byte[])kv.Value.Clone(), StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// C5 (participant-scoped routing) — resolve the PARTY ID a connected peer represents from the raw TRANSPORT
    /// public key its trust-gated HELLO presented, by matching it against the admitted-peer transport-key set
    /// (constant-time-equality matched). Returns null when no admitted peer holds that transport key (an unknown /
    /// unidentified peer) — the routing filter then treats it as NOT a participant (fail-closed: a DM stream is
    /// never shipped to a peer whose party cannot be identified). Reads the CURRENT set so an admit applies live.
    /// </summary>
    public string? PartyIdForTransportKey(ReadOnlySpan<byte> transportPublicKey)
    {
        if (transportPublicKey.Length == 0) return null;
        lock (_gate)
        {
            foreach (var (party, key) in _transportKeysByParty)
            {
                if (key.Length == transportPublicKey.Length
                    && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(key, transportPublicKey))
                {
                    return party;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Admit a single PEER, ADDITIVELY: replace the principal-key roster with <paramref name="newRoster"/> (the
    /// post-<see cref="MemberRoster.Admit"/> roster the admission surface produced) AND record the admitted
    /// peer's team-scoped TRANSPORT subkey so <c>MemberSetTrustPolicy</c> trusts that peer for sync. This additive
    /// path leaves every already-admitted peer's transport key in place — the admission surface (gap #3) admits one
    /// party at a time and must not drop the others. The transport key is supplied by the JOINING node because a
    /// team-scoped subkey is
    /// HKDF(joiner-root-private-key, teamId) — material the admitting node never holds — so it travels with the
    /// (party, principal-key) over the admission channel.
    /// </summary>
    /// <param name="newRoster">The post-admit principal-key roster (forge-proof attribution binding).</param>
    /// <param name="admittedPartyId">The admitted peer's party id.</param>
    /// <param name="admittedTransportKey">The admitted peer's raw 32-byte team-scoped transport subkey
    /// (the pubkey it presents in the sync HELLO).</param>
    /// <param name="admittedDmKey">C5 — the admitted peer's raw 32-byte team-scoped DM-encryption PUBLIC key (the
    /// X25519 public half it published on the enrollment wire). Additive + optional: when supplied it is recorded
    /// alongside the transport key so this node can derive a per-conversation DM seal key with that peer. Null/empty
    /// (a legacy joiner that did not present a DM key) leaves the DM map unchanged for that party (its DMs cannot be
    /// sealed until its record carries a DM key).</param>
    public void AdmitPeer(
        MemberRoster newRoster, string admittedPartyId, byte[] admittedTransportKey, byte[]? admittedDmKey = null)
    {
        ArgumentNullException.ThrowIfNull(newRoster);
        ArgumentException.ThrowIfNullOrWhiteSpace(admittedPartyId);
        ArgumentNullException.ThrowIfNull(admittedTransportKey);
        if (admittedTransportKey.Length == 0)
        {
            throw new ArgumentException("The admitted peer's transport key must be non-empty.", nameof(admittedTransportKey));
        }
        lock (_gate)
        {
            _roster = newRoster;
            _transportKeysByParty[admittedPartyId] = (byte[])admittedTransportKey.Clone();
            if (admittedDmKey is { Length: > 0 })
            {
                _dmKeysByParty[admittedPartyId] = (byte[])admittedDmKey.Clone();
            }
        }
    }

    /// <summary>Install a peer only while the live roster still matches the decision's anchor.</summary>
    public bool TryAdmitPeer(
        TeamTrustAnchor expectedAnchor,
        MemberRoster newRoster,
        string admittedPartyId,
        byte[] admittedTransportKey,
        byte[]? admittedDmKey = null)
    {
        ArgumentNullException.ThrowIfNull(expectedAnchor);
        ArgumentNullException.ThrowIfNull(newRoster);
        ArgumentException.ThrowIfNullOrWhiteSpace(admittedPartyId);
        ArgumentNullException.ThrowIfNull(admittedTransportKey);
        if (admittedTransportKey.Length == 0)
        {
            throw new ArgumentException("The admitted peer's transport key must be non-empty.", nameof(admittedTransportKey));
        }
        lock (_gate)
        {
            if (TeamTrustAnchor.FromRoster(_roster) != expectedAnchor)
            {
                return false;
            }
            _roster = newRoster;
            _transportKeysByParty[admittedPartyId] = (byte[])admittedTransportKey.Clone();
            if (admittedDmKey is { Length: > 0 })
            {
                _dmKeysByParty[admittedPartyId] = (byte[])admittedDmKey.Clone();
            }
            return true;
        }
    }

    /// <summary>
    /// ADOPT A's TEAM after a two-sided WIRE ENROLLMENT (cerebrum [2026-06-21] — the joiner half). This is the
    /// B-SIDE counterpart of the admitter's <see cref="AdmitPeer"/>: B has just validated A's
    /// <c>EnrollmentResponse</c> against the out-of-band invite anchor and produced an
    /// <c>EnrollmentAdoptionPlan</c>; B now (a) REPLACES its own genesis roster with A's validated roster (single-
    /// team reference edition — B's own genesis team is superseded) so B's comms <c>rosterBinding</c> +
    /// attribution reflect A's team, and (b) SETS the transport-key map to the full enrolled-member set from the
    /// plan (every live member's team-scoped transport pubkey — A's included) so <c>MemberSetTrustPolicy</c>
    /// trusts every team member's wire HELLO.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a distinct method (not <see cref="AdmitPeer"/> in a loop or <see cref="AdoptSyncedRoster"/>).</b>
    /// <see cref="AdmitPeer"/> admits ONE peer additively (the admitter side); <see cref="AdoptSyncedRoster"/>
    /// PRUNES the transport map to the synced live members but never ADDS keys (it adopts a roster whose transport
    /// keys were already established by prior admits). The joiner's adoption is the bootstrap case: B has NO prior
    /// transport keys for this team and must SET the whole set at once from the plan. This method replaces the
    /// roster AND replaces the transport map with the plan's full set — the atomic "B becomes a trusting member of
    /// A's team" step.
    /// </para>
    /// <para>
    /// B's OWN team-scoped subkey for A's team (HKDF(B-root, A.teamId)) is the never-brick FLOOR the per-team
    /// registrar already unions in (<c>DefaultTeamServiceRegistrar</c>), so B's own wire HELLO key is trusted by
    /// construction once B's active team is A's team — this map carries the PEERS (incl. A). Including B's own key
    /// here too is harmless (it is also in the floor) and keeps the map a faithful image of the enrolled set.
    /// </para>
    /// </remarks>
    /// <param name="adoptedRoster">A's roster, independently re-validated to genesis by the joiner.</param>
    /// <param name="transportKeysByParty">The full enrolled-member transport-key set from the validated plan
    /// (party → raw 32-byte Ed25519). Replaces (not merges) B's transport map — the bootstrap set.</param>
    /// <param name="dmKeysByParty">C5 — the full enrolled-member DM-encryption PUBLIC-key set from the validated
    /// plan (party → raw 32-byte X25519). Replaces (not merges) B's DM map — the bootstrap set, so B can derive a
    /// per-conversation DM seal key with every enrolled peer. Optional + additive (null = a legacy response carrying
    /// no DM keys → B holds no peer DM keys until the synced roster records re-emit them).</param>
    public void AdoptEnrollment(
        MemberRoster adoptedRoster,
        IReadOnlyDictionary<string, byte[]> transportKeysByParty,
        IReadOnlyDictionary<string, byte[]>? dmKeysByParty = null)
    {
        ArgumentNullException.ThrowIfNull(adoptedRoster);
        ArgumentNullException.ThrowIfNull(transportKeysByParty);
        lock (_gate)
        {
            _roster = adoptedRoster;
            _transportKeysByParty.Clear();
            foreach (var (party, key) in transportKeysByParty)
            {
                if (!string.IsNullOrWhiteSpace(party) && key is { Length: > 0 })
                {
                    _transportKeysByParty[party] = (byte[])key.Clone();
                }
            }
            _dmKeysByParty.Clear();
            if (dmKeysByParty is not null)
            {
                foreach (var (party, key) in dmKeysByParty)
                {
                    if (!string.IsNullOrWhiteSpace(party) && key is { Length: > 0 })
                    {
                        _dmKeysByParty[party] = (byte[])key.Clone();
                    }
                }
            }
        }
    }

    /// <summary>
    /// Adopt a roster reconstructed from the SYNCED roster doctype (the roster-sync production wiring — gap #1).
    /// Replaces the PRINCIPAL-key roster atomically so the comms <c>rosterBinding</c> (<see cref="ForgeProofBinding"/>)
    /// reflects the synced membership immediately; a member admitted on a peer now forge-proves here, and a revoked
    /// member's attribution binding drops. <see cref="RosterCrdtProjection"/> calls this after re-validating the
    /// synced records to genesis (the trust anchor) — it NEVER passes an empty/untrustworthy rebuild (that path
    /// keeps the local roster, non-bricking).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The transport-trust set is REBUILT from the converged roster — roster-derived trust (INFO-2; the ≥3-node
    /// mesh fix).</b> Before this, <see cref="AdoptSyncedRoster"/> could only PRUNE (drop a revoked party's key) —
    /// it never ADDED a transport key, because the synced record carried only the PRINCIPAL key. So a peer admitted
    /// AFTER this node enrolled was never learned here: its transport key wasn't in the one-time
    /// <see cref="AdoptEnrollment"/> set and the roster-CRDT propagated only its principal key — so two non-hub
    /// members (B↔C) admitted at different times could never trust each other for sync (only the admitting hub
    /// worked). Now the synced admission record CARRIES the team-scoped transport pubkey
    /// (<c>RosterRecordCrdtState.TransportPublicKeyB64Url</c>); the projection harvests
    /// <paramref name="transportKeysByConvergedMember"/> = (party → carried transport key) for every party the
    /// genesis-rooted rebuild VALIDATED into the live set, and this method REBUILDS the transport map from it
    /// (unioned with any still-valid locally-known keys for the rollout window, where old records carry no key but
    /// the live one-time enrollment set / a prior <see cref="AdmitPeer"/> already holds the hub pair). Every
    /// converged member computes the SAME full transport-trust set from the SAME converged roster — no admit-order
    /// dependency, no per-pair wiring.
    /// </para>
    /// <para>
    /// <b>Revocation still drops transport-trust (gap-#3, SUBSUMED).</b> Because the rebuild is keyed on the
    /// CONVERGED LIVE-MEMBER set, a revoked party — no longer a live member — simply is not in
    /// <paramref name="transportKeysByConvergedMember"/>, so its transport key is not rebuilt. The
    /// rebuild-from-live-set is a STRONGER form of the prior prune (it reconstructs exactly the live set rather
    /// than subtracting the dead one), so the revocation-drops-transport-trust property is preserved by
    /// construction. A locally-known key for a party that is NO LONGER a live member is also dropped (the union is
    /// intersected with the live set), so a stale carried-over key can never re-trust a revoked member.
    /// </para>
    /// <para>
    /// <b>Trust-anchor unchanged (UNSIGNED-by-association is safe).</b> The carried transport key is honored ONLY
    /// for a party the SIGNED genesis-rooted chain already validated into <paramref name="syncedRoster"/> — the
    /// projection passes keys only for validated live members. A forged transport key for a non-member never
    /// reaches this map (the member isn't in the rebuilt roster; the <c>FromSyncedRecords</c> injection guard drops
    /// the fake member long before its transport key is looked at). The own-subkey floor is NOT in this map (the
    /// registrar contributes it separately), so single-user trust never regresses — a fresh node has an empty map
    /// and this is a no-op.
    /// </para>
    /// </remarks>
    /// <param name="syncedRoster">The principal-key roster reconstructed + validated to genesis from the synced
    /// records (the trust anchor — never empty/untrustworthy here, the projection guards that).</param>
    /// <param name="transportKeysByConvergedMember">The (party → raw 32-byte team-scoped transport pubkey) the
    /// converged records carried, for the parties the rebuild VALIDATED as live members. Drives the rebuilt
    /// transport map. A party absent here keeps any still-valid locally-known key ONLY if it is still a live member
    /// (rollout back-compat); a party that is no longer a live member is dropped (revocation).</param>
    /// <param name="dmKeysByConvergedMember">C5 — the (party → raw 32-byte X25519 DM PUBLIC key) the converged
    /// records carried, for the parties the rebuild VALIDATED as live members. Drives the rebuilt DM-key map the
    /// SAME way <paramref name="transportKeysByConvergedMember"/> drives the transport map: roster-derived, filtered
    /// to live members, revocation-drops-by-rebuild. Optional + additive (null = no DM keys carried → the DM map is
    /// rebuilt from still-valid locally-known keys for live members only).</param>
    /// <param name="xwingKeysByConvergedMember">2c-iii-b — the (party → raw 1216-byte team-scoped X-Wing PUBLIC
    /// key) the converged records carried, for the parties the rebuild VALIDATED as live members. Drives the
    /// rebuilt X-Wing-key map the SAME roster-derived, live-member-filtered, revocation-drops-by-rebuild way as the
    /// DM map — the suite-#3 capability source PR-B consumes. Optional + additive (null = no X-Wing keys carried →
    /// the map is rebuilt from still-valid locally-known keys for live members only).</param>
    public void AdoptSyncedRoster(
        MemberRoster syncedRoster,
        IReadOnlyDictionary<string, byte[]>? transportKeysByConvergedMember = null,
        IReadOnlyDictionary<string, byte[]>? dmKeysByConvergedMember = null,
        IReadOnlyDictionary<string, byte[]>? xwingKeysByConvergedMember = null)
    {
        ArgumentNullException.ThrowIfNull(syncedRoster);
        lock (_gate)
        {
            _roster = syncedRoster;

            // The set of CURRENT live members — the only parties whose transport key may be trusted (a revoked
            // member is absent, so its key is never rebuilt — gap-#3 revocation-drop subsumed by rebuild-from-live).
            var liveParties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in syncedRoster.Members) liveParties.Add(m.PartyId);

            // REBUILD (INFO-2). Start from the carried transport keys of converged live members (roster-derived),
            // then union any still-valid locally-known key for a live member that the converged records did NOT
            // carry a key for (the rollout window: old records carry no transport key, but the live one-time
            // enrollment set / a prior AdmitPeer already wired the hub pair — don't drop it before records re-emit).
            var rebuilt = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (transportKeysByConvergedMember is not null)
            {
                foreach (var (party, key) in transportKeysByConvergedMember)
                {
                    if (!string.IsNullOrWhiteSpace(party) && key is { Length: > 0 } && liveParties.Contains(party))
                    {
                        rebuilt[party] = (byte[])key.Clone();
                    }
                }
            }
            // Carry forward an existing locally-known key ONLY for a still-live member the rebuild didn't supply
            // a carried key for. A party no longer in the live set is dropped (revocation); a party the converged
            // records DID carry is authoritative (don't let a stale local key shadow the roster-derived one).
            foreach (var (party, key) in _transportKeysByParty)
            {
                if (liveParties.Contains(party) && !rebuilt.ContainsKey(party))
                {
                    rebuilt[party] = key; // already a private clone (set via AdmitPeer/AdoptEnrollment)
                }
            }

            _transportKeysByParty.Clear();
            foreach (var (party, key) in rebuilt) _transportKeysByParty[party] = key;

            // C5 — REBUILD the DM-key map the SAME roster-derived way (filtered to live members; revocation drops by
            // rebuild-from-live; rollout-window carry-forward of a still-valid locally-known DM key for a live member
            // the converged records did not carry). A non-member's DM key is never rebuilt (it is not in liveParties),
            // so the resolver can never derive a key against a non-member.
            var rebuiltDm = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (dmKeysByConvergedMember is not null)
            {
                foreach (var (party, key) in dmKeysByConvergedMember)
                {
                    if (!string.IsNullOrWhiteSpace(party) && key is { Length: > 0 } && liveParties.Contains(party))
                    {
                        rebuiltDm[party] = (byte[])key.Clone();
                    }
                }
            }
            foreach (var (party, key) in _dmKeysByParty)
            {
                if (liveParties.Contains(party) && !rebuiltDm.ContainsKey(party))
                {
                    rebuiltDm[party] = key; // already a private clone (set via AdmitPeer/AdoptEnrollment).
                }
            }
            _dmKeysByParty.Clear();
            foreach (var (party, key) in rebuiltDm) _dmKeysByParty[party] = key;

            // 2c-iii-b — REBUILD the X-Wing-key map the SAME roster-derived way (filtered to live members;
            // revocation drops by rebuild-from-live; rollout-window carry-forward of a still-valid locally-known
            // X-Wing key for a live member the converged records did not carry). A non-member's X-Wing key is never
            // rebuilt (not in liveParties), so PR-B's capability gate can never box suite #3 to a non-member.
            var rebuiltXWing = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (xwingKeysByConvergedMember is not null)
            {
                foreach (var (party, key) in xwingKeysByConvergedMember)
                {
                    if (!string.IsNullOrWhiteSpace(party) && key is { Length: > 0 } && liveParties.Contains(party))
                    {
                        rebuiltXWing[party] = (byte[])key.Clone();
                    }
                }
            }
            foreach (var (party, key) in _xwingKeysByParty)
            {
                if (liveParties.Contains(party) && !rebuiltXWing.ContainsKey(party))
                {
                    rebuiltXWing[party] = key; // already a private clone.
                }
            }
            _xwingKeysByParty.Clear();
            foreach (var (party, key) in rebuiltXWing) _xwingKeysByParty[party] = key;
        }
    }

    /// <summary>
    /// C5 — record the install's OWN (genesis/self) team-scoped DM PUBLIC key, so this node's resolver knows its own
    /// DM public key (the genesis party's DM key, harvested into <see cref="DmPublicKeyOf"/> for completeness/diag)
    /// and so a fresh single-user node holds it before any peer admit. Idempotent; the founder's DM key is also
    /// stamped onto the synced genesis record and re-harvested via <see cref="AdoptSyncedRoster"/>, so this is the
    /// boot-time seed of the self entry (the own-key floor for the DM map, mirroring the transport own-subkey floor).
    /// </summary>
    public void SetOwnDmPublicKey(string selfPartyId, byte[] dmPublicKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selfPartyId);
        ArgumentNullException.ThrowIfNull(dmPublicKey);
        if (dmPublicKey.Length == 0) throw new ArgumentException("The DM public key must be non-empty.", nameof(dmPublicKey));
        lock (_gate)
        {
            _dmKeysByParty[selfPartyId] = (byte[])dmPublicKey.Clone();
        }
    }

    /// <summary>
    /// 2c-iii-b — record the install's OWN (genesis/self) team-scoped X-Wing PUBLIC key (the own-key floor for the
    /// X-Wing map, mirroring <see cref="SetOwnDmPublicKey"/>). The self X-Wing key is ALSO carried on this node's
    /// synced roster record (PR-A) and re-harvested via <see cref="AdoptSyncedRoster"/>, so this is the boot-time
    /// seed of the self entry — it makes the node X-Wing-capable to a SENDER (and lets this node's own resolver
    /// report its own X-Wing public key) before any peer admit converges. Idempotent.
    /// </summary>
    public void SetOwnXWingPublicKey(string selfPartyId, byte[] xwingPublicKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selfPartyId);
        ArgumentNullException.ThrowIfNull(xwingPublicKey);
        if (xwingPublicKey.Length == 0)
        {
            throw new ArgumentException("The X-Wing public key must be non-empty.", nameof(xwingPublicKey));
        }
        lock (_gate)
        {
            _xwingKeysByParty[selfPartyId] = (byte[])xwingPublicKey.Clone();
        }
    }
}
