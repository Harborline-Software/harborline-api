using System;
using System.Collections.Generic;
using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.Foundation.IdentityAtlas;

/// <summary>
/// The signed admission record that roots a membership edge in the genesis-anchored trust roster (enrollment
/// Phase A; cerebrum [2026-06-20] "the roster (TeamMembership + pubkey + admission-signature, genesis-rooted
/// signed log)"). Each admission is signed by an in-roster admin who holds <c>members:admit</c>; the GENESIS
/// admission is signed by the founder over their OWN key (the founder self-admits — the immutable chain root).
/// </summary>
/// <remarks>
/// <para>
/// <b>What it binds.</b> The signature covers the canonical-JSON signable form of
/// <see cref="AdmissionRecord"/> = <c>{teamId, admittedPartyId, admittedPublicKey, admittedByPartyId,
/// admittedByPublicKey, isGenesis}</c>. So a verifier can independently confirm: (1) the admitting key signed
/// THIS admission (integrity), and (2) the admitting key is itself a roster member with <c>members:admit</c>
/// that validates to genesis (the chain). The party→pubkey binding for the admitted member is part of the
/// signed payload — which is exactly what makes the roster forge-proof: you cannot enroll a key under a
/// party's name without an in-roster admitter signing that specific (party, key) pair.
/// </para>
/// <para>
/// <b>Genesis vs live (taxonomy §3 guard 4).</b> The genesis admission is self-signed (the admitting key
/// equals the admitted key, <see cref="IsGenesis"/> = true) and is the immutable chain root — removing the
/// genesis member from LIVE permission state does NOT remove them from the genesis chain (the chain still
/// verifies through them). Live permission state is fully mutable on top.
/// </para>
/// </remarks>
/// <param name="AdmittedByPublicKey">base64url of the admitter's Ed25519 public key (the signer). For genesis,
/// equal to the admitted key (self-admission).</param>
/// <param name="AdmittedByPartyId">The admitter's party id (the in-roster admin who holds members:admit).</param>
/// <param name="IssuedAt">Wall-clock instant the admission was issued (UTC).</param>
/// <param name="Nonce">Per-issuance nonce.</param>
/// <param name="Signature">base64url Ed25519 signature by the admitter over the canonical
/// <see cref="AdmissionRecord"/> form.</param>
/// <param name="IsGenesis">True iff this is the founding self-admission (the immutable chain root).</param>
/// <param name="DmPublicKey">
/// C5 (DM key-substitution fix; sec-eng deep-review of PR #1326) — base64url of the admitted member's
/// TEAM-SCOPED DM-encryption X25519 PUBLIC key, <b>BOUND INTO the signed <see cref="AdmissionRecord"/> envelope</b>
/// (the LAST signable field). A DM key is used in a NON-INTERACTIVE ECDH with NO proof-of-possession at use time,
/// so — unlike the transport key, which the handshake's PoP backstops — substituting it directly hands the
/// per-conversation seal key to the substituter. Binding it into the FORGE-PROOF admission (exactly as the
/// principal key is) closes that: only a legitimate admission, signed by the admitter over THIS exact
/// (party → DM-pubkey) pair, carries it; a roster writer who substitutes a peer's DM key produces a record whose
/// signature no longer validates and is DROPPED on rebuild. Empty (default) for a legacy/no-DM-key admission — the
/// empty string is itself part of the signed bytes, so a member with no DM key has a forge-proof "no DM key"
/// binding too.</param>
/// <param name="XWingPublicKey">
/// C5-X (X-Wing key-substitution fix; sec-eng deep-review of PR #1489) — base64url of the admitted member's
/// TEAM-SCOPED X-Wing (X25519 + ML-KEM-768) PUBLIC key, <b>BOUND INTO the signed <see cref="AdmissionRecord"/>
/// envelope</b> (the LAST signable field, after the DM key). The X-Wing key is a <b>confidentiality</b> key exactly
/// like the DM key: a SENDER encapsulates a tenant DEK / role key to it (suite #3), and X-Wing
/// (<c>draft-connolly-cfrg-xwing-kem</c>) is an UNAUTHENTICATED KEM with NO recipient-identity binding — so
/// substituting a peer's published X-Wing key hands the boxed secret (the DEK) to whoever holds the matching
/// private seed (the substituter), the SAME leak class as the DM key. The earlier "unsigned-by-association is
/// safe because the wrap's outer Ed25519 + the recipient's own seed backstop it" reasoning was FALSE (the outer
/// sig faithfully attests the poisoned box; the recipient who would fail is bypassed, not the attacker who
/// opens). So the X-Wing key is bound into the FORGE-PROOF admission exactly as C5 did for the DM key — a roster
/// writer who substitutes it produces a record whose signature no longer validates and is DROPPED on rebuild.
/// Empty (default) for a legacy/no-X-Wing admission (X-Wing-incapable member → safe-degrades to suite #1); the
/// empty string is itself part of the signed bytes, so "no X-Wing key" is a forge-proof binding too.</param>
/// <param name="AdmittedViaTokenId">
/// MTW-2 #3167 (R1.2 — admitter-provenance) — the id of the single-use pairing/admission token this admission was
/// signed FROM, <b>BOUND INTO the signed <see cref="AdmissionRecord"/> envelope</b> (a trailing signable field,
/// after the X-Wing key). On the web-admitted-member PAIRING path the node admitter's authority is DERIVED
/// per-admission from the redeemed token chain (never standing), so the admission MUST bind the token identity —
/// its provenance then reads "admitted by token T" rather than "admitted by node fiat". Empty (default) for the
/// non-pairing modes (proximity / plain invite) whose provenance is the admitter's presence, not a token; the
/// empty string is signed verbatim, so a legacy/non-token admission's signed bytes stay stable AND an attacker
/// cannot inject a token id without re-signing (which it cannot).</param>
/// <param name="MintingSessionEvidence">
/// MTW-2 #3167 (R1.2) — the opaque mint-time SessionCorrelationId captured at token mint (the member's own
/// authenticated web session), <b>BOUND INTO the signed <see cref="AdmissionRecord"/> envelope</b> (the LAST
/// signable field). Together with <see cref="AdmittedViaTokenId"/> the admission's audit provenance reads
/// "admitted by token T minted under session S". This is an AUDIT provenance claim, NOT a verification claim
/// (per admiral-ruling-amendment-2026-07-24T1210Z R1.2 — no verifiable web-session material enters the atlas
/// envelope). Empty (default) for the non-pairing modes; the empty string is signed verbatim (byte-stable).</param>
/// <param name="Permissions">The permission atoms signed at admission; null denotes an empty set.</param>
public sealed record AdmissionSignature(
    string AdmittedByPublicKey,
    string AdmittedByPartyId,
    DateTimeOffset IssuedAt,
    Guid Nonce,
    string Signature,
    bool IsGenesis,
    string DmPublicKey = "",
    string XWingPublicKey = "",
    string AdmittedViaTokenId = "",
    string MintingSessionEvidence = "",
    IReadOnlyCollection<string>? Permissions = null);

/// <summary>
/// The canonical signable payload an <see cref="AdmissionSignature"/> attests — the (team, admitted party,
/// admitted key) tuple plus the admitter's identity. Pinning the property names keeps the canonical signed
/// bytes stable across signer + verifier (the same discipline <c>MessageCrdtState.SignablePayload</c> uses).
/// </summary>
/// <param name="TeamId">The org/team this admission is into (string form of the Guid).</param>
/// <param name="AdmittedPartyId">The party being admitted.</param>
/// <param name="AdmittedPublicKey">base64url of the admitted member's public key (the party→key binding).</param>
/// <param name="AdmittedByPartyId">The admitting in-roster admin's party id.</param>
/// <param name="AdmittedByPublicKey">base64url of the admitting admin's public key.</param>
/// <param name="IsGenesis">Whether this is the founding self-admission.</param>
/// <param name="AdmittedDmPublicKey">
/// C5 — base64url of the admitted member's team-scoped DM-encryption X25519 PUBLIC key, signed INTO the admission
/// envelope so the (party → DM-pubkey) binding is forge-proof (the DM key-substitution fix; sec-eng deep-review of
/// PR #1326). Defaulting empty, so a legacy/no-DM-key admission's signed bytes stay stable (the
/// empty string is signed verbatim — an attacker cannot inject a DM key without re-signing, which it cannot).</param>
/// <param name="AdmittedXWingPublicKey">
/// C5-X — base64url of the admitted member's team-scoped X-Wing (X25519 + ML-KEM-768) PUBLIC key, signed INTO the
/// admission envelope so the (party → X-Wing-pubkey) binding is forge-proof (the X-Wing key-substitution fix;
/// sec-eng deep-review of PR #1489). The X-Wing key is a confidentiality key like the DM key — substituting it
/// hands the boxed DEK to the substituter (X-Wing is an unauthenticated KEM with no recipient-identity binding) —
/// so it must be signed in, not carried unsigned-by-association like the transport key. The LAST field, defaulting
/// empty, so a legacy/no-X-Wing admission's signed bytes stay stable (the empty string is signed verbatim — an
/// attacker cannot inject an X-Wing key without re-signing, which it cannot).</param>
/// <param name="AdmittedViaTokenId">
/// MTW-2 #3167 (R1.2) — the single-use pairing/admission token id this admission was signed FROM, signed INTO the
/// envelope so the (admission → token) provenance is forge-proof. The LAST-but-one signable field, defaulting
/// empty (byte-stable for non-token admissions; the empty string is signed verbatim — an attacker cannot inject a
/// token id without re-signing).</param>
/// <param name="AdmittedUnderSessionEvidence">
/// MTW-2 #3167 (R1.2) — the opaque mint-time SessionCorrelationId (audit provenance, not a verification claim),
/// signed INTO the envelope. The LAST signable field, defaulting empty (byte-stable).</param>
/// <param name="AdmittedPermissions">Ordinally sorted, deduplicated permission atoms, including an explicit empty array.</param>
/// <param name="FormatVersion">Version 2 binds permissions. Pre-versioned signatures are deliberately invalid.</param>
public sealed record AdmissionRecord(
    string TeamId,
    string AdmittedPartyId,
    string AdmittedPublicKey,
    string AdmittedByPartyId,
    string AdmittedByPublicKey,
    bool IsGenesis,
    string AdmittedDmPublicKey = "",
    string AdmittedXWingPublicKey = "",
    string AdmittedViaTokenId = "",
    string AdmittedUnderSessionEvidence = "",
    IReadOnlyCollection<string>? AdmittedPermissions = null,
    int FormatVersion = RosterWireFormat.CurrentVersion);
