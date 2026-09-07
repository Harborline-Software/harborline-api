using Harborline.Api.Blocks.Assets.Registry.Model.Spatial;

namespace Harborline.Api.Blocks.Assets.Registry.Services.Spatial;

/// <summary>
/// The typed domain rejection of a losing mint (ADR 0168 D2-A3 layer 2/3 — modelled on
/// <c>HomeEpochAdvanceRejectedException</c>). A raw <c>DbUpdateException</c> is NOT an acceptable
/// surface: a naive handler retries it, and a retry of a rejected mint IS the collision. Raised
/// AFTER the quarantine artifact has committed (0168 D2-A6 — a rejection with no artifact is
/// silent dropping); the losing content is retrievable via the quarantine surface.
/// </summary>
public sealed class SpatialFrameEpochMintRejectedException : Exception
{
    /// <summary>The stable classified reason.</summary>
    public SpatialFrameQuarantineReason Reason { get; }

    /// <summary>The id of the committed quarantine record holding the losing content.</summary>
    public Guid QuarantineId { get; }

    /// <summary>The tenant of the rejected mint.</summary>
    public string TenantId { get; }

    /// <summary>The anchor of the rejected mint.</summary>
    public string Anchor { get; }

    /// <summary>The normalized frame code of the rejected mint.</summary>
    public string FrameCode { get; }

    /// <summary>The epoch the losing mint attempted.</summary>
    public long AttemptedEpoch { get; }

    /// <summary>Constructs the rejection from its committed quarantine artifact.</summary>
    public SpatialFrameEpochMintRejectedException(SpatialFrameQuarantineRecord quarantine)
        : base($"Spatial-frame epoch mint rejected ({quarantine?.Reason}) for tenant "
               + $"'{quarantine?.TenantId.Value}', anchor '{quarantine?.Anchor.Value}', frame "
               + $"'{quarantine?.FrameCode}': attempted epoch {quarantine?.AttemptedEpoch} against tip "
               + $"{quarantine?.TipEpochAtDetection}. The losing content is quarantined "
               + $"(id {quarantine?.Id}), never dropped — do not retry blindly; re-read the tip.")
    {
        ArgumentNullException.ThrowIfNull(quarantine);
        Reason = quarantine.Reason;
        QuarantineId = quarantine.Id;
        TenantId = quarantine.TenantId.Value;
        Anchor = quarantine.Anchor.Value;
        FrameCode = quarantine.FrameCode;
        AttemptedEpoch = quarantine.AttemptedEpoch;
    }
}
