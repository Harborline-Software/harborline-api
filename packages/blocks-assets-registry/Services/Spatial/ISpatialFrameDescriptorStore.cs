using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Model.Spatial;


namespace Harborline.Api.Blocks.Assets.Registry.Services.Spatial;

/// <summary>
/// The Wave-5 spatial-frame-descriptor store (ADR 0101 Rev 3.2 [A13]; ADR 0168 OQ-1 ruling).
/// </summary>
/// <remarks>
/// <para>
/// <b>This interface must NEVER resolve to a host type.</b> The only production registration is the
/// package-side adapter (<see cref="FoundationBackedSpatialFrameDescriptorStore"/>) — a builder who
/// registers a host store behind this interface evicts the adapter and, with it, the only code that
/// can call the <c>internal</c> <see cref="RegistryTenantGuard"/>. The composition hard-fail
/// (host-side, [A10]) asserts this.
/// </para>
/// <para>
/// Resolution is <b>tenant-scoped, never node-level existence</b> (Wave 5 scope constraint): one
/// node holds many tenants, so a descriptor "exists" only under the tenant that minted it.
/// </para>
/// </remarks>
public interface ISpatialFrameDescriptorStore
{
    /// <summary>
    /// Mints the next frame epoch for <c>(tenant, anchor, frameCode)</c> as a serialized, guarded,
    /// strict-monotonic append (genesis is 1; each mint appends <c>previous + 1</c> — 0168 D2-A3).
    /// Refuses (fail-closed) without a positively-asserted home claim
    /// (<see cref="FrameEpochMintRefusedException"/>); a losing mint is quarantined, never dropped,
    /// and surfaces as <see cref="SpatialFrameEpochMintRejectedException"/> (0168 D2-A6).
    /// </summary>
    Task<SpatialFrameDescriptor> MintAsync(SpatialFrameMintRequest request, CancellationToken ct = default);

    /// <summary>Resolves one descriptor by its identity triple + epoch, under the tenant.
    /// Every read carries a REQUIRED <see cref="SpatialFrameReadContext"/> — the store-level
    /// Redact@Read / Audit@Read enforcement of the <c>pii</c> binding (CIC ruling 2026-08-06):
    /// <see cref="SpatialFrameReadContext.Redacted"/> withholds the two governed PII cells
    /// (returned <see langword="null"/>, never decrypted, never audited);
    /// <see cref="SpatialFrameReadContext.PrivilegedUnseal"/> unseals them and appends one
    /// identity-triple-only audit row per unsealed row (append failure withholds the read —
    /// fail-closed). Authorization (e.g. the route's <c>spatial:read</c> gate) is the caller's
    /// obligation before choosing the privileged posture.</summary>
    Task<SpatialFrameDescriptor?> FindAsync(
        TenantId tenant, RegistryEntityId anchor, string frameCode, long frameEpoch,
        SpatialFrameReadContext context, CancellationToken ct = default);

    /// <summary>Lists every epoch of a frame series (ascending epoch), under the tenant.
    /// <paramref name="context"/> — see <see cref="FindAsync"/>.</summary>
    Task<IReadOnlyList<SpatialFrameDescriptor>> ListAsync(
        TenantId tenant, RegistryEntityId anchor, string frameCode,
        SpatialFrameReadContext context, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the quarantined (losing) mints of a frame series — the D2-A6 conformance
    /// obligation that losing content is retrievable. Tenant-guarded; the CP-4 authority-gated
    /// retrieval surface governs any exposure beyond this substrate call.
    /// </summary>
    Task<IReadOnlyList<SpatialFrameQuarantineRecord>> ListQuarantinedAsync(
        TenantId tenant, RegistryEntityId anchor, string frameCode,
        SpatialFrameReadContext context, CancellationToken ct = default);
}

/// <summary>
/// A mint request. <paramref name="PreviousEpoch"/> is the caller's believed predecessor (the tip
/// it read; <c>0</c> for a genesis mint) — bound into the signed payload and checked against the
/// durable tip under the held write lock; a stale belief is the layer-2 conflict that quarantines.
/// </summary>
public sealed record SpatialFrameMintRequest(
    TenantId Tenant,
    RegistryEntityId Anchor,
    string FrameCode,
    long PreviousEpoch,
    string AxisConvention,
    string OriginDescription,
    string LengthUnit,
    SpatialFrameGeoreference? Georeference);
