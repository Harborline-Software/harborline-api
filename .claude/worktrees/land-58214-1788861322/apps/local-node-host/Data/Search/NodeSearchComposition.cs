using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harborline.Api.LocalNodeHost.Data.Search;

/// <summary>
/// DI composition for the local-first knowledge-graph "360-view" search (ADR 0135 KG-search F3-lift
/// amendment, Slice 0). Registers the fail-closed clip projection (G-1) as the SOLE producer of the query
/// <c>WHERE</c>, the clipped read service, and the residency-enforcing indexer (G-3).
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="NodeLocalSearchDbContext"/> factory itself is registered by
/// <c>LocalNodeSqlCipherRegistration</c> (so it shares the SAME SQLCipher-encrypted file + interceptor as
/// every other node-local store — SC-1), exactly like the comms / roster / admission contexts. This method
/// registers only the services layered on top of that context.
/// </para>
/// <para>
/// <b>G-1 wiring discipline.</b> The clip is bound to the SHIPPED
/// <see cref="ClosureAuthorizedRecordSetProjection"/> over the materialised authorization closure — the search read service
/// has no constructor overload that omits the clip, and the only registered
/// <see cref="IAuthorizedRecordSetProjection"/> is the closure-backed one. There is no "no-clip" registration.
/// </para>
/// </remarks>
public static class NodeSearchComposition
{
    /// <summary>
    /// Registers the KG-search Slice 0 services: the fail-closed clip projection (G-1), the clipped read
    /// service, and the residency-enforcing indexer (G-3). The grant store
    /// (<c>Harborline.Api.Blocks.AccessGrant.IGrantStore</c>) MUST already be registered (it is, by the grant
    /// module's composition) — the clip resolves over it.
    /// </summary>
    /// <param name="services">The host service collection.</param>
    /// <returns>The same collection for chaining.</returns>
    public static IServiceCollection AddNodeKnowledgeGraphSearch(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // G-1: the ONLY registered clip is the fail-closed closure-backed projection — the sole producer
        // of the query WHERE. No "no-clip" alternative is registered.
        services.TryAddSingleton<IAuthorizedRecordSetProjection, ClosureAuthorizedRecordSetProjection>();

        services.TryAddSingleton<NodeSearchReadService>();
        services.TryAddSingleton<NodeSearchIndexer>();

        return services;
    }
}
