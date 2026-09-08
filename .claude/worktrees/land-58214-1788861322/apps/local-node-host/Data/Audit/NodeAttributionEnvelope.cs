namespace Harborline.Api.LocalNodeHost.Data.Audit;

/// <summary>
/// The single source of truth for the <b>node-signed attribution envelope</b> (MTW-2 2612-C, per the
/// 2612-B key-model ruling, Admiral 2026-07-18). Builds the <c>attribution</c> sub-object that rides
/// INSIDE the audit row's canonical-JSON <see cref="NodeAuditEventRow.Payload"/> — the payload the
/// node's per-event Ed25519 signature already covers (<see cref="NodeAuditSignaturePayload"/>). Because
/// the attribution is part of the signed payload, tampering any attribution field breaks BOTH the
/// Ed25519 signature (reader → <c>VerificationFailed</c>) and the key-independent hash chain: the
/// envelope is tamper-evident with no new signing path (board finding F7 — sign at the ONE
/// durable-mutation seam, not N routes).
/// </summary>
/// <remarks>
/// <para>
/// <b>Attestation, NOT non-repudiation (2612-B ruling; board finding F9).</b> The signer is the NODE
/// key, not the member's key. The envelope asserts "this node attests member Party <c>P</c> acted, in
/// membership <c>M</c>, on session <c>S</c>, under authority epoch <c>E</c> / grant pins <c>…</c>." A
/// node CAN assert any member — true per-member non-repudiation waits for the passkey / roster-key
/// future (ADR 0153 direction, deferred). Callers and tests describe what this proves: <b>attestation
/// integrity + tamper-evidence</b>, NEVER "impersonation-resistance."
/// </para>
/// <para>
/// <b>The verifying-key binding is SIGNED (board findings F4/F5).</b> The <c>key_binding</c> row carries
/// the acting member Party bound to the NODE's attesting public key. Because that binding lives inside
/// the signed payload, it cannot be forged without the node key — closing the forgeable-binding gap
/// (the C5/X-Wing admission lesson: any key must be SIGNED-in, never carried unsigned-by-association)
/// WITHOUT inventing per-member key distribution. Key <b>lifecycle</b> binds to the node key's existing
/// epoch/rotation doctrine for free: the attribution envelope IS the audit signature, so a passphrase
/// reseal seals the old node public key as a <see cref="NodeAuditSignatureEpochRow"/> and pre-reseal
/// attribution envelopes verify against that sealed epoch exactly as every other audit row does (ADR
/// 0126 §D4 / OQ1). There is no separate attribution key store.
/// </para>
/// <para>
/// <b>Vocabulary collision, disambiguated (board finding F8).</b> <c>member_party_id</c> is the AUTHZ
/// People <c>PartyId</c> (a <see cref="Harborline.Api.Foundation.Authorization.CanonicalPartyReference"/>
/// string). <c>attesting_public_key</c> is the CRYPTO
/// <see cref="Harborline.Api.Foundation.Crypto.PrincipalId"/> — the node's 32-byte Ed25519 signing key, base64url
/// — i.e. the signature's issuer. The two are NEVER the same value and never interchanged: a Party is not
/// a key.
/// </para>
/// </remarks>
internal static class NodeAttributionEnvelope
{
    /// <summary>The envelope schema tag — versions the attribution shape independently of the audit row.</summary>
    internal const string SchemaTag = "node-signed-attestation/v1";

    /// <summary>Mode: a real selected-session member acted (bound principal on the request).</summary>
    internal const string ModeMemberSession = "member-session";

    /// <summary>Mode: the single-operator bootstrap / desktop / detached-workflow fallback (no bound principal).</summary>
    internal const string ModeOperatorFallback = "operator-fallback";

    /// <summary>The attestation kind — the node key vouches; this is NOT per-member non-repudiation.</summary>
    internal const string NodeAttestationKind = "node-signed";

    /// <summary>
    /// Builds the <c>attribution</c> sub-object for the signed audit payload.
    /// </summary>
    /// <param name="attribution">
    /// The acting member's attribution, or <c>null</c> for the operator fallback (no bound principal).
    /// </param>
    /// <param name="operatorPartyId">
    /// The single-operator fallback Party id (equals the node's <c>local</c> operator identity) —
    /// stamped as <c>member_party_id</c> when <paramref name="attribution"/> is null.
    /// </param>
    /// <param name="attestingPublicKeyBase64Url">
    /// The node's attesting Ed25519 public key (the signer's <c>IssuerId</c>, base64url), or <c>null</c>
    /// when the node signer is not wired (a v0/unsigned audit build — the binding degrades to
    /// hash-chain-only integrity, never a false <c>VerificationFailed</c>).
    /// </param>
    internal static Dictionary<string, object?> Build(
        NodeCallerAttribution? attribution,
        string operatorPartyId,
        string? attestingPublicKeyBase64Url)
    {
        ArgumentException.ThrowIfNullOrEmpty(operatorPartyId);

        var isMemberSession = attribution is not null;
        var memberPartyId = attribution?.MemberPartyId ?? operatorPartyId;

        // F4/F5 — the party→pubkey binding rides INSIDE the signed payload. The acting member Party is
        // bound to the NODE's attesting key (attestation, not per-member non-repudiation — F9).
        var keyBinding = new Dictionary<string, object?>
        {
            ["member_party_id"] = memberPartyId,
            ["attesting_public_key"] = attestingPublicKeyBase64Url,
            ["attestation_kind"] = NodeAttestationKind,
        };

        return new Dictionary<string, object?>
        {
            ["schema"] = SchemaTag,
            ["mode"] = isMemberSession ? ModeMemberSession : ModeOperatorFallback,
            ["member_party_id"] = memberPartyId,
            ["membership"] = attribution is null ? null : new Dictionary<string, object?>
            {
                ["id"] = attribution.MembershipId,
                ["owner_version"] = attribution.MembershipOwnerVersion,
            },
            ["session"] = attribution is null ? null : new Dictionary<string, object?>
            {
                ["correlation_id"] = attribution.SessionCorrelationId,
                ["coordination_correlation_id"] = attribution.CoordinationCorrelationId,
            },
            ["key_binding"] = keyBinding,
        };
    }
}
