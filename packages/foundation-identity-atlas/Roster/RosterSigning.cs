using System;
using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.Foundation.IdentityAtlas;

/// <summary>
/// The single source of truth for how an admission is SIGNED and VERIFIED — the genesis-rooted trust roster's
/// signing discipline (enrollment Phase A). Reuses the proven <see cref="IOperationSigner"/> /
/// <see cref="IOperationVerifier"/> canonical-JSON path (the same one comms attribution uses), so admission
/// signatures are byte-stable + cross-language compatible by construction.
/// </summary>
/// <remarks>
/// <para>
/// The signature covers the canonical-JSON signable envelope over an <see cref="AdmissionRecord"/> = the
/// versioned identity, key and provenance fields. Because
/// the admitted member's (party, key) binding is INSIDE the signed payload, no one can enroll a key under a
/// party's name without an in-roster admitter signing that exact pair — which is what makes the party→pubkey map
/// forge-proof (closing #1277 B1).
/// </para>
/// <para>
/// <b>C5 — the DM public key is bound INTO the signed envelope too</b> (the DM key-substitution fix; sec-eng
/// deep-review of PR #1326). The DM X25519 public key is used in a NON-INTERACTIVE ECDH with NO proof-of-possession
/// at use time, so substituting it directly leaks the per-conversation key — the transport key's "unsigned because
/// PoP-backstopped" posture does NOT carry over. Signing the (party → DM-pubkey) pair into the admission makes that
/// binding forge-proof exactly like the principal key: a roster writer who substitutes a peer's DM key produces a
/// record whose signature no longer validates and is dropped on rebuild.
/// </para>
/// </remarks>
public static class RosterSigning
{
    /// <summary>
    /// Produce a signed admission. The <paramref name="signer"/> is the admitting member (for genesis, the
    /// founder self-admitting). The admitter's public key is read from <see cref="IOperationSigner.IssuerId"/>
    /// so the signature is always attributable to the actual signing key.
    /// </summary>
    public static AdmissionSignature SignAdmission(
        IOperationSigner signer,
        Guid teamId,
        string admittedPartyId,
        PrincipalId admittedPublicKey,
        string admittedByPartyId,
        bool isGenesis,
        DateTimeOffset issuedAt,
        Guid nonce,
        string admittedDmPublicKey = "",
        string admittedXWingPublicKey = "",
        string admittedViaTokenId = "",
        string admittedUnderSessionEvidence = "")
    {
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentException.ThrowIfNullOrWhiteSpace(admittedPartyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(admittedByPartyId);

        // C5 — normalize the DM key to a non-null string so the signed bytes are deterministic for a no-DM-key
        // admission (the empty string is signed verbatim; an attacker cannot later inject a DM key without re-signing).
        var dmKey = admittedDmPublicKey ?? string.Empty;
        // C5-X — same discipline for the X-Wing key: a non-null string so "no X-Wing key" is a deterministic signed
        // binding (X-Wing-incapable members safe-degrade to suite #1; an attacker cannot inject an X-Wing key without
        // re-signing, which it cannot).
        var xwingKey = admittedXWingPublicKey ?? string.Empty;
        // #3167 R1.2 — same discipline for the admitter-provenance fields: normalize to non-null so a non-pairing
        // admission signs the empty string verbatim (byte-stable), and a pairing admission binds the token id +
        // mint-session evidence into the forge-proof envelope.
        var viaTokenId = admittedViaTokenId ?? string.Empty;
        var sessionEvidence = admittedUnderSessionEvidence ?? string.Empty;

        var admitterKey = signer.IssuerId;
        var record = new AdmissionRecord(
            TeamId: teamId.ToString("D"),
            AdmittedPartyId: admittedPartyId,
            AdmittedPublicKey: admittedPublicKey.ToBase64Url(),
            AdmittedByPartyId: admittedByPartyId,
            AdmittedByPublicKey: admitterKey.ToBase64Url(),
            IsGenesis: isGenesis,
            AdmittedDmPublicKey: dmKey,
            AdmittedXWingPublicKey: xwingKey,
            AdmittedViaTokenId: viaTokenId,
            AdmittedUnderSessionEvidence: sessionEvidence);

        // The signing envelope's IssuerId is the admitter's key (set by the signer); IssuedAt is truncated to
        // epoch-ms to keep the stored instant and the signed instant byte-aligned (the same precaution
        // CommsMessageFactory takes), so VerifyAdmission reconstructs identical signable bytes.
        var issuedAtMs = DateTimeOffset.FromUnixTimeMilliseconds(issuedAt.ToUnixTimeMilliseconds());
        var op = signer.SignAsync(record, issuedAtMs, nonce).AsTask().GetAwaiter().GetResult();

        return new AdmissionSignature(
            AdmittedByPublicKey: admitterKey.ToBase64Url(),
            AdmittedByPartyId: admittedByPartyId,
            IssuedAt: issuedAtMs,
            Nonce: nonce,
            Signature: op.Signature.ToBase64Url(),
            IsGenesis: isGenesis,
            DmPublicKey: dmKey,
            XWingPublicKey: xwingKey,
            AdmittedViaTokenId: viaTokenId,
            MintingSessionEvidence: sessionEvidence);
    }

    /// <summary>
    /// Re-verify an admission: reconstruct the signed envelope from the admission's stored fields + the
    /// (team, admitted party, admitted key) it claims, and check the Ed25519 signature against the STAMPED
    /// admitter key (<see cref="AdmissionSignature.AdmittedByPublicKey"/>). Returns true iff the admitter's key
    /// validly signed THIS exact (party, key) admission. Fail-closed on any malformed field.
    /// </summary>
    public static bool VerifyAdmission(
        Guid teamId,
        string admittedPartyId,
        PrincipalId admittedPublicKey,
        AdmissionSignature admission,
        IOperationVerifier verifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(admittedPartyId);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(verifier);

        try
        {
            var record = new AdmissionRecord(
                TeamId: teamId.ToString("D"),
                AdmittedPartyId: admittedPartyId,
                AdmittedPublicKey: admittedPublicKey.ToBase64Url(),
                AdmittedByPartyId: admission.AdmittedByPartyId,
                AdmittedByPublicKey: admission.AdmittedByPublicKey,
                IsGenesis: admission.IsGenesis,
                // C5 — reconstruct with the DM key the signature STAMPED. The caller passes the carried DM key on
                // the record (MemberAdmissionRecord.DmPublicKey) through to the AdmissionSignature.DmPublicKey, so a
                // SUBSTITUTED DM key (a roster writer changed the carried key away from what the admitter signed)
                // reconstructs a record whose canonical bytes differ from the signed bytes → the Ed25519 check FAILS
                // → the whole record is dropped. THIS is the line that defeats the DM key-substitution MITM.
                AdmittedDmPublicKey: admission.DmPublicKey ?? string.Empty,
                // C5-X — same defence for the X-Wing key: reconstruct with the X-Wing key the signature STAMPED
                // (AdmissionSignature.XWingPublicKey). A SUBSTITUTED X-Wing key reconstructs a record whose canonical
                // bytes differ from the signed bytes → the Ed25519 check FAILS → the whole record is dropped. THIS is
                // the line that defeats the X-Wing key-substitution DEK-leak (the confidentiality vuln #1489 caught).
                AdmittedXWingPublicKey: admission.XWingPublicKey ?? string.Empty,
                // #3167 R1.2 — reconstruct with the token id + mint-session evidence the signature STAMPED
                // (AdmissionSignature.AdmittedViaTokenId / .MintingSessionEvidence). A roster writer who strips or
                // alters the provenance (e.g. drops the token id on a synced/wire round-trip) reconstructs a record
                // whose canonical bytes differ from the signed bytes → the Ed25519 check FAILS → the record is
                // dropped. This is what makes "an admission whose signature does not bind the token identity is
                // invalid on this path" enforceable rather than advisory.
                AdmittedViaTokenId: admission.AdmittedViaTokenId ?? string.Empty,
                AdmittedUnderSessionEvidence: admission.MintingSessionEvidence ?? string.Empty);

            var op = new SignedOperation<AdmissionRecord>(
                Payload: record,
                IssuerId: PrincipalId.FromBase64Url(admission.AdmittedByPublicKey),
                IssuedAt: admission.IssuedAt,
                Nonce: admission.Nonce,
                Signature: Signature.FromBase64Url(admission.Signature));
            return verifier.Verify(op);
        }
        catch (FormatException)
        {
            // Malformed key / signature → not verifiable → not authentic.
            return false;
        }
    }

    /// <summary>
    /// Produce a signed REVOCATION (the syncable counterpart of <see cref="SignAdmission"/> — roster-sync gap
    /// #1). The <paramref name="signer"/> is the revoking in-roster admin (its key must match the admin's
    /// roster binding and it must hold <c>members:revoke</c> — enforced where the revocation is APPLIED, not
    /// here). The signature covers the canonical <see cref="RevocationRecord"/> = (team, revoked party,
    /// revoker party, revoker key), so a peer can independently confirm the revoker key signed THIS exact
    /// (team, revoked-party) revocation.
    /// </summary>
    public static RevocationSignature SignRevocation(
        IOperationSigner signer,
        Guid teamId,
        string revokedPartyId,
        string revokedByPartyId,
        DateTimeOffset issuedAt,
        Guid nonce)
    {
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentException.ThrowIfNullOrWhiteSpace(revokedPartyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(revokedByPartyId);

        var revokerKey = signer.IssuerId;
        var record = new RevocationRecord(
            TeamId: teamId.ToString("D"),
            RevokedPartyId: revokedPartyId,
            RevokedByPartyId: revokedByPartyId,
            RevokedByPublicKey: revokerKey.ToBase64Url());

        // Truncate IssuedAt to epoch-ms to keep the stored instant and the signed instant byte-aligned (the
        // same precaution SignAdmission takes), so VerifyRevocation reconstructs identical signable bytes.
        var issuedAtMs = DateTimeOffset.FromUnixTimeMilliseconds(issuedAt.ToUnixTimeMilliseconds());
        var op = signer.SignAsync(record, issuedAtMs, nonce).AsTask().GetAwaiter().GetResult();

        return new RevocationSignature(
            RevokedByPublicKey: revokerKey.ToBase64Url(),
            RevokedByPartyId: revokedByPartyId,
            IssuedAt: issuedAtMs,
            Nonce: nonce,
            Signature: op.Signature.ToBase64Url());
    }

    /// <summary>
    /// Re-verify a REVOCATION: reconstruct the signed envelope from the revocation's stored fields + the
    /// (team, revoked party) it claims, and check the Ed25519 signature against the STAMPED revoker key
    /// (<see cref="RevocationSignature.RevokedByPublicKey"/>). Returns true iff the revoker's key validly
    /// signed THIS exact revocation. Fail-closed on any malformed field. This proves integrity + that the
    /// holder of the stamped key signed it; the receiving roster ADDITIONALLY checks the stamped key is an
    /// in-roster admin holding <c>members:revoke</c> (the authority check) before applying.
    /// </summary>
    public static bool VerifyRevocation(
        Guid teamId,
        string revokedPartyId,
        RevocationSignature revocation,
        IOperationVerifier verifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revokedPartyId);
        ArgumentNullException.ThrowIfNull(revocation);
        ArgumentNullException.ThrowIfNull(verifier);

        try
        {
            var record = new RevocationRecord(
                TeamId: teamId.ToString("D"),
                RevokedPartyId: revokedPartyId,
                RevokedByPartyId: revocation.RevokedByPartyId,
                RevokedByPublicKey: revocation.RevokedByPublicKey);

            var op = new SignedOperation<RevocationRecord>(
                Payload: record,
                IssuerId: PrincipalId.FromBase64Url(revocation.RevokedByPublicKey),
                IssuedAt: revocation.IssuedAt,
                Nonce: revocation.Nonce,
                Signature: Signature.FromBase64Url(revocation.Signature));
            return verifier.Verify(op);
        }
        catch (FormatException)
        {
            // Malformed key / signature → not verifiable → not authentic.
            return false;
        }
    }
}
