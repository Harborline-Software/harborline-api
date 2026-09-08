using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.Assets.Registry.Services.Spatial;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;
using Harborline.Api.LocalNodeHost.Health;


namespace Harborline.Api.LocalNodeHost.Data.AssetRegistry;

/// <summary>
/// The host <see cref="IFrameEpochAuthority"/> (ADR 0101 Rev 3.2 [A10]; ADR 0168 D2-A4(a)):
/// grants mint ONLY on a positively-asserted home claim — a <see cref="HomeEpochRecord"/> for the
/// tenant whose <c>HomeDeviceId</c> is this node's Ed25519 signing principal. Absence of any home
/// record is refusal, not a grant.
/// </summary>
/// <remarks>
/// This is <b>defence in depth — a pre-flight refusal surface, never the enforcing read</b>. The
/// enforcing check is <see cref="NodeEfSpatialFrameDescriptorPort"/>'s in-transaction re-read of
/// the same tip inside <c>HomeEpochFenceTransaction.RunAsync</c> (0168 D2-A4(b)); this pre-flight
/// merely refuses cheaply before a transaction is opened.
/// </remarks>
public sealed class NodeHomeClaimFrameEpochAuthority : IFrameEpochAuthority
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;
    private readonly string _deviceId;

    /// <summary>Wires the authority over the local-node store and the node signing principal.</summary>
    public NodeHomeClaimFrameEpochAuthority(
        IDbContextFactory<LocalNodeDbContext> contextFactory,
        NodePrincipalSigner nodeSigner)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        ArgumentNullException.ThrowIfNull(nodeSigner);
        _deviceId = nodeSigner.NodePublicKey;
    }

    /// <inheritdoc />
    public async Task AssertMintAuthorityAsync(TenantId tenant, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var home = await ctx.Set<HomeEpochRecord>()
            .AsNoTracking()
            .Where(r => r.TenantId == tenant.Value)
            .OrderByDescending(r => r.EpochNumber)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (home is null)
        {
            throw new FrameEpochMintRefusedException(
                tenant.Value,
                "no HomeEpochRecord exists for the tenant — absence of home state is refusal, "
                + "not a grant (ADR 0168 D2-A4(a)).");
        }

        if (!string.Equals(home.HomeDeviceId, _deviceId, StringComparison.Ordinal))
        {
            throw new FrameEpochMintRefusedException(
                tenant.Value,
                $"the tenant's home device as of home epoch {home.EpochNumber} is not this node.");
        }
    }
}
