namespace Harborline.Api.LocalNodeHost.Data.AssetRegistry;

/// <summary>
/// Durable row for one <c>SpatialFrameDescriptor</c> (ADR 0101 Rev 3.2 Wave 5 / [A13] host-local
/// bullet): composite PK <c>(TenantId, AnchorId, FrameCode, FrameEpoch)</c> — the D2-A3 layer-3
/// storage backstop — plus <b>a column per signed field</b> so the Ed25519 mint signature is
/// independently re-verifiable from the row alone.
/// </summary>
/// <remarks>
/// <c>ContentHash</c> is deliberately CLEARTEXT and unsealed: it is the signed preimage — sealing
/// or MAC'ing it would make the mint signature unverifiable forever (the pinned trap), and it is
/// not part of the two-field governed set (0168 D2-A8). The CP-4 field-envelope sealing of
/// <c>Georeference</c> + <c>OriginDescription</c> (Wave-5 precondition 3) is applied by the port
/// via <see cref="SpatialFramePiiFieldSealer"/>: those two columns hold the tenant-DEK
/// <c>EncryptedField</c> envelope JSON at rest, never prose; re-verification decrypts at read and
/// recomputes the hash over the recovered cleartext. The tenant DEK is a random stored key wrapped
/// by the resolved at-rest hierarchy and can be destroyed. Subject-grain shredding remains unavailable
/// until the two fields bind to the subject-scoped <c>identifier</c> class (ADR 0139 D3
/// <c>subjectRef</c> — the pinned expiry).
/// </remarks>
public sealed class SpatialFrameDescriptorRow
{
    /// <summary>Tenant scope (composite-key member; explicit column per ADR 0092).</summary>
    public required string TenantId { get; init; }

    /// <summary>The anchor <c>RegistryEntityId</c> canonical string (composite-key member).</summary>
    public required string AnchorId { get; init; }

    /// <summary>The [A14]-normalized lowercase-kebab frame code (composite-key member).</summary>
    public required string FrameCode { get; init; }

    /// <summary>The strict-monotonic frame epoch (genesis 1; composite-key member).</summary>
    public required long FrameEpoch { get; init; }

    /// <summary>Stable axis-convention code (cleartext — keying/display, 0168 D2-A8).</summary>
    public required string AxisConvention { get; init; }

    /// <summary>Governed field — holds the tenant-DEK <c>EncryptedField</c> envelope JSON at rest (CP-4).</summary>
    public required string OriginDescription { get; init; }

    /// <summary>The fixed length unit (<c>metre</c>, ADR 0141).</summary>
    public required string LengthUnit { get; init; }

    /// <summary>Optional georeference — sealed envelope JSON at rest when present (governed field, CP-4).</summary>
    public string? GeoreferenceJson { get; init; }

    /// <summary>Base64Url of the minting node's Ed25519 public key (the signing principal).</summary>
    public required string Issuer { get; init; }

    /// <summary>The per-mint signature nonce (signed-envelope field).</summary>
    public required Guid Nonce { get; init; }

    /// <summary>The signed instant, truncated to epoch-ms (signed-envelope field).</summary>
    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>Base64Url Ed25519 signature over the canonical mint payload.</summary>
    public required string Signature { get; init; }

    /// <summary>The home device the mint authority was asserted for (signed field).</summary>
    public required string HomeDeviceId { get; init; }

    /// <summary>The <c>HomeEpochRecord.EpochNumber</c> the authority was held under (signed field).</summary>
    public required long GrantingHomeEpoch { get; init; }

    /// <summary>The superseded frame epoch (<c>0</c> at genesis; signed field).</summary>
    public required long PreviousEpoch { get; init; }

    /// <summary>
    /// Lowercase-hex SHA-256 over the canonical content record — CLEARTEXT, see remarks.
    /// <para><b>ACCEPTED RISK — cleartext-digest confirmation oracle.</b> Because axisConvention
    /// and lengthUnit are cleartext, a row holder can confirm a HYPOTHESIZED
    /// originDescription/georeference with a single hash computation — a guessing oracle over the
    /// two sealed fields that survives any key destruction. Accepted because the digest must
    /// persist in the exact form the mint signature covers (sealing or MAC'ing it breaks every
    /// historical signature — the pinned trap). Same expiry as the sealing posture: revisit when
    /// the fields bind to the subject-scoped identifier class (ADR 0139 D3 subjectRef).</para>
    /// </summary>
    public required string ContentHash { get; init; }
}

/// <summary>
/// Durable quarantine row for a rejected (losing) mint — full defining content plus the identity
/// triple it collided with (0168 D2-A6). Same CP-4 governance cell as the descriptor.
/// </summary>
public sealed class SpatialFrameQuarantineRow
{
    /// <summary>Primary key.</summary>
    public required Guid Id { get; init; }

    /// <summary>Tenant scope.</summary>
    public required string TenantId { get; init; }

    /// <summary>The anchor of the collided identity triple.</summary>
    public required string AnchorId { get; init; }

    /// <summary>The normalized frame code of the collided identity triple.</summary>
    public required string FrameCode { get; init; }

    /// <summary>The epoch the losing mint attempted.</summary>
    public required long AttemptedEpoch { get; init; }

    /// <summary>The durable tip at detection.</summary>
    public required long TipEpochAtDetection { get; init; }

    /// <summary>The stable classified reason (string form of <c>SpatialFrameQuarantineReason</c>).</summary>
    public required string Reason { get; init; }

    /// <summary>Losing axis-convention code.</summary>
    public required string AxisConvention { get; init; }

    /// <summary>Losing origin prose — sealed envelope JSON at rest (governed field, CP-4).</summary>
    public required string OriginDescription { get; init; }

    /// <summary>Losing length unit.</summary>
    public required string LengthUnit { get; init; }

    /// <summary>Losing georeference — sealed envelope JSON at rest when present (governed field, CP-4).</summary>
    public string? GeoreferenceJson { get; init; }

    /// <summary>When the conflict was detected.</summary>
    public required DateTimeOffset DetectedAt { get; init; }
}
