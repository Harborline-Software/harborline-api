using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Foundation.IdentityAtlas;

/// <summary>
/// A team's TRUST ROSTER — the genesis-rooted signed membership log (enrollment Phase A; cerebrum
/// [2026-06-20]). It is the verified party→pubkey map that makes two things possible that the signing
/// mechanism ALONE cannot: (1) the <c>MemberSetTrustPolicy</c> trust gate (trust a peer iff its presented
/// pubkey ∈ this roster's verified set), and (2) FORGE-PROOF attribution (verify a message's author by
/// checking its signing key against the roster's binding for the claimed party — closing #1277 B1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Genesis-rooted signed log (taxonomy §3 guard 4).</b> The chain roots in an immutable GENESIS admission:
/// the founder self-admits (signs an <see cref="AdmissionRecord"/> over their own key with
/// <see cref="AdmissionSignature.IsGenesis"/> = true). Every subsequent admission is signed by an in-roster
/// admin who (a) is themselves validated to genesis and (b) holds <c>members:admit</c>. The roster is VALID
/// iff every member's admission chains back to the single genesis (no orphan admissions, no second genesis).
/// </para>
/// <para>
/// <b>The FIXED guards (taxonomy §3), enforced here:</b>
/// <list type="number">
///   <item><b>No-escalation</b> — an admit/grant may only confer a permission set that is a subset of the
///     admitter's currently-held set (<see cref="PermissionSet.IsSubsetOf"/>). Enforced in
///     <see cref="Admit"/> / <see cref="Grant"/>.</item>
///   <item><b>Signed no-bricking floor</b> - at least one member's signed AND live sets must hold
///     <c>grant:permissions</c>, <c>org:transfer-ownership</c>, and <c>members:admit</c>.
///     Local grants cannot supply signed evidence; removal requires a successor that still holds it live.</item>
///   <item><b>Genesis-immutable vs live-mutable</b> — revoking all of a member's permissions (or removing
///     them) does NOT remove their admission from the chain; the genesis member in particular cannot be
///     un-genesised. The chain still verifies; the live permission set is what mutates.</item>
/// </list>
/// </para>
/// <para>
/// <b>Verification uses the same <see cref="IOperationVerifier"/> path</b> as comms attribution (proven,
/// cross-language byte-stable canonical-JSON signing). An admission's signature is re-checked against the
/// admitter's stamped key; the chain check then confirms that key is itself a validated roster admin.
/// </para>
/// <para>
/// <b>In-memory + immutable-by-replacement.</b> Each mutating op returns a NEW roster (the log is append-only
/// at the chain level; permission sets mutate by replacement). This is the v1 in-memory store; the durable
/// synced append-log roster doctype (survey #1275 §4) replaces the persistence behind the same shape.
/// </para>
/// </remarks>
public sealed class MemberRoster
{
    /// <summary>Stable code for a roster change refused by the signed and live authority floor.</summary>
    public const string NoBrickingFloorCode = "roster.revocation.no_bricking_floor";

    /// <summary>Floor refusals from this rebuild, carrying the original signed removal evidence.</summary>
    public IReadOnlyList<RosterRevocationRefusal> RefusedRevocations { get; private set; } =
        Array.Empty<RosterRevocationRefusal>();

    // Transitional live permission state for the 291 guards; slice 3c replaces this with grant derivation.
    // It is deliberately outside the membership record and is never serialized as membership evidence.
    // Permissions is NULL for a member reconstructed from replicated records: no permission set rides the
    // wire any more (293 s3b2), so the roster reports NO set for that member and every reader falls through to
    // the grant closure. It is never PermissionSet.Empty - an empty set would answer, and shadow the grants.
    private sealed record MemberState(string PartyId, PrincipalId PublicKey,
        PermissionSet? Permissions, AdmissionSignature Admission)
    {
        public RosterMember Member { get; } = new(PartyId, PublicKey, Admission);
    }

    private readonly Guid _teamId;

    // LIVE membership state — the mutable set (admit/revoke/grant). What HasPermission + the trust gate read.
    private readonly IReadOnlyDictionary<string, MemberState> _byParty;

    // The IMMUTABLE, append-only ADMISSION LOG — every admission ever signed, keyed by admitted party. This is
    // SEPARATE from live state (genesis-vs-live, taxonomy §3 guard 4): revoking a member drops them from
    // _byParty but their admission STAYS in the log, so the chain still verifies through them (esp. the genesis
    // member, who is the canonical revoke-after-handoff case). ValidatesToGenesis chains over THIS log.
    private readonly IReadOnlyDictionary<string, AdmissionEntry> _admissionLog;

    private readonly string _genesisPartyId;

    private MemberRoster(
        Guid teamId,
        IReadOnlyDictionary<string, MemberState> byParty,
        IReadOnlyDictionary<string, AdmissionEntry> admissionLog,
        string genesisPartyId)
    {
        _teamId = teamId;
        _byParty = byParty;
        _admissionLog = admissionLog;
        _genesisPartyId = genesisPartyId;
    }

    /// <summary>The party id of the genesis (founding) member — the immutable chain root.</summary>
    public string GenesisPartyId => _genesisPartyId;

    /// <summary>The team this roster governs.</summary>
    public Guid TeamId => _teamId;

    /// <summary>All current members (live state).</summary>
    public IReadOnlyCollection<RosterMember> Members => _byParty.Values.Select(m => m.Member).ToArray();

    /// <summary>One entry in the immutable admission log — the (party, key, admission) tuple, retained even
    /// after the member is revoked from live state so the genesis chain still verifies through them.</summary>
    private sealed record AdmissionEntry(string PartyId, PrincipalId PublicKey, AdmissionSignature Admission);

    /// <summary>
    /// Found a roster from the GENESIS self-admission. The founder signs an admission over their OWN
    /// (party, key) with <see cref="AdmissionSignature.IsGenesis"/> = true; this is the immutable chain root.
    /// The genesis member is seeded with the <see cref="PermissionCompositions.Owner"/> composition (the full
    /// grant substrate). Throws if the genesis signature does not validate.
    /// </summary>
    public static MemberRoster Genesis(
        Guid teamId,
        string founderPartyId,
        IOperationSigner founderSigner,
        IOperationVerifier verifier,
        DateTimeOffset issuedAt,
        Guid nonce,
        string founderDmPublicKey = "",
        string founderXWingPublicKey = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(founderPartyId);
        ArgumentNullException.ThrowIfNull(founderSigner);
        ArgumentNullException.ThrowIfNull(verifier);

        var founderKey = founderSigner.IssuerId;
        // C5 — the founder SIGNS its own DM public key into the genesis self-admission, so the founder's
        // (party → DM-pubkey) binding is forge-proof from the chain root (the DM key-substitution fix).
        // C5-X — likewise the founder's own X-Wing public key (a confidentiality key) is signed in, so the
        // founder's (party → X-Wing-pubkey) binding is forge-proof from the chain root (the X-Wing key-substitution
        // DEK-leak fix, #1489).
        var admission = RosterSigning.SignAdmission(
            signer: founderSigner,
            teamId: teamId,
            admittedPartyId: founderPartyId,
            admittedPublicKey: founderKey,
            admittedByPartyId: founderPartyId,
            isGenesis: true,
            issuedAt: issuedAt,
            nonce: nonce,
            admittedDmPublicKey: founderDmPublicKey ?? string.Empty,
            admittedXWingPublicKey: founderXWingPublicKey ?? string.Empty);

        // Re-verify the genesis admission as a fail-closed sanity gate (a malformed founder signer must not
        // produce an "unverifiable genesis" that later breaks every chain check).
        if (!RosterSigning.VerifyAdmission(teamId, founderPartyId, founderKey, admission, verifier))
        {
            throw new InvalidOperationException("Genesis admission signature did not verify.");
        }

        var founder = new MemberState(founderPartyId, founderKey, PermissionCompositions.Owner, admission);
        var map = new Dictionary<string, MemberState>(StringComparer.Ordinal) { [founderPartyId] = founder };
        var log = new Dictionary<string, AdmissionEntry>(StringComparer.Ordinal)
        {
            [founderPartyId] = new AdmissionEntry(founderPartyId, founderKey, admission),
        };
        return new MemberRoster(teamId, map, log, founderPartyId);
    }

    /// <summary>HKDF info-string prefix for the deterministic-genesis nonce. Version-stamped so a future v2
    /// derivation can coexist with already-deployed v1 installs during migration (same discipline as
    /// <c>TeamSubkeyDerivation.InfoPrefix</c>).</summary>
    public const string StableGenesisInfoPrefix = "sunfish-roster-genesis-v1:";

    /// <summary>
    /// Found a roster with a genesis self-admission that is <b>STABLE across process restarts</b> (the #1291 F1
    /// convergence fix). Unlike <see cref="Genesis"/> — which takes a caller-supplied <c>nonce</c> and
    /// <c>issuedAt</c> — this DERIVES both deterministically from the founder's stable identity, so every boot of
    /// the same node reconstructs the <b>byte-identical</b> genesis record (same signature, same content-derived
    /// RecordId).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this matters (F1).</b> The synced roster doctype's RecordId is content-derived
    /// (<c>admission:{party}:{nonce}</c>) and deduped by RecordId only. If the host minted a fresh random nonce
    /// on every boot (<see cref="Genesis"/> with <c>Guid.NewGuid()</c>), a restart produced a SECOND genesis
    /// record: the hydrated boot-1 genesis + the fresh boot-2 genesis have different nonces ⇒ different RecordIds
    /// ⇒ not deduped ⇒ <see cref="FromSyncedRecords"/> sees ≥2 genesis ⇒ <see cref="Empty"/> ⇒ the node can never
    /// adopt a synced member AND the duplicate genesis poisons every peer's convergence. A deterministic genesis
    /// collapses the re-mint to the EXISTING record (RecordId matches ⇒ dedup) ⇒ exactly ONE genesis ever.
    /// </para>
    /// <para>
    /// <b>Derivation.</b> The nonce is HKDF-SHA256(ikm = the founder's public-key bytes, salt = empty,
    /// info = <see cref="StableGenesisInfoPrefix"/> + teamId + ":" + founderPartyId, L = 16) folded into a Guid.
    /// Keying on the founder's public key (itself derived from the install's stable root seed) + the teamId +
    /// the party id makes the nonce unique per (install, team, founder) yet identical across reboots — and it
    /// requires no persisted nonce (no durability dependency, the cleanest-long-term option per the F1 verdict).
    /// The issuance instant is pinned to a fixed deterministic epoch (the chain-root self-admission carries no
    /// meaningful timestamp), so the whole record — and thus its signature — is byte-stable across restarts.
    /// </para>
    /// </remarks>
    public static MemberRoster StableGenesis(
        Guid teamId,
        string founderPartyId,
        IOperationSigner founderSigner,
        IOperationVerifier verifier,
        string founderDmPublicKey = "",
        string founderXWingPublicKey = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(founderPartyId);
        ArgumentNullException.ThrowIfNull(founderSigner);
        ArgumentNullException.ThrowIfNull(verifier);

        var nonce = DeriveStableGenesisNonce(teamId, founderPartyId, founderSigner.IssuerId);
        // The genesis self-admission carries no meaningful issuance instant (it is the immutable chain root), so
        // pin it to a fixed epoch to keep the record fully deterministic across restarts. RosterSigning truncates
        // to epoch-ms; UnixEpoch is already ms-aligned. C5 — the founder's DM public key (HKDF(root, teamId), itself
        // deterministic from the root seed) is signed in, so the genesis record stays BYTE-stable across restarts.
        // C5-X — the founder's X-Wing public key (HKDF(root, teamId) over the X-Wing domain — equally deterministic)
        // is signed in too, so adding it keeps the genesis record byte-stable across restarts.
        return Genesis(
            teamId, founderPartyId, founderSigner, verifier, DateTimeOffset.UnixEpoch, nonce,
            founderDmPublicKey, founderXWingPublicKey);
    }

    /// <summary>
    /// Derive the DETERMINISTIC genesis nonce for a (team, founder, founder-key) triple — HKDF-SHA256 over the
    /// founder's public key into a 16-byte Guid. Stable across restarts; unique per install/team/founder.
    /// Exposed so a host can reconstruct the same nonce off-line if it ever needs to (it normally just calls
    /// <see cref="StableGenesis"/>).
    /// </summary>
    public static Guid DeriveStableGenesisNonce(Guid teamId, string founderPartyId, PrincipalId founderKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(founderPartyId);

        var info = Encoding.UTF8.GetBytes(
            StableGenesisInfoPrefix + teamId.ToString("D") + ":" + founderPartyId);
        Span<byte> nonceBytes = stackalloc byte[16];
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: founderKey.AsSpan(),
            output: nonceBytes,
            salt: ReadOnlySpan<byte>.Empty,
            info: info);
        return new Guid(nonceBytes);
    }

    /// <summary>True iff <paramref name="partyId"/> is a current member.</summary>
    public bool Contains(string partyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partyId);
        return _byParty.ContainsKey(partyId);
    }

    /// <summary>The member entry for <paramref name="partyId"/>, or null when not a member.</summary>
    public RosterMember? Find(string partyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partyId);
        return _byParty.TryGetValue(partyId, out var m) ? m.Member : null;
    }

    /// <summary>
    /// The verified set of member SIGNING public keys — the trust-gate input. A peer is trusted iff the pubkey
    /// it presents in HELLO is in this set (the <c>MemberSetTrustPolicy</c> generalization of
    /// <c>SharedRootTrustPolicy</c>). Returned as raw 32-byte Ed25519 keys (the shape the trust policy
    /// consumes), so kernel-sync needs no dependency on this package.
    /// </summary>
    public IReadOnlyList<byte[]> TrustedPublicKeys() =>
        _byParty.Values.Select(m => m.PublicKey.AsSpan().ToArray()).ToArray();

    /// <summary>
    /// FORGE-PROOF ATTRIBUTION lookup (closes #1277 B1). Returns the public key bound to
    /// <paramref name="partyId"/> in this roster, or null when the party is not a member. An attribution check
    /// passes ONLY when the message's actual signing key equals this bound key — so an attacker who signs with
    /// their OWN key while claiming a victim's partyId FAILS (their key ≠ the victim's bound key).
    /// </summary>
    public PrincipalId? PublicKeyOf(string partyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partyId);
        return _byParty.TryGetValue(partyId, out var m) ? m.PublicKey : null;
    }

    /// <summary>The effective permission set held by <paramref name="partyId"/>, or null when not a member.</summary>
    public PermissionSet? PermissionsOf(string partyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partyId);
        return _byParty.TryGetValue(partyId, out var m) ? m.Permissions : null;
    }

    /// <summary>True iff <paramref name="partyId"/> is a member AND holds <paramref name="permission"/>.</summary>
    public bool HasPermission(string partyId, string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        return _byParty.TryGetValue(partyId, out var m) && m.Permissions?.Contains(permission) == true;
    }

    /// <summary>
    /// Admit a new member, signed by an in-roster admitter. Returns a NEW roster with the member added.
    /// Enforces: the admitter is a current member who holds <c>members:admit</c>; NO-ESCALATION (the admitted
    /// set ⊆ the admitter's held set); and re-verifies the produced admission signature. Throws
    /// <see cref="RosterGuardException"/> on a guard violation.
    /// </summary>
    public MemberRoster Admit(
        string admitterPartyId,
        IOperationSigner admitterSigner,
        string newPartyId,
        PrincipalId newPublicKey,
        PermissionSet grantedPermissions,
        IOperationVerifier verifier,
        DateTimeOffset issuedAt,
        Guid nonce,
        string newDmPublicKey = "",
        string newXWingPublicKey = "",
        string admittedViaTokenId = "",
        string admittedUnderSessionEvidence = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(admitterPartyId);
        ArgumentNullException.ThrowIfNull(admitterSigner);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPartyId);
        ArgumentNullException.ThrowIfNull(grantedPermissions);
        ArgumentNullException.ThrowIfNull(verifier);

        if (!_byParty.TryGetValue(admitterPartyId, out var admitter))
        {
            throw new RosterGuardException($"Admitter '{admitterPartyId}' is not a member of the roster.");
        }
        // The admitter must actually be the one signing — bind the signer's key to the claimed admitter party.
        if (!admitter.PublicKey.Equals(admitterSigner.IssuerId))
        {
            throw new RosterGuardException(
                $"Admitter signing key does not match the roster binding for '{admitterPartyId}'.");
        }
        if (Held(admitter).Contains(Permission.MembersAdmit) is false)
        {
            throw new RosterGuardException(
                $"Admitter '{admitterPartyId}' does not hold '{Permission.MembersAdmit}'.");
        }
        // NO-ESCALATION: the admitted set must be a subset of the admitter's held set.
        if (!grantedPermissions.IsSubsetOf(Held(admitter)))
        {
            throw new RosterGuardException(
                "No-escalation violated: the admitted permission set exceeds the admitter's held set.");
        }
        if (_byParty.ContainsKey(newPartyId))
        {
            throw new RosterGuardException($"Party '{newPartyId}' is already a member.");
        }

        // C5 — the admitter SIGNS the joiner's DM public key into the admission, so the joiner's
        // (party → DM-pubkey) binding is forge-proof exactly like its principal key (the DM key-substitution fix).
        // C5-X — likewise the joiner's X-Wing public key (a confidentiality key) is signed in, so the joiner's
        // (party → X-Wing-pubkey) binding is forge-proof (the X-Wing key-substitution DEK-leak fix, #1489).
        var admission = RosterSigning.SignAdmission(
            signer: admitterSigner,
            teamId: _teamId,
            admittedPartyId: newPartyId,
            admittedPublicKey: newPublicKey,
            admittedByPartyId: admitterPartyId,
            isGenesis: false,
            issuedAt: issuedAt,
            nonce: nonce,
            admittedDmPublicKey: newDmPublicKey ?? string.Empty,
            admittedXWingPublicKey: newXWingPublicKey ?? string.Empty,
            // #3167 R1.2 — bind the pairing token id + mint-session evidence INTO the signed admission on the
            // web-admitted-member pairing path (empty for proximity / plain invite, which pass nothing).
            admittedViaTokenId: admittedViaTokenId ?? string.Empty,
            admittedUnderSessionEvidence: admittedUnderSessionEvidence ?? string.Empty);

        if (!RosterSigning.VerifyAdmission(_teamId, newPartyId, newPublicKey, admission, verifier))
        {
            throw new RosterGuardException("Produced admission signature did not verify.");
        }

        var next = new Dictionary<string, MemberState>(_byParty, StringComparer.Ordinal)
        {
            [newPartyId] = new MemberState(newPartyId, newPublicKey, grantedPermissions, admission),
        };
        // Append to the immutable admission log (a re-admission of a previously-revoked party records the NEW
        // admission — the latest signed admission for that party).
        var log = new Dictionary<string, AdmissionEntry>(_admissionLog, StringComparer.Ordinal)
        {
            [newPartyId] = new AdmissionEntry(newPartyId, newPublicKey, admission),
        };
        return new MemberRoster(_teamId, next, log, _genesisPartyId);
    }

    /// <summary>
    /// Re-set a member's live permission set (the <c>grant:permissions</c> primitive). Returns a NEW roster.
    /// Enforces NO-ESCALATION (the new set ⊆ the granter's held set) and the NO-BRICKING FLOOR (the change may
    /// not remove the last signed and live holder of {grant:permissions, org:transfer-ownership, members:admit}). Throws
    /// <see cref="RosterGuardException"/> on violation. Does NOT alter the admission chain (the member stays
    /// rooted; only their LIVE permissions change).
    /// </summary>
    public MemberRoster Grant(
        string granterPartyId,
        string targetPartyId,
        PermissionSet newPermissions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(granterPartyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPartyId);
        ArgumentNullException.ThrowIfNull(newPermissions);

        if (!_byParty.TryGetValue(granterPartyId, out var granter))
        {
            throw new RosterGuardException($"Granter '{granterPartyId}' is not a member.");
        }
        if (Held(granter).Contains(Permission.GrantPermissions) is false)
        {
            throw new RosterGuardException(
                $"Granter '{granterPartyId}' does not hold '{Permission.GrantPermissions}'.");
        }
        if (!_byParty.TryGetValue(targetPartyId, out var target))
        {
            throw new RosterGuardException($"Target '{targetPartyId}' is not a member.");
        }
        // NO-ESCALATION: the new set must be a subset of the granter's held set.
        if (!newPermissions.IsSubsetOf(Held(granter)))
        {
            throw new RosterGuardException(
                "No-escalation violated: the granted set exceeds the granter's held set.");
        }

        var next = new Dictionary<string, MemberState>(_byParty, StringComparer.Ordinal)
        {
            [targetPartyId] = target with { Permissions = newPermissions },
        };
        // Grant mutates LIVE permissions only — the admission log (the chain) is untouched.
        var candidate = new MemberRoster(_teamId, next, _admissionLog, _genesisPartyId);

        // NO-BRICKING FLOOR: the change must not extinguish the root-grant capability.
        if (!candidate.HasRootGrantHolder())
        {
            throw new RosterGuardException(
                "No-bricking floor violated: the change would remove the last holder of the root-grant "
                + $"({Permission.GrantPermissions} + {Permission.OrgTransferOwnership} + {Permission.MembersAdmit}).",
                NoBrickingFloorCode);
        }
        return candidate;
    }

    /// <summary>
    /// Remove a member from LIVE state (the <c>members:revoke</c> op). Returns a NEW roster. Enforces the
    /// NO-BRICKING FLOOR (cannot remove the last holder of the root-grant — transfer first). The removed
    /// member stays in the immutable admission CHAIN (genesis-vs-live); removal only drops them from live
    /// membership/permission state. Throws <see cref="RosterGuardException"/> on violation.
    /// </summary>
    /// <param name="revokerPartyId">The revoking member.</param>
    /// <param name="targetPartyId">The member to remove from live state.</param>
    /// <param name="authority">
    /// Where a party's authority comes from on the REPLICATED path (the local grant store) - the replay runs
    /// this same method, so a revocation refused locally is refused on replay. Null on the local path: the
    /// roster's own live sets answer.
    /// </param>
    public MemberRoster Revoke(
        string revokerPartyId, string targetPartyId, Func<string, PermissionSet>? authority = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revokerPartyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPartyId);

        if (!_byParty.TryGetValue(revokerPartyId, out var revoker))
        {
            throw new RosterGuardException($"Revoker '{revokerPartyId}' is not a member.");
        }
        if ((authority?.Invoke(revokerPartyId) ?? Held(revoker)).Contains(Permission.MembersRevoke) is false)
        {
            throw new RosterGuardException(
                $"Revoker '{revokerPartyId}' does not hold '{Permission.MembersRevoke}'.");
        }
        if (!_byParty.ContainsKey(targetPartyId))
        {
            throw new RosterGuardException($"Target '{targetPartyId}' is not a member.");
        }

        var next = new Dictionary<string, MemberState>(_byParty, StringComparer.Ordinal);
        next.Remove(targetPartyId);
        // Revoke drops the member from LIVE state but KEEPS their admission in the immutable log (genesis-vs-
        // live): the chain still verifies through them — the canonical revoke-the-genesis-support-after-handoff
        // case. The log is unchanged.
        var candidate = new MemberRoster(_teamId, next, _admissionLog, _genesisPartyId);

        // NO-BRICKING FLOOR: revoking the last root-grant holder is forbidden — transfer ownership first.
        if (!candidate.HasRootGrantHolder(authority))
        {
            throw new RosterGuardException(
                "No-bricking floor violated: cannot revoke the last holder of the root-grant "
                + $"({Permission.GrantPermissions} + {Permission.OrgTransferOwnership} + {Permission.MembersAdmit}) "
                + "- transfer ownership first.", NoBrickingFloorCode);
        }
        return candidate;
    }

    /// <summary>
    /// VALIDATE the whole roster to genesis: every member's admission signature re-verifies for its stamped
    /// admitter key, and every admitter (except the genesis self-admission) chains back to the single genesis
    /// member, transitively. Returns true iff the roster is internally consistent and fully genesis-rooted.
    /// Fail-closed — any unverifiable / orphan / second-genesis admission makes the whole roster invalid.
    /// </summary>
    public bool ValidatesToGenesis(IOperationVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);

        // The chain is validated over the IMMUTABLE ADMISSION LOG (not live state) — so a revoked member
        // (incl. the genesis support member after handoff) does not break the chain.

        // Exactly one genesis admission, and it must be the recorded genesis, self-admitted.
        var genesisEntries = _admissionLog.Values.Where(e => e.Admission.IsGenesis).ToList();
        if (genesisEntries.Count != 1) return false;
        var genesis = genesisEntries[0];
        if (!string.Equals(genesis.PartyId, _genesisPartyId, StringComparison.Ordinal)) return false;
        // Genesis self-admission: admitter == admitted (same key + same party).
        if (!string.Equals(genesis.Admission.AdmittedByPartyId, genesis.PartyId, StringComparison.Ordinal))
            return false;
        if (!string.Equals(
                genesis.Admission.AdmittedByPublicKey, genesis.PublicKey.ToBase64Url(), StringComparison.Ordinal))
            return false;

        // Every logged admission must re-verify for its stamped admitter key.
        foreach (var e in _admissionLog.Values)
        {
            if (!RosterSigning.VerifyAdmission(_teamId, e.PartyId, e.PublicKey, e.Admission, verifier))
                return false;
        }

        // Every non-genesis admission must be by a logged member whose recorded key matches the admission's
        // stamped admitter key (the admitter is itself in the chain — chains to genesis transitively).
        foreach (var e in _admissionLog.Values)
        {
            if (e.Admission.IsGenesis) continue;
            var admitterParty = e.Admission.AdmittedByPartyId;
            if (!_admissionLog.TryGetValue(admitterParty, out var admitter)) return false; // orphan admission
            if (!string.Equals(
                    admitter.PublicKey.ToBase64Url(), e.Admission.AdmittedByPublicKey, StringComparison.Ordinal))
                return false; // admitter key drift — not the recorded admitter
        }

        // Reachability: every logged admission must be reachable from genesis by following AdmittedByPartyId
        // edges (catches a cycle of mutual non-genesis admissions that has no genesis root).
        var reachable = new HashSet<string>(StringComparer.Ordinal) { _genesisPartyId };
        bool grew;
        do
        {
            grew = false;
            foreach (var e in _admissionLog.Values)
            {
                if (reachable.Contains(e.PartyId)) continue;
                if (reachable.Contains(e.Admission.AdmittedByPartyId))
                {
                    reachable.Add(e.PartyId);
                    grew = true;
                }
            }
        } while (grew);

        // Finally: every LIVE member must be present in the admission log (no live member without an admission).
        foreach (var m in _byParty.Values)
        {
            if (!_admissionLog.ContainsKey(m.PartyId)) return false;
        }

        return reachable.Count == _admissionLog.Count;
    }

    /// <summary>True iff a live member holds all three root-authority atoms.</summary>
    public bool HasRootGrantHolder(Func<string, PermissionSet>? authority = null) =>
        _byParty.Values.Any(member =>
            (authority?.Invoke(member.PartyId) ?? member.Permissions) is { } held && HoldsRootGrant(held));

    // Fail-closed read of a member's live set: a member the roster holds no set for holds nothing locally.
    private static PermissionSet Held(MemberState member) => member.Permissions ?? PermissionSet.Empty;

    private static bool HoldsRootGrant(PermissionSet signedPermissions) =>
        signedPermissions.Contains(Permission.GrantPermissions)
        && signedPermissions.Contains(Permission.OrgTransferOwnership)
        && signedPermissions.Contains(Permission.MembersAdmit);

    // ── ROSTER-SYNC (gap #1): emit syncable records + reconstruct a validated roster from peer records ────────

    /// <summary>
    /// Emit the syncable ADMISSION records for the whole genesis-rooted log — one
    /// <see cref="MemberAdmissionRecord"/> per logged admission (including the genesis self-admission and any
    /// since-revoked member, whose admission stays in the immutable chain). The roster-sync producer pushes
    /// these onto the synced <c>"roster"</c> doctype so a peer can reconstruct + validate the team roster. The
    /// permission set carried is always the signed admission set. Local grants do not rewrite admission
    /// evidence; a separate signed revocation drops a revoked member on the peer.
    /// </summary>
    public IReadOnlyList<MemberAdmissionRecord> EnumerateAdmissions()
    {
        var teamId = _teamId.ToString("D");
        var list = new List<MemberAdmissionRecord>(_admissionLog.Count);
        foreach (var e in _admissionLog.Values)
        {
            // C5 — the DM public key on the emitted record comes from the SIGNED admission (e.Admission.DmPublicKey),
            // NOT an external stamp, so what rides the wire is exactly what the admitter signed (forge-proof). A
            // legacy admission with no signed DM key emits null (the field stays empty on the wire).
            var dmKey = DecodeSignedDmKeyOrNull(e.Admission.DmPublicKey);
            // C5-X — same for the X-Wing public key: emit the SIGNED value (e.Admission.XWingPublicKey), NOT an
            // external stamp, so what rides the wire is exactly what the admitter signed (forge-proof). A legacy
            // admission with no signed X-Wing key emits null (X-Wing-incapable on the wire → suite #1).
            var xwingKey = DecodeSignedXWingKeyOrNull(e.Admission.XWingPublicKey);
            list.Add(new MemberAdmissionRecord(
                teamId, e.PartyId, e.PublicKey, e.Admission,
                TransportPublicKey: null, DmPublicKey: dmKey, XWingPublicKey: xwingKey));
        }
        return list;
    }

    /// <summary>
    /// Decode a SIGNED DM public key (base64url, as carried in <see cref="AdmissionSignature.DmPublicKey"/>) into its
    /// raw 32-byte form, or null when empty/malformed. Used to surface the forge-proof DM key off the validated
    /// admission (never an external/unsigned source). Fail-closed: a malformed signed value yields null (no DM key).
    /// </summary>
    internal static byte[]? DecodeSignedDmKeyOrNull(string? signedDmKeyB64Url)
    {
        if (string.IsNullOrEmpty(signedDmKeyB64Url)) return null;
        try { return PrincipalId.FromBase64Url(signedDmKeyB64Url).AsSpan().ToArray(); }
        catch (FormatException) { return null; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// C5-X — decode a SIGNED X-Wing public key (base64url, as carried in <see cref="AdmissionSignature.XWingPublicKey"/>)
    /// into its raw byte form, or null when empty/malformed. Used to surface the forge-proof X-Wing key off the validated
    /// admission (never an external/unsigned source). Unlike the 32-byte DM key, the X-Wing key is a 1216-byte raw key
    /// (not a <see cref="PrincipalId"/>), so it uses a plain base64url(no-padding) codec — and foundation-identity-atlas
    /// is kernel-free, so it does NOT length-validate against the kernel's X-Wing length here (the host wire/durable
    /// layer, which owns that constant, enforces the exact 1216-byte length on reconstruction). Fail-closed: a
    /// non-base64url signed value yields null (no X-Wing key).
    /// </summary>
    internal static byte[]? DecodeSignedXWingKeyOrNull(string? signedXWingKeyB64Url)
    {
        if (string.IsNullOrEmpty(signedXWingKeyB64Url)) return null;
        try { return DecodeRawBase64Url(signedXWingKeyB64Url); }
        catch (FormatException) { return null; }
    }

    /// <summary>base64url (no padding) encode of a raw byte span — used for the 1216-byte X-Wing key (not
    /// <see cref="PrincipalId"/>-shaped, so no <c>ToBase64Url</c>). Byte-identical to the host wire layer's encoder.</summary>
    private static string EncodeRawBase64Url(ReadOnlySpan<byte> bytes)
    {
        var b64 = Convert.ToBase64String(bytes);
        return b64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>base64url (no padding) decode of a raw key — throws <see cref="FormatException"/> on a non-base64url
    /// value. Byte-identical to the host wire layer's decoder (sans the host's kernel-length check, which is enforced
    /// there, not in this kernel-free package).</summary>
    private static byte[] DecodeRawBase64Url(string b64Url)
    {
        var padded = b64Url.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
            case 1: throw new FormatException("Malformed base64url X-Wing key (bad length).");
        }
        return Convert.FromBase64String(padded);
    }

    /// <summary>
    /// C5 defence-in-depth — a synced admission record's CARRIED DM key (<see cref="MemberAdmissionRecord.DmPublicKey"/>,
    /// the raw byte[] that physically rides the wire) MUST equal the DM key the admission SIGNATURE attests
    /// (<see cref="AdmissionSignature.DmPublicKey"/>). They are produced consistently by the projection
    /// (<c>RosterRecordCrdtState.ToAdmissionOrNull</c> derives both from the same wire field), so a mismatch can only
    /// arise from a tampered/buggy record — reject it. Without this, a record could carry one (unsigned) byte[] DM key
    /// while signing another; the harvest reads the SIGNED value anyway, but rejecting the inconsistent record removes
    /// all ambiguity (the carried byte[] is never an independent harvest source).
    /// </summary>
    private static bool CarriedDmKeyMatchesSigned(MemberAdmissionRecord record)
    {
        var signed = record.Admission.DmPublicKey ?? string.Empty;
        var carried = record.DmPublicKey;
        if (carried is null || carried.Length == 0)
        {
            // No carried byte[] DM key → consistent only if the signature also attests none.
            return signed.Length == 0;
        }
        // A carried byte[] DM key → its base64url MUST equal the signed value.
        string carriedB64;
        try { carriedB64 = PrincipalId.FromBytes(carried).ToBase64Url(); }
        catch (ArgumentException) { return false; } // a non-32-byte carried key is never well-formed.
        return string.Equals(carriedB64, signed, StringComparison.Ordinal);
    }

    /// <summary>
    /// C5-X defence-in-depth — a synced admission record's CARRIED X-Wing key
    /// (<see cref="MemberAdmissionRecord.XWingPublicKey"/>, the raw byte[] that physically rides the wire) MUST equal the
    /// X-Wing key the admission SIGNATURE attests (<see cref="AdmissionSignature.XWingPublicKey"/>). They are produced
    /// consistently by the projection (<c>RosterRecordCrdtState.ToAdmissionOrNull</c> derives both from the same wire
    /// field), so a mismatch can only arise from a tampered/buggy record — reject it. Without this, a record could carry
    /// one (unsigned) byte[] X-Wing key while signing another; the harvest reads the SIGNED value anyway, but rejecting
    /// the inconsistent record removes all ambiguity (the carried byte[] is never an independent harvest source — the
    /// exact hole #1489 caught).
    /// </summary>
    private static bool CarriedXWingKeyMatchesSigned(MemberAdmissionRecord record)
    {
        var signed = record.Admission.XWingPublicKey ?? string.Empty;
        var carried = record.XWingPublicKey;
        if (carried is null || carried.Length == 0)
        {
            // No carried byte[] X-Wing key → consistent only if the signature also attests none.
            return signed.Length == 0;
        }
        // A carried byte[] X-Wing key → its base64url MUST equal the signed value.
        return string.Equals(EncodeRawBase64Url(carried), signed, StringComparison.Ordinal);
    }

    /// <summary>
    /// C5 — the forge-proof DM-encryption PUBLIC key bound to <paramref name="partyId"/> in this roster's SIGNED
    /// admission chain, or null when the party is not a live member or its admission signed no DM key. Unlike the
    /// prior last-carried-wins harvest of an unsigned roster field, this returns ONLY the DM key the admission
    /// SIGNATURE attests for that party — a substituted DM key never reaches a validated member (its record fails
    /// signature verification and is dropped on rebuild). This is the harvest source the host's live DM-key map uses.
    /// </summary>
    public byte[]? DmPublicKeyOf(string partyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partyId);
        return _byParty.TryGetValue(partyId, out var m)
            ? DecodeSignedDmKeyOrNull(m.Admission.DmPublicKey)
            : null;
    }

    /// <summary>
    /// C5-X — the forge-proof X-Wing (X25519 + ML-KEM-768) PUBLIC key bound to <paramref name="partyId"/> in this
    /// roster's SIGNED admission chain, or null when the party is not a live member or its admission signed no X-Wing
    /// key (X-Wing-incapable → a sender boxes it suite #1). Unlike the prior last-carried-wins harvest of the UNSIGNED
    /// roster field (the #1489 confidentiality hole), this returns ONLY the X-Wing key the admission SIGNATURE attests
    /// for that party — a substituted X-Wing key never reaches a validated member (its record fails signature
    /// verification and is dropped on rebuild). This is the chain-validated harvest source the host's live X-Wing-key
    /// map uses, paralleling <see cref="DmPublicKeyOf"/>.
    /// </summary>
    public byte[]? XWingPublicKeyOf(string partyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partyId);
        return _byParty.TryGetValue(partyId, out var m)
            ? DecodeSignedXWingKeyOrNull(m.Admission.XWingPublicKey)
            : null;
    }

    /// <summary>
    /// Produce a SIGNED revocation for SYNC (the syncable counterpart of <see cref="Revoke"/> — gap #1). Unlike
    /// <see cref="Revoke"/> (the LIVE local op that drops a member with no signature, because the caller is the
    /// trusted local admin), this STAMPS a <see cref="RevocationSignature"/> a peer can independently validate.
    /// Enforces the SAME authority + no-bricking floor as <see cref="Revoke"/> (the revoker is an in-roster
    /// member holding <c>members:revoke</c> whose signing key matches its roster binding; revoking the last
    /// root-grant holder is forbidden), then returns BOTH the post-revocation roster AND the signed record to
    /// sync. Throws <see cref="RosterGuardException"/> on a guard violation.
    /// </summary>
    public (MemberRoster Roster, MemberRevocationRecord Signed) SignRevoke(
        string revokerPartyId,
        IOperationSigner revokerSigner,
        string targetPartyId,
        IOperationVerifier verifier,
        DateTimeOffset issuedAt,
        Guid nonce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revokerPartyId);
        ArgumentNullException.ThrowIfNull(revokerSigner);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPartyId);
        ArgumentNullException.ThrowIfNull(verifier);

        if (!_byParty.TryGetValue(revokerPartyId, out var revoker))
        {
            throw new RosterGuardException($"Revoker '{revokerPartyId}' is not a member.");
        }
        // The revoker must actually be the one signing — bind the signer's key to the claimed revoker party.
        if (!revoker.PublicKey.Equals(revokerSigner.IssuerId))
        {
            throw new RosterGuardException(
                $"Revoker signing key does not match the roster binding for '{revokerPartyId}'.");
        }

        // Revoke (Phase B live op) enforces members:revoke + target-is-member + the no-bricking floor and
        // returns the post-revocation roster — reuse it so the local state matches exactly what the signed
        // record means.
        var roster = Revoke(revokerPartyId, targetPartyId);

        var signed = RosterSigning.SignRevocation(
            signer: revokerSigner,
            teamId: _teamId,
            revokedPartyId: targetPartyId,
            revokedByPartyId: revokerPartyId,
            issuedAt: issuedAt,
            nonce: nonce);

        // Fail-closed sanity: the record we are about to sync must re-verify (a malformed signer must not
        // produce an unverifiable revocation that a peer would silently drop).
        if (!RosterSigning.VerifyRevocation(_teamId, targetPartyId, signed, verifier))
        {
            throw new RosterGuardException("Produced revocation signature did not verify.");
        }
        return (roster, new MemberRevocationRecord(_teamId.ToString("D"), targetPartyId, signed));
    }

    /// <summary>
    /// THE TRUST ANCHOR (gap #1 integrity core). Reconstruct a fully-VALIDATED roster from a set of synced
    /// records — the genesis self-admission + every admission + every revocation a peer shipped. A node
    /// receiving roster deltas calls this to rebuild the team roster from the synced records (never trusting the
    /// sender's say-so): each record is independently re-verified to genesis, and any forged / unsigned /
    /// wrong-signer / orphan record is DROPPED (fail-closed) — so a peer CANNOT inject a fake member by sending
    /// a bogus roster delta.
    /// </summary>
    /// <remarks>
    /// <para><b>Validation pipeline (every gate fail-closed):</b></para>
    /// <list type="number">
    ///   <item>EXACTLY ONE genesis admission (self-signed, <see cref="AdmissionSignature.IsGenesis"/>) — its
    ///     party id is the chain root. Zero or two+ genesis records ⇒ the whole roster is rejected (returns an
    ///     empty/invalid roster) — a second genesis is an injection attempt.</item>
    ///   <item>Each admission's signature re-verifies for its stamped admitter key
    ///     (<see cref="RosterSigning.VerifyAdmission"/>); a bad signature drops that record.</item>
    ///   <item>The chain is built FROM genesis OUTWARD: an admission is admitted into the rebuilt roster only
    ///     when its stamped admitter is ALREADY a validated member holding <c>members:admit</c> whose recorded
    ///     key matches the admission's stamped admitter key, AND no-escalation holds (admitted set ⊆ admitter's
    ///     held set). An orphan admission (admitter not reachable from genesis) is DROPPED.</item>
    ///   <item>Each revocation re-verifies (<see cref="RosterSigning.VerifyRevocation"/>) AND its stamped
    ///     revoker is a validated member holding <c>members:revoke</c>; a valid revocation drops the target from
    ///     LIVE state (the admission stays in the chain). A forged/unauthorized revocation is DROPPED.</item>
    /// </list>
    /// <para>
    /// CRDT-convergent + order-independent: the records arrive in any order (the CRDT list's total order is not
    /// causal), so the chain is built by repeated relaxation (admit every record whose admitter is by-then
    /// validated, until no more can be added) — the same fixpoint <see cref="ValidatesToGenesis"/> uses.
    /// </para>
    /// </remarks>
    public static MemberRoster FromSyncedRecords(
        IEnumerable<MemberAdmissionRecord> admissions,
        IEnumerable<MemberRevocationRecord> revocations,
        IOperationVerifier verifier,
        Func<string, DateTimeOffset, DateTimeOffset>? orderTime = null,
        IRosterAuthority? authority = null)
    {
        ArgumentNullException.ThrowIfNull(admissions);
        ArgumentNullException.ThrowIfNull(revocations);
        ArgumentNullException.ThrowIfNull(verifier);

        var admissionList = admissions.ToList();
        var revocationList = revocations.ToList();

        // (1) Exactly one VALID, self-signed genesis. Anything else = no trustworthy root → empty/invalid roster.
        var genesisCandidates = admissionList
            .Where(a => a.Admission.IsGenesis
                && Guid.TryParse(a.TeamId, out _)
                && string.Equals(a.Admission.AdmittedByPartyId, a.PartyId, StringComparison.Ordinal)
                && string.Equals(a.Admission.AdmittedByPublicKey, a.PublicKey.ToBase64Url(), StringComparison.Ordinal))
            .ToList();
        if (genesisCandidates.Count != 1)
        {
            return Empty(); // zero genesis (no root) or ≥2 genesis (injection) → reject the whole roster.
        }
        var genesis = genesisCandidates[0];
        var teamId = Guid.Parse(genesis.TeamId);
        if (!RosterSigning.VerifyAdmission(teamId, genesis.PartyId, genesis.PublicKey, genesis.Admission, verifier))
        {
            return Empty();
        }
        // C5 — the genesis record's carried DM key must match what it signed (defence-in-depth; the harvest reads the
        // signed value regardless). An inconsistent genesis is a tampered root → reject the whole roster fail-closed.
        if (!CarriedDmKeyMatchesSigned(genesis))
        {
            return Empty();
        }
        // C5-X — same gate for the genesis X-Wing key (the harvest reads the signed value regardless). An inconsistent
        // genesis X-Wing field is a tampered root → reject the whole roster fail-closed.
        if (!CarriedXWingKeyMatchesSigned(genesis))
        {
            return Empty();
        }

        // No permission set rides the wire any more (293 s3b2), so the authority the chain gates read comes
        // from the local grant store through IRosterAuthority. The chain root is the one exception: the genesis
        // self-admission IS the root-authority evidence, so it keeps the owner floor.
        // ONE tenant-key form for both authority readers (the projection's chain check already uses "D"): the
        // parsed team id, never the record's arrival form. Read once per party per rebuild - a rebuild is a
        // point-in-time evaluation, and the relaxation fixpoint asks for the same party many times.
        var canonicalTeamId = teamId.ToString("D");
        var authorityByParty = new Dictionary<string, PermissionSet>(StringComparer.Ordinal);
        PermissionSet AuthorityOf(string partyId)
        {
            if (string.Equals(partyId, genesis.PartyId, StringComparison.Ordinal))
                return PermissionCompositions.Owner;
            if (!authorityByParty.TryGetValue(partyId, out var held))
            {
                held = authority?.PermissionsFor(canonicalTeamId, partyId) ?? PermissionSet.Empty;
                authorityByParty[partyId] = held;
            }
            return held;
        }

        var live = new Dictionary<string, MemberState>(StringComparer.Ordinal)
        {
            [genesis.PartyId] = new MemberState(
                genesis.PartyId, genesis.PublicKey, PermissionCompositions.Owner, genesis.Admission),
        };
        var log = new Dictionary<string, AdmissionEntry>(StringComparer.Ordinal)
        {
            [genesis.PartyId] = new AdmissionEntry(genesis.PartyId, genesis.PublicKey, genesis.Admission),
        };

        // (2)+(3) Relax admissions from genesis outward until no more can be validated (order-independent).
        // Only records into THIS team are eligible (a record naming a different team is dropped — cross-team
        // injection).
        //
        // C5 round-2 (sec-eng re-review BLOCKER — admit-capable re-admission overwrite). Two coupled properties:
        //   • FIRST-WRITE-WINS — a party's (party → identity / DM-pubkey / permission) binding is set ONCE from its
        //     EARLIEST genesis-rooted admission and is IMMUTABLE. A later same-party admission — EVEN one signed by a
        //     legitimately admit-capable admin/owner — CANNOT silently overwrite it. So admit-AUTHORITY does NOT
        //     confer the ability to (re)set another member's DM key (which, in a non-interactive ECDH, would hand the
        //     re-admitter that member's DMs). A re-key, if ever a feature, would be a MEMBER-self-signed record (see
        //     ADR 0136), never a unilateral admin override.
        //   • SNAPSHOT-ORDER-INDEPENDENCE — "earliest" is decided by a DETERMINISTIC total order on the admission
        //     fields (IssuedAt, Nonce, PartyId, AdmittedByPublicKey, Signature) — the same discipline the revocation
        //     pass already uses — NOT by the order records happen to arrive or the order the relaxation reaches an
        //     admitter. To keep the CHOICE of binding independent of relaxation timing, each pass first collects ALL
        //     currently-admissible candidates for not-yet-bound parties, then binds each such party from its
        //     deterministically-EARLIEST candidate. (A naive in-loop `live[party]=` could otherwise let a LATER
        //     admission whose admitter happens to be reached first win the binding.)
        var pending = admissionList
            .Where(a => !a.Admission.IsGenesis && string.Equals(a.TeamId, genesis.TeamId, StringComparison.Ordinal))
            .ToList();
        // The deterministic total order over admissions — earliest-wins for first-write-wins; stable across snapshot
        // orderings. Identical comparator to the revocation pass below (IssuedAt → Nonce → … stable tiebreaks).
        IOrderedEnumerable<MemberAdmissionRecord> InDeterministicOrder(IEnumerable<MemberAdmissionRecord> xs) =>
            xs.OrderBy(a => orderTime?.Invoke(a.Admission.Signature, a.Admission.IssuedAt) ?? a.Admission.IssuedAt)
              .ThenBy(a => a.Admission.Nonce)
              .ThenBy(a => a.PartyId, StringComparer.Ordinal)
              .ThenBy(a => a.Admission.AdmittedByPublicKey, StringComparer.Ordinal)
              .ThenBy(a => a.Admission.Signature, StringComparer.Ordinal);
        bool grew;
        do
        {
            grew = false;
            // (a) Collect every admissible candidate this pass (admitter already validated + every integrity/authority
            //     gate passes), grouped by the party being admitted — WITHOUT mutating `live` mid-collection, so the
            //     candidate set is independent of intra-pass iteration order.
            var admissibleByParty = new Dictionary<string, List<MemberAdmissionRecord>>(StringComparer.Ordinal);
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                var a = pending[i];
                // FIRST-WRITE-WINS: a party already bound live is IMMUTABLE — drop any further admission of it
                // (this is what defeats the admit-capable re-admission overwrite: an admin/owner re-admitting an
                // existing participant with a substituted DM key is dropped here even though it would pass every
                // other gate).
                if (live.ContainsKey(a.PartyId))
                {
                    pending.RemoveAt(i);
                    continue;
                }
                // Re-verify the admission for its stamped admitter key. C5: VerifyAdmission reconstructs the signed
                // record with a.Admission.DmPublicKey, so a SUBSTITUTED DM key (a roster writer changed the signed DM
                // field away from what the admitter actually signed) makes the Ed25519 check FAIL → dropped here.
                if (!RosterSigning.VerifyAdmission(teamId, a.PartyId, a.PublicKey, a.Admission, verifier))
                {
                    pending.RemoveAt(i); // bad signature (incl. a substituted DM key) — never admittable
                    continue;
                }
                // C5 defence-in-depth — the carried byte[] DM key must match the signed DM key (consistency gate).
                if (!CarriedDmKeyMatchesSigned(a))
                {
                    pending.RemoveAt(i);
                    continue;
                }
                // C5-X defence-in-depth — the carried byte[] X-Wing key must match the signed X-Wing key. The
                // substitution attack #1489 caught (a 2nd same-party record carrying an attacker X-Wing key with a
                // VALID signature, because the X-Wing key was NOT in the signed bytes) is now defeated TWICE: the
                // signed-in X-Wing key makes a substituted carried key fail VerifyAdmission above (different canonical
                // bytes → bad signature → dropped), and this gate independently rejects any carried-vs-signed divergence.
                if (!CarriedXWingKeyMatchesSigned(a))
                {
                    pending.RemoveAt(i);
                    continue;
                }
                // The admitter must ALREADY be a validated member, hold members:admit, and its recorded key
                // must match the admission's stamped admitter key (the admitter is itself rooted to genesis).
                if (!live.TryGetValue(a.Admission.AdmittedByPartyId, out var admitter)) continue; // not yet
                if (!string.Equals(
                        admitter.PublicKey.ToBase64Url(), a.Admission.AdmittedByPublicKey, StringComparison.Ordinal))
                {
                    pending.RemoveAt(i); // admitter key drift — not the recorded admitter
                    continue;
                }
                if (!AuthorityOf(admitter.PartyId).Contains(Permission.MembersAdmit))
                {
                    pending.RemoveAt(i); // admitter holds no members:admit in the grant store — reject
                    continue;
                }
                // NO-ESCALATION: the admitted party's held authority must be a subset of the admitter's.
                if (!AuthorityOf(a.PartyId).IsSubsetOf(AuthorityOf(admitter.PartyId)))
                {
                    pending.RemoveAt(i);
                    continue;
                }
                // Admissible — stage it under its party (don't bind yet; we pick the earliest per party below).
                if (!admissibleByParty.TryGetValue(a.PartyId, out var bucket))
                {
                    bucket = new List<MemberAdmissionRecord>();
                    admissibleByParty[a.PartyId] = bucket;
                }
                bucket.Add(a);
                pending.RemoveAt(i);
            }
            // (b) Bind each newly-admissible party from its DETERMINISTICALLY-EARLIEST candidate (first-write-wins;
            //     the rest of that party's candidates are discarded — they cannot rebind an already-bound party).
            foreach (var (party, candidates) in admissibleByParty)
            {
                if (live.ContainsKey(party)) continue; // defensive — should not happen (party was not-yet-bound)
                var winner = InDeterministicOrder(candidates).First();
                // NO live permission set for a replicated member: PermissionsOf answers null, so every reader
                // falls through to the grant closure. An empty set would answer, and shadow the grants.
                live[party] = new MemberState(winner.PartyId, winner.PublicKey, null, winner.Admission);
                log[party] = new AdmissionEntry(winner.PartyId, winner.PublicKey, winner.Admission);
                grew = true;
            }
        } while (grew);
        // Whatever remains in `pending` is orphan (admitter never reached genesis) — DROPPED fail-closed.

        var rebuilt = new MemberRoster(teamId, live, log, genesis.PartyId);

        var refusedRevocations = new List<RosterRevocationRefusal>();

        // (4) Apply each VALID, authorized revocation — drop the target from LIVE state (chain untouched).
        // Process in IssuedAt order so a revoke-then-readmit (different nonce/time) converges deterministically.
        foreach (var rev in revocationList
                     .Where(r => string.Equals(r.TeamId, genesis.TeamId, StringComparison.Ordinal))
                     .OrderBy(r => orderTime?.Invoke(r.Signed.Signature, r.Signed.IssuedAt) ?? r.Signed.IssuedAt)
                     .ThenBy(r => r.Signed.Nonce).ThenBy(r => r.Signed.Signature, StringComparer.Ordinal))
        {
            if (!rebuilt.TryAuthorizeRevocation(rev, verifier, AuthorityOf)) continue; // forged/unauthorized → DROPPED
            if (!rebuilt._byParty.ContainsKey(rev.RevokedPartyId)) continue; // already gone / never a member
            try
            {
                // The SAME floor the local path runs — a revocation refused locally must be refused on replay,
                // or two nodes reach different rosters from one record set.
                rebuilt = rebuilt.Revoke(rev.Signed.RevokedByPartyId, rev.RevokedPartyId, AuthorityOf);
            }
            catch (RosterGuardException ex) when (ex.Code == NoBrickingFloorCode)
            {
                refusedRevocations.Add(new RosterRevocationRefusal(ex.Code, rev));
            }
        }

        rebuilt.RefusedRevocations = refusedRevocations.AsReadOnly();
        return rebuilt;
    }

    /// <summary>
    /// Validate a peer revocation RECORD (signature + the target it claims) against THIS roster. Used by
    /// <see cref="FromSyncedRecords"/>. The signature re-verifies for the stamped revoker key, and that key is a
    /// current member holding <c>members:revoke</c>. Fail-closed.
    /// </summary>
    private bool TryAuthorizeRevocation(
        MemberRevocationRecord record, IOperationVerifier verifier, Func<string, PermissionSet>? authority = null)
    {
        if (record is null) return false;
        // (a) Signature integrity: the stamped revoker key signed this (team, target) revocation.
        if (!RosterSigning.VerifyRevocation(_teamId, record.RevokedPartyId, record.Signed, verifier))
            return false;
        // (b) Authority: the revoker is a CURRENT member whose recorded key matches the stamped key AND holds
        //     members:revoke (from the grant store on the replicated path). A revocation signed by a
        //     non-member / non-admin / wrong key is rejected.
        if (!_byParty.TryGetValue(record.Signed.RevokedByPartyId, out var revoker)) return false;
        if (!string.Equals(revoker.PublicKey.ToBase64Url(), record.Signed.RevokedByPublicKey, StringComparison.Ordinal))
            return false;
        return (authority?.Invoke(revoker.PartyId) ?? Held(revoker)).Contains(Permission.MembersRevoke);
    }

    /// <summary>An empty / invalid roster — the fail-closed result when synced records have no trustworthy
    /// genesis root. Validates to genesis as false; trusts no one. (A node that cannot rebuild a trustworthy
    /// roster keeps its own local genesis-seeded roster rather than adopting this — see the projection.)</summary>
    public static MemberRoster Empty() => new(
        Guid.Empty,
        new Dictionary<string, MemberState>(StringComparer.Ordinal),
        new Dictionary<string, AdmissionEntry>(StringComparer.Ordinal),
        string.Empty);
}

/// <summary>
/// Where the REPLICATED path reads a party's authority now that no permission set rides the wire (ticket 293
/// slice 3b2). The host implements it over the local grant store and hands it to
/// <see cref="MemberRoster.FromSyncedRecords"/>; a caller that supplies none gets the fail-closed floor, where
/// only the genesis chain root holds authority.
/// </summary>
public interface IRosterAuthority
{
    /// <summary>The permission set the local grant store holds for a party within a team.</summary>
    /// <param name="teamId">The team (tenant) the roster belongs to.</param>
    /// <param name="partyId">The party whose authority is being read.</param>
    PermissionSet PermissionsFor(string teamId, string partyId);
}

/// <summary>
/// A syncable ADMISSION record — one entry of the roster-sync doctype's append-log. Carries everything a peer
/// needs to independently re-validate and rebuild the chain: the team, the admitted party and key,
/// and the signed <see cref="AdmissionSignature"/>. Emitted by <see cref="MemberRoster.EnumerateAdmissions"/>;
/// consumed by <see cref="MemberRoster.FromSyncedRecords"/>.
/// </summary>
/// <param name="TeamId">The team this admission is into (string form of the Guid).</param>
/// <param name="PartyId">The admitted member's party id.</param>
/// <param name="PublicKey">The admitted member's Ed25519 public key (the party→key binding).</param>
/// <param name="Admission">The signature that roots this admission in the genesis chain.</param>
/// <param name="TransportPublicKey">
/// The admitted member's TEAM-SCOPED transport public key (HKDF(member-root, teamId) public half) — the key it
/// presents in the sync HELLO and that <c>MemberSetTrustPolicy</c> checks (INFO-2; the ≥3-node mesh follow-on).
/// <b>Nullable + additive.</b> The PRINCIPAL key (<see cref="PublicKey"/>) is what the SIGNED chain binds for
/// forge-proof attribution; the transport key is the routing/handshake datum the trust gate consumes. Carrying it
/// on the synced admission record lets every converged member derive the FULL transport-trust set from the
/// converged roster (roster-derived trust), so peers admitted at different times trust each other directly
/// (B↔C) instead of only via the admitting hub. It is <b>UNSIGNED-v1, validated by ASSOCIATION</b> (honored only
/// for a party the signed genesis-rooted chain already validated into the roster — a forged transport key for a
/// non-member is never in the rebuilt roster, so it is never trusted; the same model the reviewed
/// <c>WireEnrollment.MemberTransportKeys</c> already uses). It travels OUTSIDE the signed
/// <see cref="AdmissionSignature"/> payload, so old signatures still verify and old records (null transport key)
/// keep working — they contribute nothing to the transport map (the own-subkey floor + the live one-time
/// enrollment set still cover the hub pair). Public-key-only: leaks nothing (it is as public as the principal
/// key). Defaults <c>null</c> — <see cref="MemberRoster"/> stays transport-agnostic; the host's projection layer
/// (which owns the transport keys) stamps it when building the synced record.
/// </param>
/// <param name="DmPublicKey">
/// The admitted member's TEAM-SCOPED DM-encryption public key — the X25519 PUBLIC half of the member's
/// per-node-secret DM keypair (HKDF(member-root, teamId) over the DM domain), the key a DM peer runs the X25519
/// ECDH against to derive the per-conversation seal key (C5; DM content confidentiality). It rides the synced
/// roster record on the SAME channel as <see cref="TransportPublicKey"/> (the #1310 pattern), so every converged
/// member harvests every other member's DM public key from the converged roster and a 1:1 DM "just works" with no
/// key-exchange handshake. <b>The PRIVATE half NEVER appears here</b> — it derives from the member's OWN root seed
/// on the member's own node and never leaves it, so a non-participant (even a team member holding this public key)
/// CANNOT derive the per-conversation key (the leak guarantee, DR-5). <b>Nullable + additive</b> with the SAME
/// back-compat + UNSIGNED-by-association posture as <see cref="TransportPublicKey"/>: OUTSIDE the signed admission
/// payload (old signatures still verify), honored only for a party the SIGNED chain validated into the roster,
/// public-key-only (leaks nothing). Defaults <c>null</c>; the host projection layer (which owns the seed-derived
/// DM keypair) stamps it when building the synced record.
/// </param>
/// <param name="XWingPublicKey">
/// The admitted member's TEAM-SCOPED <b>X-Wing</b> (X25519 + ML-KEM-768) PUBLIC key — the 1216-byte
/// <c>pk_M ‖ pk_X</c> encapsulation key (HKDF(member-root, teamId) over the X-Wing domain — see
/// <c>IXWingSubkeyDerivation</c>), the key a SENDER encapsulates a tenant DEK / role key to when boxing a
/// suite-#3 (<c>KemSuite.XWingX25519MlKem768_v1</c>) wrap (PQC Phase 2 / BL-01 increment 2c-iii-b — the
/// write-side enabler; ADR 0004 Amendment 2). It rides the synced roster record on the SAME channel as
/// <see cref="TransportPublicKey"/> / <see cref="DmPublicKey"/>, so every converged member harvests every other
/// member's X-Wing public key from the converged roster — a sender then knows which recipients are
/// X-Wing-CAPABLE (have published an X-Wing key) and can box suite #3 for them (suite #1 otherwise — safe
/// degrade). <b>The PRIVATE half (the 32-byte X-Wing decapsulation seed) NEVER appears here</b> — it derives
/// from the member's OWN root seed on the member's own node and never leaves it.
/// <para>
/// <b>C5-X — the X-Wing key is SIGNED into the admission (a confidentiality key, like the DM key), NOT carried
/// unsigned-by-association like the transport key</b> (the X-Wing key-substitution DEK-leak fix; sec-eng
/// deep-review of PR #1489). The earlier reasoning — "substitution is a DoS because the wrap's outer Ed25519 +
/// the recipient opening with its own seed backstop it" — was FALSE. X-Wing
/// (<c>draft-connolly-cfrg-xwing-kem</c>) is an UNAUTHENTICATED KEM with NO recipient-identity binding: if a
/// sender encapsulates to a SUBSTITUTED key, the holder of the matching private seed (the ATTACKER who supplied
/// it) decapsulates and recovers the DEK — the legitimate recipient is simply bypassed, not the one who fails to
/// open. The outer Ed25519 signs over WHATEVER recipient key the harvest supplied, so it faithfully attests the
/// poisoned box and cannot cross-check the (party → X-Wing-key) binding (that binding lived ONLY in the unsigned
/// harvest). So — exactly as C5 did for the DM key — the X-Wing key is now bound into the FORGE-PROOF admission
/// (<see cref="AdmissionSignature.XWingPublicKey"/>), the wire field is DERIVED from that signed value
/// (<c>RosterRecordCrdtState.FromAdmission</c>), and the harvest reads it off the CHAIN-VALIDATED rebuilt roster
/// (<see cref="MemberRoster.XWingPublicKeyOf"/>) — a substituted X-Wing key produces a record whose signature no
/// longer validates and is DROPPED on rebuild, so it never reaches a live member.
/// </para>
/// <b>Nullable + additive</b> (back-compat): a legacy admission with no signed X-Wing key emits null → the member
/// is X-Wing-incapable → safe-degrades to suite #1. Defaults <c>null</c>; the host projection layer (which owns the
/// seed-derived X-Wing keypair) supplies it to the admit/genesis call so it is SIGNED in, and
/// <see cref="MemberRoster.EnumerateAdmissions"/> emits the SIGNED value onto the wire.
/// </param>
public sealed record MemberAdmissionRecord(
    string TeamId,
    string PartyId,
    PrincipalId PublicKey,
    AdmissionSignature Admission,
    byte[]? TransportPublicKey = null,
    byte[]? DmPublicKey = null,
    byte[]? XWingPublicKey = null);

/// <summary>
/// A syncable REVOCATION record — one entry of the roster-sync doctype's append-log. Carries the team, the
/// revoked party, and the signed <see cref="RevocationSignature"/>. Produced by
/// <see cref="MemberRoster.SignRevoke"/>; consumed by <see cref="MemberRoster.FromSyncedRecords"/>.
/// </summary>
/// <param name="TeamId">The team this revocation is within (string form of the Guid).</param>
/// <param name="RevokedPartyId">The party being revoked from live state.</param>
/// <param name="Signed">The signature that authenticates the revoker + binds the (team, revoked-party).</param>
public sealed record MemberRevocationRecord(
    string TeamId,
    string RevokedPartyId,
    RevocationSignature Signed);

/// <summary>
/// Thrown when a roster mutation violates a FIXED guard (no-escalation, no-bricking floor, or the
/// admitter/revoker authority check). These are structural — the wrong thing is impossible, not discouraged.
/// </summary>
public sealed class RosterGuardException : Exception
{
    /// <summary>Construct with the guard-violation message.</summary>
    public RosterGuardException(string message, string? code = null) : base(message) { Code = code; }

    /// <summary>Stable refusal code where the guard has a classified outcome.</summary>
    public string? Code { get; }
}

/// <summary>A floor refusal produced while folding a verified signed revocation.</summary>
/// <param name="Code">The guard's stable refusal code.</param>
/// <param name="Revocation">The signed removal that was refused; its target remains live.</param>
public sealed record RosterRevocationRefusal(string Code, MemberRevocationRecord Revocation);
