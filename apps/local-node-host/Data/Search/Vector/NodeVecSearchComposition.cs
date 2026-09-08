using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Recovery.Crypto;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.LocalNodeHost.Data.Search.Vector.BruteForce;
using Harborline.Api.LocalNodeHost.Data.Search.Vector.Sqlite;

namespace Harborline.Api.LocalNodeHost.Data.Search.Vector;

/// <summary>
/// DI composition for the KG-search secure vector layer (ADR 0135 KG-search F3-lift amendment, Slice 1b).
/// Registers, under the G-1..G-6 + M1 gates:
/// <list type="bullet">
///   <item>the durable EF grant store (<see cref="NodeEfGrantStore"/>) as the canonical <see cref="IGrantStore"/>
///     (G-2) — replacing the in-memory stopgap — plus the shared same-transaction closure projection;</item>
///   <item>the M1-fenced embedding provider (the real capability-wired provider is Slice 1d; a host with no provider
///     fails closed at ingest, never indexing an unverifiable artifact);</item>
///   <item>the KNN engine — the real <c>vec0</c> path when the native is opted-in + present, else the
///     native-free brute-force engine (identical exact-clip + per-subject-decrypt semantics);</item>
///   <item>the per-subject-encrypting vector indexer (G-3 + G-5 + G-6) and the clipped hybrid read service (RRF).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>G-2 grant-store override.</b> This registers <see cref="NodeEfGrantStore"/> as the <see cref="IGrantStore"/>
/// via <see cref="ServiceCollectionDescriptorExtensions.Replace"/> (the search read paths must resolve grants
/// from the durable file-co-located store so the grant read shares the scan transaction). The recovery crypto
/// substrate (<c>AddHarborlineRecoveryCoordinator</c>) MUST already be registered — the indexer resolves
/// <see cref="ISubjectFieldEncryptor"/> + the read engine resolves <see cref="ISubjectFieldDecryptor"/> from it.
/// </para>
/// <para>
/// <b>M1 fail-closed when no provider.</b> <paramref name="embeddingProvider"/> may be null (no real floor on
/// this host). The indexer is then constructed with a null provider, so its ingest path throws
/// <see cref="KgFloorUnavailableException"/> rather than indexing anything — a production host without a real
/// floor does NOT silently index stubs.
/// </para>
/// </remarks>
public static class NodeVecSearchComposition
{
    /// <summary>
    /// Registers the Slice 1b secure vector layer.
    /// </summary>
    /// <param name="services">The host service collection.</param>
    /// <param name="embeddingProvider">
    /// The M1-fenced embedding provider, or null (a host with no real floor — ingest then fails closed).
    /// </param>
    /// <param name="allowStubModel">TEST-ONLY — admit the self-identifying stub model to the index (default false; production leaves it false).</param>
    public static IServiceCollection AddNodeKnowledgeGraphVectorSearch(
        this IServiceCollection services,
        IKgEmbeddingProvider? embeddingProvider = null,
        bool allowStubModel = false)
    {
        ArgumentNullException.ThrowIfNull(services);

        // G-2: the durable, file-co-located EF grant store IS the canonical IGrantStore. Replace any prior
        // in-memory registration so every grant read (incl. the KG clip's) goes through the recoverable store.
        services.Replace(ServiceDescriptor.Singleton<IGrantStore, NodeEfGrantStore>());

        // Both FTS and vector reads use the same transaction-aware materialised-closure projection.
        services.TryAddSingleton<IAuthorizedRecordSetProjection, ClosureAuthorizedRecordSetProjection>();

        // The KNN engine — real vec0 when opted-in + native present, else the native-free brute-force engine.
        services.TryAddSingleton<IVecKnnEngine>(sp =>
        {
            if (Vec0Native.RealVecOptedIn() && Vec0Native.ResolveNativePath() is not null)
            {
                return new Vec0KnnEngine(KgModelFloor.BgeM3.Dimension);
            }
            var decryptor = sp.GetRequiredService<ISubjectFieldDecryptor>();
            return new BruteForceKnnEngine(decryptor, NodeDecryptCapability.Factory);
        });

        // The vec0 cleartext acceleration sink — only when the real vec0 path is active (else null = no sink).
        if (Vec0Native.RealVecOptedIn() && Vec0Native.ResolveNativePath() is not null)
        {
            services.TryAddSingleton<IVecAccelerationSink, Vec0AccelerationSink>();
        }

        // The per-subject-encrypting indexer (G-3 + G-5 + G-6 + M1).
        services.TryAddSingleton(sp => new NodeVecIndexer(
            sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<NodeLocalSearchDbContext>>(),
            embeddingProvider,
            sp.GetRequiredService<ISubjectFieldEncryptor>(),
            sp.GetService<IVecAccelerationSink>(),
            allowStubModel));

        // F1 (Slice-1d, BINDING GDPR wire) — register the subject-erasure propagator that purges this
        // index's per-subject rows + their derived cleartext vec0 residue on a crypto-shred. AddSingleton
        // (not TryAdd) so it joins the IEnumerable<ISubjectErasurePropagator> the recovery erasure flow
        // resolves; without this, NodeVecIndexer.PurgeSubjectAsync has no erasure-flow caller (the gap the
        // Slice-1b deep-review flagged as F1) and a real-vec0 host would leave cleartext residue after a shred.
        services.AddSingleton<ISubjectErasurePropagator>(sp =>
            new KgVecIndexSubjectErasurePropagator(sp.GetRequiredService<NodeVecIndexer>()));

        // The clipped hybrid (dense + lexical → RRF) read service.
        if (embeddingProvider is not null)
        {
            services.TryAddSingleton(sp => new NodeVecSearchReadService(
                sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<NodeLocalSearchDbContext>>(),
                sp.GetRequiredService<IAuthorizedRecordSetProjection>(),
                embeddingProvider,
                sp.GetRequiredService<IVecKnnEngine>()));
        }

        return services;
    }
}

/// <summary>
/// Mints the node's per-tenant decrypt capability for the local KG vector read (the node is the authorized local
/// reader — it holds the file DEK). A long-lived <see cref="FixedDecryptCapability"/> scoped to the query tenant.
/// </summary>
public static class NodeDecryptCapability
{
    /// <summary>The factory the brute-force engine calls — a tenant-scoped capability valid far in the future.</summary>
    public static IDecryptCapability Factory(Harborline.Foundation.Assets.Common.TenantId tenant) =>
        new FixedDecryptCapability(
            capabilityId: "node-kg-vector-read",
            actor: new Harborline.Api.Foundation.Assets.Common.ActorId("node-local-reader"),
            tenant: tenant,
            validUntil: DateTimeOffset.MaxValue);
}
