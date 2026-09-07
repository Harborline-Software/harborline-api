using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Packs.Install;

namespace Harborline.Api.Foundation.Packs.Graph;

/// <summary>
/// Holds the rebuildable content-edge-index (design note §2.2) and rebuilds it from durable install state.
/// The index is a CACHE keyed by install-state version, never a source of truth: a consumer that reads a
/// stale index gets a transparently-rebuilt one, and the index can be dropped at any point and rebuilt to
/// the identical result. Two entry points, mirroring the note: the seed projector calls <see cref="Rebuild"/>
/// as a side-effect of projection (warming the cache); a reader calls <see cref="GetOrRebuild"/> which
/// self-heals if the cached fingerprint no longer matches live install state (so correctness never depends
/// on the projector having run).
/// </summary>
public interface IPackContentEdgeIndexProvider
{
    /// <summary>
    /// Returns the content-edge-index for the tenant, rebuilding it iff the cached fingerprint no longer
    /// matches the live install state (self-healing — a reader is always current without a projector pass).
    /// </summary>
    PackContentEdgeIndex GetOrRebuild(TenantId tenant);

    /// <summary>
    /// Forces a rebuild from live install state and caches it (the seed projector's side-effect of
    /// projection). Returns the freshly built index.
    /// </summary>
    PackContentEdgeIndex Rebuild(TenantId tenant);
}

/// <summary>
/// In-memory <see cref="IPackContentEdgeIndexProvider"/> — a per-tenant cache of the last-built
/// content-edge-index. Rebuilds from <see cref="IPackInstallStore"/> (the single source of truth); thread
/// safe (the node serves the graph route + projects concurrently). The cache is process-lifetime: a restart
/// simply rebuilds on first read (safe — it is derived state), and the durable pack store's boot
/// re-projection warms it.
/// </summary>
public sealed class InMemoryPackContentEdgeIndexProvider : IPackContentEdgeIndexProvider
{
    private readonly IPackInstallStore _store;
    private readonly object _gate = new();
    private readonly Dictionary<TenantId, PackContentEdgeIndex> _byTenant = new();

    /// <summary>Constructs the provider over the install-state store it derives from.</summary>
    public InMemoryPackContentEdgeIndexProvider(IPackInstallStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <inheritdoc />
    public PackContentEdgeIndex GetOrRebuild(TenantId tenant)
    {
        var installed = _store.ListInstalled(tenant);
        var fingerprint = PackInstallStateFingerprint.Compute(installed);

        lock (_gate)
        {
            if (_byTenant.TryGetValue(tenant, out var cached)
                && string.Equals(cached.InstallStateFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return cached;
            }

            var rebuilt = PackContentEdgeIndexBuilder.Build(installed);
            _byTenant[tenant] = rebuilt;
            return rebuilt;
        }
    }

    /// <inheritdoc />
    public PackContentEdgeIndex Rebuild(TenantId tenant)
    {
        var rebuilt = PackContentEdgeIndexBuilder.Build(_store.ListInstalled(tenant));
        lock (_gate)
        {
            _byTenant[tenant] = rebuilt;
        }

        return rebuilt;
    }
}
