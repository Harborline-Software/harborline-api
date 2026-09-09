using System.Collections.Generic;
using System.Linq;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Data.Roster;

/// <summary>The kind of a synced roster record — an admission (admit) or a revocation.</summary>
public enum RosterRecordKind
{
    /// <summary>A signed admission (the genesis self-admission or an admin admitting a member).</summary>
    Admission = 0,
    /// <summary>A signed revocation (an admin dropping a member from live state).</summary>
    Revocation = 1,
}

/// <summary>
/// The synced projection of one TRUST-ROSTER record — the value pushed, in produce order, into the roster
/// CRDT <b>list</b> (the roster-sync doctype; the foundational production-wiring gap #1). It mirrors
/// <c>MessageCrdtState</c> (the comms append-log doctype) but the payload is a signed admission/revocation
/// rather than a message. A node receiving these records reconstructs + VALIDATES the team roster from them
/// (<see cref="MemberRoster.FromSyncedRecords"/>) — never trusting the sender's say-so.
/// </summary>
/// <remarks>
/// <para>
/// <b>Flat serializable snapshot.</b> The CRDT list stores each item as its System.Text.Json form
/// (<c>ICrdtList.Push&lt;T&gt;</c> serializes; <c>Get&lt;T&gt;</c> deserializes), so this is deliberately a
/// flat record of strings, not a domain aggregate. The cryptographic payload (the <c>AdmissionSignature</c> /
/// <c>RevocationSignature</c>) is the SAME byte-stable form the foundation signs + verifies, so a peer
/// independently re-validates each record — the signature, not the wire, is the trust anchor.
/// </para>
/// <para>
/// <b>Stable record identity.</b> <see cref="RecordId"/> is a content-derived id (kind + party + nonce) so the
/// list reconcile is idempotent (the same record applied twice is skipped) — the same discipline
/// <c>MessageCrdtState.MessageId</c> gives the comms list.
/// </para>
/// </remarks>
/// <param name="RecordId">Stable per-record id — the append's identity across replicas.</param>
/// <param name="Kind">Admission or revocation.</param>
/// <param name="TeamId">The team this record is into/within (string form of the Guid).</param>
/// <param name="PartyId">The admitted (admission) or revoked (revocation) party id.</param>
/// <param name="PublicKeyB64Url">base64url of the admitted member's public key (admission only; empty for revocation).</param>
/// <param name="Permissions">The admitted member's permission set as strings (admission only; empty for revocation).</param>
/// <param name="AdmittedByPublicKey">base64url of the admitter/revoker public key (the signer).</param>
/// <param name="AdmittedByPartyId">The admitter/revoker party id.</param>
/// <param name="IssuedAtIso">Round-trippable ISO-8601 issuance instant (UTC).</param>
/// <param name="NonceGuid">The signing nonce (string form).</param>
/// <param name="SignatureB64Url">base64url Ed25519 signature over the canonical signable form.</param>
/// <param name="IsGenesis">True iff this is the founding self-admission (admission only).</param>
/// <param name="TransportPublicKeyB64Url">
/// base64url of the admitted member's TEAM-SCOPED transport public key — the key it presents in the sync HELLO,
/// that <c>MemberSetTrustPolicy</c> checks (INFO-2; the ≥3-node mesh follow-on). Carrying it on the synced
/// admission record makes the transport-trust set ROSTER-DERIVED: every converged member harvests every other
/// member's transport key from the converged roster, so two peers admitted at different times trust each other
/// directly (B↔C) — not only via the admitting hub. <b>Additive + back-compat:</b> defaults EMPTY; an old node
/// deserializing a new record ignores the unknown field, a new node reading an old record (empty) contributes
/// nothing to the transport map (the own-subkey floor + the live one-time enrollment set still cover the hub
/// pair). <b>UNSIGNED-v1, validated by association:</b> it is OUTSIDE the signed admission payload (so old
/// signatures still verify) and is honored ONLY for a party the signed chain already validated into the rebuilt
/// roster — a forged transport key for a non-member is never in the rebuilt roster, so never trusted; a member's
/// principal binding is the SIGNED trust anchor and is untouched. Public-key-only (leaks nothing). Empty for a
/// revocation record (no key).
/// </param>
/// <param name="DmPublicKeyB64Url">
/// base64url of the admitted member's TEAM-SCOPED DM-encryption public key — the X25519 PUBLIC half of the
/// member's per-node-secret DM keypair (C5; DM content confidentiality). It rides on the SAME synced-record
/// channel as <see cref="TransportPublicKeyB64Url"/> (the #1310 pattern): every converged member harvests every
/// other member's DM public key from the converged roster, so the C5 roster-bound DM key resolver can run the
/// X25519 ECDH against a peer's DM public key with no key-exchange handshake. <b>Additive + back-compat, exactly
/// like the transport key:</b> defaults EMPTY; an old node ignores the unknown field, a new node reading an old
/// record (empty) simply cannot seal/unseal that party's DMs until its record re-emits with the key. <b>The
/// PRIVATE half is NEVER on the wire</b> — it derives from the member's OWN root seed on its own node, so a
/// non-participant holding this public key still CANNOT derive the per-conversation key (the leak guarantee). It
/// is <b>UNSIGNED-v1, validated by ASSOCIATION</b> (honored only for a party the signed genesis-rooted chain
/// validated into the rebuilt roster — a forged DM key for a non-member is never trusted), OUTSIDE the signed
/// admission payload (old signatures still verify), public-key-only (leaks nothing). Empty for a revocation
/// record (no key).
/// </param>
/// <param name="XWingPublicKeyB64Url">
/// base64url of the admitted member's TEAM-SCOPED <b>X-Wing</b> (X25519 + ML-KEM-768) public key — the
/// 1216-byte <c>pk_M ‖ pk_X</c> encapsulation key (PQC Phase 2 / BL-01 increment 2c-iii-b; the suite-#3
/// write-side enabler, ADR 0004 Amendment 2). It rides on the SAME synced-record channel as
/// <see cref="TransportPublicKeyB64Url"/> / <see cref="DmPublicKeyB64Url"/>: every converged member harvests
/// every other member's X-Wing public key from the converged roster, so a SENDER knows which recipients are
/// X-Wing-CAPABLE and can box a suite-#3 (<c>KemSuite.XWingX25519MlKem768_v1</c>) tenant-DEK / role-key wrap
/// for them — a recipient with no published X-Wing key keeps getting suite #1 (safe degrade). <b>Additive +
/// back-compat, exactly like the transport key:</b> defaults EMPTY; an old node ignores the unknown field, a
/// new node reading an old record (empty) simply treats that party as not-yet-X-Wing-capable (stays suite #1)
/// until its record re-emits with the key. Unlike the 32-byte <c>PrincipalId</c>-shaped transport/DM keys, this
/// is a 1216-byte key, so it is carried as a RAW base64url of the bytes (length-validated on reconstruction
/// against the X-Wing public-key length, not via <c>PrincipalId</c>). <b>The PRIVATE half (the 32-byte X-Wing
/// decapsulation seed) is NEVER on the wire</b> — it derives from the member's OWN root seed on its own node.
/// <b>C5-X — the X-Wing key is SIGNED into the admission (NOT unsigned-by-association like the transport key)</b>
/// (the X-Wing key-substitution DEK-leak fix; sec-eng deep-review of PR #1489). The X-Wing key is a
/// CONFIDENTIALITY key like the DM key: X-Wing is an UNAUTHENTICATED KEM with no recipient-identity binding, so a
/// substituted published key hands the boxed DEK to whoever holds the matching private seed (the substituter) —
/// the same leak class C5 had to sign the DM key against (the earlier "outer-Ed25519 + own-seed backstop ⇒ DoS"
/// reasoning was FALSE: the outer sig faithfully attests the poisoned box; the attacker, not the recipient,
/// opens). So this wire field is DERIVED from the SIGNED admission value (<see cref="ToAdmissionOrNull"/> puts it
/// into <c>AdmissionSignature.XWingPublicKey</c>, which <c>VerifyAdmission</c> reconstructs the signed record
/// from): a substituted X-Wing field makes the admission signature FAIL on rebuild → the record is DROPPED → its
/// party is never live → never harvested. The harvest reads the chain-validated <c>MemberRoster.XWingPublicKeyOf</c>,
/// not this raw field. Empty for a legacy admission (no signed X-Wing key → X-Wing-incapable → suite #1) or a
/// revocation record (no key).
/// </param>
/// <param name="AdmittedViaTokenId">
/// #3167 R1.2 — the single-use pairing/admission token id the admission was signed FROM, carried so it survives
/// the sync round-trip. It is DERIVED from the SIGNED value (<c>a.AdmittedViaTokenId</c>) and put back into
/// <c>AdmissionSignature.AdmittedViaTokenId</c> on rebuild, so <c>VerifyAdmission</c> reconstructs the signed
/// record from it — a writer who STRIPS or alters it makes the admission signature FAIL on rebuild → the record
/// is DROPPED. Additive + back-compat: defaults EMPTY (a non-pairing admission signs no token id; the empty
/// string is part of the signed bytes and round-trips stably). Empty for a revocation record.
/// </param>
/// <param name="MintingSessionEvidence">
/// #3167 R1.2 — the opaque mint-time SessionCorrelationId the admission was signed under (audit provenance).
/// Carried + reconstructed exactly like <see cref="AdmittedViaTokenId"/>: derived from the signed value, put back
/// into <c>AdmissionSignature.MintingSessionEvidence</c> on rebuild so a stripped/altered value drops the record.
/// Additive + back-compat, defaults EMPTY. Empty for a revocation record.
/// </param>
public sealed record RosterRecordCrdtState(
    string RecordId,
    RosterRecordKind Kind,
    string TeamId,
    string PartyId,
    string PublicKeyB64Url,
    IReadOnlyList<string> Permissions,
    string AdmittedByPublicKey,
    string AdmittedByPartyId,
    string IssuedAtIso,
    string NonceGuid,
    string SignatureB64Url,
    bool IsGenesis,
    string TransportPublicKeyB64Url = "",
    string DmPublicKeyB64Url = "",
    string XWingPublicKeyB64Url = "",
    string AdmittedViaTokenId = "",
    string MintingSessionEvidence = "",
    int WireFormatVersion = 0,
    string ReceivedAtIso = "",
    string ReceivedByPartyId = "",
    string ReceivedByPublicKey = "",
    string ReceiveAttestationSignatureB64Url = "")
{
    /// <summary>
    /// The X-Wing PUBLIC key length in bytes (1216 = ML-KEM-768 pk 1184 ‖ X25519 pk 32) — the length a carried
    /// <see cref="XWingPublicKeyB64Url"/> must decode to, or it is treated as ABSENT (fail-closed; never a
    /// malformed wire record). A LOCAL copy of <c>IXWingKem.PublicKeyLength</c> because this app-tier record must
    /// not take a kernel-security dependency just for a constant (foundation-identity-atlas is kernel-free; this
    /// host record is the one place both meet, and the value is part of the standardized X-Wing wire contract).
    /// </summary>
    public const int XWingPublicKeyLength = 1216;

    /// <summary>
    /// Build the synced wire form of a foundation admission record. The optional <paramref name="transportKey"/>
    /// (raw 32-byte team-scoped transport pubkey) is stamped onto the synced record so it RIDES to peers (INFO-2;
    /// the ≥3-node mesh follow-on) — the host's projection layer, which owns the transport keys, supplies it. When
    /// null/empty (a legacy publish, or a record built by a transport-agnostic caller) the wire field is empty and
    /// the record contributes nothing to the rebuilt transport map (additive + back-compat). If the
    /// <see cref="MemberAdmissionRecord.TransportPublicKey"/> is already populated it is used as the default, so a
    /// caller that stamped the record's own field need not pass it twice.
    /// </summary>
    public static RosterRecordCrdtState FromAdmission(
        MemberAdmissionRecord record, byte[]? transportKey = null, byte[]? dmKey = null,
        byte[]? xwingKey = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        var a = record.Admission;
        var carried = transportKey ?? record.TransportPublicKey;
        // C5-X (X-Wing key-substitution DEK-leak fix; sec-eng deep-review of PR #1489) — the X-Wing key is now SIGNED
        // into the admission (a confidentiality key, like the C5 DM key — NOT the unsigned transport-key model). So the
        // wire X-Wing field is DERIVED from the SIGNED value (a.XWingPublicKey, a base64url string) when the admission
        // signed one, NOT an external/unsigned stamp — deriving it from the signed value keeps
        // RosterRecordCrdtState.XWingPublicKeyB64Url == the signed key, so the rebuild's consistency gate
        // (MemberRoster.CarriedXWingKeyMatchesSigned) is satisfied and a roster writer cannot diverge the carried key
        // from the signed one. A caller's external xwingKey stamp / record.XWingPublicKey is used ONLY as a back-compat
        // fallback for a record produced before the X-Wing key was signed in (legacy/transitional).
        var signedXWingB64 = a.XWingPublicKey ?? string.Empty;
        var carriedXWing = signedXWingB64.Length > 0 ? null : (xwingKey ?? record.XWingPublicKey);
        // C5 (DM key-substitution fix) — the wire DM field is the key the admission SIGNED (a.DmPublicKey), NOT an
        // external stamp. The signed value is the forge-proof source of truth; deriving the wire field from it keeps
        // RosterRecordCrdtState.DmPublicKeyB64Url == the signed key, so the rebuild's consistency gate
        // (MemberRoster.CarriedDmKeyMatchesSigned) is satisfied and a roster writer cannot diverge the carried key
        // from the signed one. A caller's external dmKey stamp / record.DmPublicKey is used ONLY as a back-compat
        // fallback for a record produced before the DM key was signed in (legacy/transitional). The string-form
        // signed value wins when present.
        var signedDmB64 = a.DmPublicKey ?? string.Empty;
        var carriedDm = signedDmB64.Length > 0 ? null : (dmKey ?? record.DmPublicKey);
        return new RosterRecordCrdtState(
            RecordId: $"admission:{record.PartyId}:{a.Nonce:D}",
            Kind: RosterRecordKind.Admission,
            TeamId: record.TeamId,
            PartyId: record.PartyId,
            PublicKeyB64Url: record.PublicKey.ToBase64Url(),
            Permissions: record.Permissions.Permissions.ToArray(),
            AdmittedByPublicKey: a.AdmittedByPublicKey,
            AdmittedByPartyId: a.AdmittedByPartyId,
            IssuedAtIso: a.IssuedAt.ToString("O"),
            NonceGuid: a.Nonce.ToString("D"),
            SignatureB64Url: a.Signature,
            IsGenesis: a.IsGenesis,
            // The transport key is a 32-byte Ed25519 public key (PrincipalId-shaped). Encode via PrincipalId so
            // a non-32-byte value fails fast here rather than producing a malformed wire record. Empty when absent.
            TransportPublicKeyB64Url: carried is { Length: PrincipalId.LengthInBytes }
                ? PrincipalId.FromBytes(carried).ToBase64Url()
                : string.Empty,
            // C5 — the DM public key on the wire is the SIGNED value (a.DmPublicKey) when the admission signed one;
            // it is already a validated base64url 32-byte key, so it rides verbatim (forge-proof). Otherwise (a legacy
            // admission with no signed DM key) fall back to the carried byte[] for transitional back-compat.
            DmPublicKeyB64Url: signedDmB64.Length > 0
                ? signedDmB64
                : carriedDm is { Length: PrincipalId.LengthInBytes }
                    ? PrincipalId.FromBytes(carriedDm).ToBase64Url()
                    : string.Empty,
            // C5-X — the X-Wing public key on the wire is the SIGNED value (a.XWingPublicKey) when the admission signed
            // one; it is already a validated base64url 1216-byte key, so it rides verbatim (forge-proof). Otherwise (a
            // legacy admission with no signed X-Wing key) fall back to the carried byte[] for transitional back-compat.
            // The X-Wing key is a 1216-byte raw key (not PrincipalId-shaped); a carried fallback is encoded ONLY when
            // exactly XWingPublicKeyLength — any other length is treated as ABSENT (empty wire field) rather than
            // producing a malformed record a peer must drop. Empty when no X-Wing key (recipient stays suite #1).
            XWingPublicKeyB64Url: signedXWingB64.Length > 0
                ? signedXWingB64
                : carriedXWing is { Length: XWingPublicKeyLength }
                    ? EncodeRawBase64Url(carriedXWing)
                    : string.Empty,
            // #3167 R1.2 — the provenance fields ride verbatim from the SIGNED admission (plain strings, not keys),
            // so a pairing admission's token id + mint-session evidence survive the sync round-trip and the peer's
            // rebuild reconstructs the exact signed record. Empty for a non-pairing admission (signed empty).
            AdmittedViaTokenId: a.AdmittedViaTokenId ?? string.Empty,
            MintingSessionEvidence: a.MintingSessionEvidence ?? string.Empty);
    }

    /// <summary>Build the synced wire form of a foundation revocation record.</summary>
    public static RosterRecordCrdtState FromRevocation(MemberRevocationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var r = record.Signed;
        return new RosterRecordCrdtState(
            RecordId: $"revocation:{record.RevokedPartyId}:{r.Nonce:D}",
            Kind: RosterRecordKind.Revocation,
            TeamId: record.TeamId,
            PartyId: record.RevokedPartyId,
            PublicKeyB64Url: string.Empty,
            Permissions: System.Array.Empty<string>(),
            AdmittedByPublicKey: r.RevokedByPublicKey,
            AdmittedByPartyId: r.RevokedByPartyId,
            IssuedAtIso: r.IssuedAt.ToString("O"),
            NonceGuid: r.Nonce.ToString("D"),
            SignatureB64Url: r.Signature,
            IsGenesis: false);
    }

    /// <summary>Attach this node's canonical receive evidence before the record enters the synced document.</summary>
    public RosterRecordCrdtState AttestReceipt(
        IOperationSigner signer, string nodePartyId, DateTimeOffset receivedAt)
    {
        if (!Guid.TryParse(NonceGuid, out var nonce))
            throw new ArgumentException("A roster record needs a valid nonce before receipt attestation.", nameof(NonceGuid));
        var attestation = RosterReceiveAttestationSigning.Sign(signer, nodePartyId, RecordId, receivedAt, nonce);
        return this with
        {
            WireFormatVersion = attestation.FormatVersion,
            ReceivedAtIso = attestation.ReceivedAt.ToString("O"),
            ReceivedByPartyId = attestation.NodePartyId,
            ReceivedByPublicKey = attestation.NodePublicKey,
            ReceiveAttestationSignatureB64Url = attestation.Signature,
        };
    }

    /// <summary>Reconstruct the signed receive evidence, or null for an old/malformed wire shape.</summary>
    public RosterReceiveAttestation? ReceiveAttestationOrNull() =>
        WireFormatVersion == RosterWireFormat.CurrentVersion && DateTimeOffset.TryParse(ReceivedAtIso, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var receivedAt)
            ? new(WireFormatVersion, ReceivedByPartyId, ReceivedByPublicKey, receivedAt,
                ReceiveAttestationSignatureB64Url) : null;

    /// <summary>
    /// Reconstruct the foundation admission record from this wire form, or null when this is not a
    /// well-formed admission (kind mismatch / malformed key / nonce). Fail-closed: a malformed record yields
    /// null and is dropped by the rebuild, never an exception.
    /// </summary>
    public MemberAdmissionRecord? ToAdmissionOrNull()
    {
        if (Kind != RosterRecordKind.Admission) return null;
        try
        {
            var admission = new AdmissionSignature(
                AdmittedByPublicKey: AdmittedByPublicKey,
                AdmittedByPartyId: AdmittedByPartyId,
                IssuedAt: System.DateTimeOffset.Parse(
                    IssuedAtIso, null, System.Globalization.DateTimeStyles.RoundtripKind),
                Nonce: System.Guid.Parse(NonceGuid),
                Signature: SignatureB64Url,
                IsGenesis: IsGenesis,
                // C5 (DM key-substitution fix) — the wire DM field IS the signed DM key, so put it into the
                // AdmissionSignature.DmPublicKey that MemberRoster.FromSyncedRecords → RosterSigning.VerifyAdmission
                // reconstructs the SIGNED record from. A substituted DmPublicKeyB64Url therefore reconstructs a record
                // whose canonical bytes differ from what the admitter signed → the signature check FAILS → dropped.
                DmPublicKey: DmPublicKeyB64Url ?? string.Empty,
                // C5-X (X-Wing key-substitution DEK-leak fix; #1489) — the wire X-Wing field IS the signed X-Wing key,
                // so put it into the AdmissionSignature.XWingPublicKey that VerifyAdmission reconstructs the SIGNED
                // record from. A substituted XWingPublicKeyB64Url reconstructs a record whose canonical bytes differ
                // from what the admitter signed → the signature check FAILS → the whole record is DROPPED on rebuild.
                // THIS is what closes the confidentiality leak (a 2nd same-party record carrying an attacker X-Wing key
                // can no longer carry a valid signature, because the X-Wing key is now IN the signed bytes).
                XWingPublicKey: XWingPublicKeyB64Url ?? string.Empty,
                // #3167 R1.2 — put the carried provenance strings back into the AdmissionSignature the rebuild's
                // VerifyAdmission reconstructs the SIGNED record from. A stripped/altered token id or evidence
                // reconstructs canonical bytes that differ from what the admitter signed → the signature FAILS →
                // the whole record is DROPPED on rebuild (this is what makes "an admission whose signature does not
                // bind the token identity is invalid on this path" hold across the sync channel too).
                AdmittedViaTokenId: AdmittedViaTokenId ?? string.Empty,
                MintingSessionEvidence: MintingSessionEvidence ?? string.Empty,
                // The carried set reconstructs the signed bytes, exactly as the two carried keys do.
                Permissions: Permissions ?? System.Array.Empty<string>());
            // INFO-2: reconstruct the carried team-scoped transport pubkey (empty/legacy → null; a malformed key
            // throws below and drops the WHOLE record fail-closed — the same discipline as the principal key).
            var transportKey = string.IsNullOrEmpty(TransportPublicKeyB64Url)
                ? null
                : PrincipalId.FromBase64Url(TransportPublicKeyB64Url).AsSpan().ToArray();
            // C5: reconstruct the carried team-scoped DM pubkey (same fail-closed discipline as the transport key).
            // It is derived from the SAME wire field as the signed AdmissionSignature.DmPublicKey above, so the two
            // are consistent by construction (the rebuild's CarriedDmKeyMatchesSigned gate confirms it).
            var dmKey = string.IsNullOrEmpty(DmPublicKeyB64Url)
                ? null
                : PrincipalId.FromBase64Url(DmPublicKeyB64Url).AsSpan().ToArray();
            // 2c-iii-b: reconstruct the carried team-scoped X-Wing pubkey (the 1216-byte raw key). Empty/legacy →
            // null (the party is not X-Wing-capable; a sender boxes it suite #1). A present-but-wrong-length or
            // non-base64url value DROPS the WHOLE record fail-closed (null) — the same all-or-nothing discipline as
            // the transport/principal keys: DecodeRawXWingKeyOrThrow throws FormatException, caught below.
            var xwingKey = string.IsNullOrEmpty(XWingPublicKeyB64Url)
                ? null
                : DecodeRawXWingKeyOrThrow(XWingPublicKeyB64Url);
            return new MemberAdmissionRecord(
                TeamId,
                PartyId,
                PrincipalId.FromBase64Url(PublicKeyB64Url),
                PermissionSet.From(Permissions ?? System.Array.Empty<string>()),
                admission,
                transportKey,
                dmKey,
                xwingKey);
        }
        catch (System.FormatException) { return null; }
        catch (System.ArgumentException) { return null; }
    }

    /// <summary>
    /// Reconstruct the foundation revocation record from this wire form, or null when this is not a
    /// well-formed revocation. Fail-closed (see <see cref="ToAdmissionOrNull"/>).
    /// </summary>
    public MemberRevocationRecord? ToRevocationOrNull()
    {
        if (Kind != RosterRecordKind.Revocation) return null;
        try
        {
            var signed = new RevocationSignature(
                RevokedByPublicKey: AdmittedByPublicKey,
                RevokedByPartyId: AdmittedByPartyId,
                IssuedAt: System.DateTimeOffset.Parse(
                    IssuedAtIso, null, System.Globalization.DateTimeStyles.RoundtripKind),
                Nonce: System.Guid.Parse(NonceGuid),
                Signature: SignatureB64Url);
            return new MemberRevocationRecord(TeamId, PartyId, signed);
        }
        catch (System.FormatException) { return null; }
        catch (System.ArgumentException) { return null; }
    }

    // ── X-Wing raw-key codec ────────────────────────────────────────────────────────────────────────
    // The transport/DM keys are 32-byte PrincipalId-shaped and use PrincipalId.To/FromBase64Url; the X-Wing key is
    // a 1216-byte raw key with no PrincipalId wrapper, so it needs a plain raw-bytes ↔ base64url(no padding) codec.

    /// <summary>base64url (no padding) encode of a raw byte span — used ONLY for the 1216-byte X-Wing key.</summary>
    private static string EncodeRawBase64Url(ReadOnlySpan<byte> bytes)
    {
        var b64 = System.Convert.ToBase64String(bytes);
        return b64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Decode a carried X-Wing key from its base64url wire form, validating it is EXACTLY
    /// <see cref="XWingPublicKeyLength"/> bytes. Throws <see cref="System.FormatException"/> on a non-base64url or
    /// wrong-length value so <see cref="ToAdmissionOrNull"/> drops the WHOLE record fail-closed (never honoring a
    /// malformed X-Wing key, never throwing past the rebuild) — the same all-or-nothing posture the transport key
    /// gets from <c>PrincipalId.FromBase64Url</c>.
    /// </summary>
    private static byte[] DecodeRawXWingKeyOrThrow(string b64Url)
    {
        var padded = b64Url.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
            case 1: throw new System.FormatException("Malformed base64url X-Wing key (bad length).");
        }
        var bytes = System.Convert.FromBase64String(padded); // throws FormatException on non-base64.
        if (bytes.Length != XWingPublicKeyLength)
        {
            throw new System.FormatException(
                $"Carried X-Wing key is {bytes.Length} bytes (expected {XWingPublicKeyLength}) — fail-closed.");
        }
        return bytes;
    }
}
