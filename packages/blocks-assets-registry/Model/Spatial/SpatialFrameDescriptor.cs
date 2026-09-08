using Harborline.Api.Blocks.Assets.Registry.Model;


namespace Harborline.Api.Blocks.Assets.Registry.Model.Spatial;

/// <summary>
/// The durable, tenant-scoped, immutable-once-created <b>spatial frame descriptor</b> — the record
/// that DEFINES an asset-local frame, keyed <c>(TenantId, Anchor, FrameCode, FrameEpoch)</c>
/// (ADR 0168 D2 / D2-A1; ADR 0101 Rev 3.2 Wave 5). A re-definition never updates in place; it
/// appends a new descriptor at a new epoch (D2-A2).
/// </summary>
/// <remarks>
/// <para>
/// <b>The attestation is a registry-row persistence envelope</b> (0168 OQ-1 amendment): every
/// signed field persists on the row so the mint signature is independently re-verifiable from the
/// row alone. None of these fields are projected into <c>packages/contracts</c>; the transport
/// envelope is defined when D2-A4(c)'s verification deferral expires, not before.
/// </para>
/// <para>
/// <b><see cref="FrameCode"/> is stored in its [A14]-normalized form</b> (lowercase kebab-case,
/// normalized at the package adapter's single chokepoint BEFORE signing) — the key is the
/// collision defence, so two lexical variants of one frame name never mint two frames.
/// </para>
/// <para>
/// <b>Content prohibition on <see cref="OriginDescription"/> (ADR 0168 D2-A8, defence in depth).</b>
/// The field describes a physical datum ("aft perpendicular at baseline") and MUST NOT carry
/// occupant or party identity — no names, no contact details, no re-identifying prose. It is one
/// of the TWO governed PII cells (with <see cref="Georeference"/>) sealed at the host storage
/// boundary under the tenant DEK (the shipped <c>pii</c> policy binding, <c>SubjectScoped:false</c>).
/// Accepted-risk posture: the single-subject residual is accepted while the content prohibition
/// holds; expiry pinned — the two fields bind to the subject-scoped key class
/// (<c>identifier</c>, <c>SubjectScoped:true</c>) when the ADR 0139 D3 <c>subjectRef</c> seam lands.
/// The prohibition is a content contract, not mechanically enforceable — which is exactly why the
/// storage-boundary sealing exists as the second layer.
/// </para>
/// <para>
/// <b>Governed-cell nullability = Redact@Read (store-level, CIC ruling 2026-08-06).</b>
/// <see cref="OriginDescription"/> is REQUIRED at mint (the adapter throws on null/whitespace)
/// and is <see langword="null"/> ONLY on a descriptor returned by a
/// <c>SpatialFrameReadContext.Redacted</c> read, where both governed cells
/// (<see cref="OriginDescription"/> and <see cref="Georeference"/>) are withheld without being
/// decrypted. On a redacted read a <see langword="null"/> <see cref="Georeference"/> is therefore
/// ambiguous between "absent" and "withheld"; <see cref="OriginDescription"/> is the reliable
/// redaction marker (always non-null on an unsealed read). Row-alone signature re-verification
/// requires the unsealed (privileged) read — a redacted descriptor cannot recompute
/// <c>ContentHash</c> by construction.
/// </para>
/// </remarks>
public sealed record SpatialFrameDescriptor(
    TenantId TenantId,
    RegistryEntityId Anchor,
    string FrameCode,
    long FrameEpoch,
    string AxisConvention,
    string? OriginDescription,
    string LengthUnit,
    SpatialFrameGeoreference? Georeference,
    SpatialFrameMintAttestation Attestation);

/// <summary>
/// The optional, deliberately weak georeference on a descriptor — a <b>timestamped geodetic
/// observation of the frame origin, never a standing conversion</b> (ADR 0168 D2). The substrate
/// computes nothing with it.
/// </summary>
/// <param name="ObservedAt">ISO-8601 instant the observation was taken.</param>
/// <param name="GeodeticCrs">The geodetic CRS code the position is expressed in (e.g. <c>EPSG:4979</c>).</param>
/// <param name="OriginPosition">Ordinates of the frame origin in the geodetic CRS.</param>
/// <param name="Orientation">Optional quaternion <c>xyzw</c> (glTF order — the D1 pin).</param>
/// <param name="PoseBasis">REQUIRED when <paramref name="Orientation"/> is present: <c>enu</c> | <c>ned</c>.</param>
public sealed record SpatialFrameGeoreference(
    string ObservedAt,
    string GeodeticCrs,
    IReadOnlyList<double> OriginPosition,
    IReadOnlyList<double>? Orientation,
    string? PoseBasis);

/// <summary>
/// The mint attestation persisted with a <see cref="SpatialFrameDescriptor"/> — every signed field
/// as a column, so the Ed25519 mint signature is re-verifiable from the row alone
/// (<c>HomeEpochRecord</c> precedent; ADR 0168 D2-A4(c)).
/// </summary>
/// <param name="Issuer">Base64Url of the minting node's Ed25519 public key (the signing principal).</param>
/// <param name="Nonce">The per-mint signature nonce.</param>
/// <param name="IssuedAt">The signed instant (truncated to epoch-ms so stored and signed bytes align).</param>
/// <param name="Signature">Base64Url Ed25519 signature over the canonical
/// <see cref="Services.Spatial.SpatialFrameMintSignaturePayload"/>.</param>
/// <param name="HomeDeviceId">The device holding the positively-asserted home claim at mint time —
/// the node's Ed25519 signing principal id (never <c>NodeIdentity.NodeId</c>).</param>
/// <param name="GrantingHomeEpoch">The <c>HomeEpochRecord.EpochNumber</c> under which mint authority
/// was held — bound into the signature so a demoted device's later mints are reject-stale-able.</param>
/// <param name="PreviousEpoch">The frame epoch this mint supersedes (<c>0</c> at genesis) — bound in
/// so a mint cannot be replayed against a different predecessor.</param>
/// <param name="ContentHash">Lowercase-hex SHA-256 over the canonical content record of the four
/// defining fields. Persists in CLEARTEXT deliberately — it is the signed preimage; sealing or
/// MAC'ing it would make the mint signature unverifiable (0168 OQ-1 amendment). ACCEPTED RISK:
/// the cleartext digest is a confirmation/guessing oracle over the two sealed fields (the other
/// two hash inputs are cleartext), surviving key destruction; accepted because it must persist in
/// the exact signed form. Expiry: revisit when the fields bind to the subject-scoped identifier
/// class (ADR 0139 D3 subjectRef).</param>
/// <para>
/// <b>Redacted-read withholding (deep-review F2 on the 3778 seam):</b> <paramref name="Signature"/>
/// and <paramref name="ContentHash"/> are <see langword="null"/> ONLY on an attestation returned by
/// a <c>SpatialFrameReadContext.Redacted</c> read. The cleartext digest + signature are
/// confirmation oracles over the two withheld governed cells (every other signed input is
/// cleartext, so guess → hash → verify), so a read that withholds the cells must withhold them
/// too. READ-BOUNDARY ONLY: the stored row values and the signed preimage are never touched (the
/// pinned never-seal-the-signed-digest trap), and a privileged read always carries both.
/// </para>
public sealed record SpatialFrameMintAttestation(
    string Issuer,
    Guid Nonce,
    DateTimeOffset IssuedAt,
    string? Signature,
    string HomeDeviceId,
    long GrantingHomeEpoch,
    long PreviousEpoch,
    string? ContentHash);
