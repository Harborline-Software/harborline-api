using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Model.Spatial;


namespace Harborline.Api.Blocks.Assets.Registry.Services.Spatial;

/// <summary>
/// The <b>foundation descriptor port</b> the package-side adapter delegates persistence to — the
/// descriptor analogue of the audit adapter's <c>IAuditLog</c> (ADR 0101 Rev 3.2 [A13]). Declared
/// in the registry package (which stays a pure domain substrate, no EF); implemented HOST-side
/// (<c>NodeEfSpatialFrameDescriptorPort</c>) because the fence and transaction helpers are
/// host-only types. A host swaps persistence by replacing THIS port registration — never
/// <see cref="ISpatialFrameDescriptorStore"/>, which must stay bound to the package adapter.
/// </summary>
/// <remarks>
/// The port owns the fenced transaction (0168 D2-A4(b)): it runs the mint inside the host's
/// <c>BEGIN IMMEDIATE</c> fence transaction, performs the enforcing in-transaction re-read of the
/// tenant's home claim (refusing unless a <c>HomeEpochRecord</c> exists and its
/// <c>HomeDeviceId</c> is this device), fixes the frame-epoch tip under the write lock, and then
/// invokes the package mint callback ON the in-flight context — guard → tip-read →
/// normalize → sign → stage all happen under that one held lock. The audit append follows the
/// commit decision on BOTH D2-A6 paths and is owned by the port, never part of the atomic unit.
/// </remarks>
public interface ISpatialFrameDescriptorPort
{
    /// <summary>
    /// Runs one fenced mint. Returns the committed descriptor; throws
    /// <see cref="FrameEpochMintRefusedException"/> when no positively-asserted home claim exists,
    /// or <see cref="SpatialFrameEpochMintRejectedException"/> AFTER the quarantine artifact has
    /// committed on either D2-A6 path.
    /// </summary>
    Task<SpatialFrameDescriptor> MintAsync(
        SpatialFrameMintCommand command,
        SpatialFrameMintCallback callback,
        CancellationToken ct = default);

    /// <summary>Reads one descriptor by identity triple + epoch under the tenant.
    /// <paramref name="context"/> is the Redact@Read / Audit@Read enforcement input
    /// (see <see cref="SpatialFrameReadContext"/>) — redacted reads withhold the governed cells;
    /// privileged reads unseal them and append one identity-triple audit row per unsealed row.</summary>
    Task<SpatialFrameDescriptor?> FindAsync(
        TenantId tenant, RegistryEntityId anchor, string frameCode, long frameEpoch,
        SpatialFrameReadContext context, CancellationToken ct = default);

    /// <summary>Reads a frame series (ascending epoch) under the tenant.
    /// <paramref name="context"/> — see <see cref="FindAsync"/>.</summary>
    Task<IReadOnlyList<SpatialFrameDescriptor>> ListAsync(
        TenantId tenant, RegistryEntityId anchor, string frameCode,
        SpatialFrameReadContext context, CancellationToken ct = default);

    /// <summary>Reads the quarantined mints of a frame series under the tenant.
    /// <paramref name="context"/> — see <see cref="FindAsync"/>.</summary>
    Task<IReadOnlyList<SpatialFrameQuarantineRecord>> ListQuarantinedAsync(
        TenantId tenant, RegistryEntityId anchor, string frameCode,
        SpatialFrameReadContext context, CancellationToken ct = default);
}

/// <summary>The identity a fenced mint runs under. <paramref name="FrameCode"/> is already the
/// [A14]-normalized form (the adapter chokepoint normalizes before delegating).</summary>
public sealed record SpatialFrameMintCommand(TenantId Tenant, RegistryEntityId Anchor, string FrameCode);

/// <summary>
/// What the host's fenced transaction hands the package callback: the values fixed UNDER the held
/// write lock — the frame-epoch tip (0 when the series is empty) and the home claim the
/// in-transaction re-read positively asserted (0168 D2-A4(a)/(b)).
/// </summary>
public sealed record SpatialFrameMintContext(long TipEpoch, string HomeDeviceId, long GrantingHomeEpoch);

/// <summary>
/// The package-side mint body — guard, normalize (already applied at the adapter chokepoint), sign
/// and decide staging — invoked by the port INSIDE the fenced transaction. It returns either the
/// descriptor to stage (the winning path) or the quarantine record to stage ALONE (the layer-2
/// conflict path); the port stages, saves and commits accordingly.
/// </summary>
public delegate ValueTask<SpatialFrameMintStaging> SpatialFrameMintCallback(
    SpatialFrameMintContext context, CancellationToken ct);

/// <summary>The staging decision a mint callback returns (exactly one of the two is non-null).</summary>
public sealed record SpatialFrameMintStaging
{
    private SpatialFrameMintStaging(SpatialFrameDescriptor? descriptor, SpatialFrameQuarantineRecord? quarantine)
    {
        Descriptor = descriptor;
        Quarantine = quarantine;
    }

    /// <summary>The descriptor to stage (winning path), or null on the conflict path.</summary>
    public SpatialFrameDescriptor? Descriptor { get; }

    /// <summary>The quarantine record to stage ALONE (layer-2 conflict path), or null.</summary>
    public SpatialFrameQuarantineRecord? Quarantine { get; }

    /// <summary>Stage the minted descriptor.</summary>
    public static SpatialFrameMintStaging Mint(SpatialFrameDescriptor descriptor) =>
        new(descriptor ?? throw new ArgumentNullException(nameof(descriptor)), null);

    /// <summary>Stage ONLY the quarantine row — the descriptor is not persisted (0168 D2-A6).</summary>
    public static SpatialFrameMintStaging Conflict(SpatialFrameQuarantineRecord quarantine) =>
        new(null, quarantine ?? throw new ArgumentNullException(nameof(quarantine)));
}
