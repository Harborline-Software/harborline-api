using Harborline.Api.Blocks.Assets.Registry.Model;


namespace Harborline.Api.Blocks.Assets.Registry.Model.Spatial;

/// <summary>Why a losing mint was quarantined (ADR 0168 D2-A6 — the two detection layers).</summary>
public enum SpatialFrameQuarantineReason
{
    /// <summary>Layer-2: the in-transaction pre-check under the held write lock found the caller's
    /// expected predecessor epoch stale against the durable tip.</summary>
    EpochConflict = 0,

    /// <summary>Layer-3: the composite-primary-key storage backstop rejected the staged descriptor
    /// (a state reachable only through an authority breach or a store defect).</summary>
    StorageConflict = 1,
}

/// <summary>
/// A rejected mint's full defining content plus the identity triple it collided with — a rejection
/// with no artifact would be silent dropping, which ADR 0168 D2-A6 forbids. The quarantine record
/// family sits inside the descriptor's CP-4 governance cell (D2-A8; cell filed in ADR 0128).
/// <c>OriginDescription</c> is always present on a staged/persisted quarantine and is
/// <see langword="null"/> ONLY when a <c>SpatialFrameReadContext.Redacted</c> read withheld the
/// governed cells (store-level Redact@Read — same contract as <see cref="SpatialFrameDescriptor"/>).
/// </summary>
public sealed record SpatialFrameQuarantineRecord(
    Guid Id,
    TenantId TenantId,
    RegistryEntityId Anchor,
    string FrameCode,
    long AttemptedEpoch,
    long TipEpochAtDetection,
    SpatialFrameQuarantineReason Reason,
    string AxisConvention,
    string? OriginDescription,
    string LengthUnit,
    SpatialFrameGeoreference? Georeference,
    DateTimeOffset DetectedAt)
{
    /// <summary>
    /// Builds the layer-3 (PK-backstop) quarantine record from the descriptor whose staged save the
    /// storage constraint rejected — written in a SECOND committed transaction after the rollback.
    /// </summary>
    public static SpatialFrameQuarantineRecord ForStorageConflict(
        SpatialFrameDescriptor losing, DateTimeOffset detectedAt) =>
        new(
            Id: Guid.NewGuid(),
            TenantId: losing.TenantId,
            Anchor: losing.Anchor,
            FrameCode: losing.FrameCode,
            AttemptedEpoch: losing.FrameEpoch,
            TipEpochAtDetection: losing.FrameEpoch,
            Reason: SpatialFrameQuarantineReason.StorageConflict,
            AxisConvention: losing.AxisConvention,
            OriginDescription: losing.OriginDescription,
            LengthUnit: losing.LengthUnit,
            Georeference: losing.Georeference,
            DetectedAt: detectedAt);
}
