
namespace Harborline.Api.Blocks.Assets.Registry.Services.Spatial;

/// <summary>
/// The CP-5 epoch-mint authority seam (ADR 0101 Rev 3.2 [A10]; ADR 0168 D2-A4(a)). Declared in the
/// registry package; the DEFAULT registration <b>refuses</b>. A grant requires a positively
/// asserted home claim — a <c>HomeEpochRecord</c> for the tenant whose <c>HomeDeviceId</c> is this
/// device. Absence of any home record is refusal, not a grant.
/// </summary>
/// <remarks>
/// This seam is <b>defence in depth — a pre-flight refusal surface, never the enforcing read</b>.
/// The enforcing check is the port's in-transaction re-read of the tenant's home-record tip inside
/// the fenced transaction (0168 D2-A4(b)); <c>HomeEpochFence.AssertNotStaleAsync</c> is additive on
/// top of that re-read, not a substitute for it.
/// </remarks>
public interface IFrameEpochAuthority
{
    /// <summary>
    /// Throws <see cref="FrameEpochMintRefusedException"/> unless this device holds a positively
    /// asserted home claim for <paramref name="tenant"/>.
    /// </summary>
    Task AssertMintAuthorityAsync(TenantId tenant, CancellationToken ct = default);
}

/// <summary>
/// The package DEFAULT <see cref="IFrameEpochAuthority"/>: it refuses every mint. "Grant when no
/// home record exists" would grant mint to every device in every install unconditionally (the
/// first-draft inversion the OQ-1 ruling struck); a host that can actually decide a home claim
/// replaces this registration with its host implementation ([A10] — <c>services.Replace</c> in a
/// named host extension, asserted by the composition hard-fail).
/// </summary>
public sealed class RefusingFrameEpochAuthority : IFrameEpochAuthority
{
    /// <inheritdoc />
    public Task AssertMintAuthorityAsync(TenantId tenant, CancellationToken ct = default) =>
        throw new FrameEpochMintRefusedException(
            tenant.Value,
            "the default frame-epoch authority refuses every mint — no host home-claim "
            + "implementation is registered (ADR 0101 Rev 3.2 [A10], fail-closed).");
}

/// <summary>
/// A fail-closed mint refusal (ADR 0168 D2-A4(a)): no positively-asserted home claim for the
/// tenant on this device. Distinct from <see cref="SpatialFrameEpochMintRejectedException"/> — a
/// refusal precedes any staging and leaves no artifact; a rejection quarantines.
/// </summary>
public sealed class FrameEpochMintRefusedException : Exception
{
    /// <summary>The tenant the refused mint targeted.</summary>
    public string TenantId { get; }

    /// <summary>Constructs the refusal.</summary>
    public FrameEpochMintRefusedException(string tenantId, string reason)
        : base($"Frame-epoch mint refused for tenant '{tenantId}': {reason}")
    {
        TenantId = tenantId;
    }
}
