using System;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Recovery.Crypto;
using Harborline.Api.Foundation.Recovery;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.Foundation.Recovery.TenantKey;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Data.Search.Vector.BruteForce;
using Harborline.Api.LocalNodeHost.Data.Search.Vector.Sqlite;
using Harborline.Api.LocalNodeHost.Tests.Identity;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Search.Vector;

/// <summary>
/// Verifies the Slice 1b DI composition (<see cref="NodeVecSearchComposition.AddNodeKnowledgeGraphVectorSearch"/>)
/// wires the security-load-bearing pieces correctly: the durable EF grant store REPLACES the in-memory stopgap
/// (G-2), the native-free brute-force engine is selected without the explicit real-vec opt-in, and the M1
/// fail-closed posture holds when no real provider is registered.
/// </summary>
[Collection(Vec0NativeEnvironmentCollection.Name)]
public sealed class VecCompositionTests
{
    private static ServiceProvider BuildProvider(IKgEmbeddingProvider? provider, bool allowStub)
    {
        var services = new ServiceCollection();

        // A throwaway in-memory EF context factory just to satisfy the indexer/engine ctors (the composition
        // test does not run a real DB scan — it asserts the wiring).
        services.AddDbContextFactory<NodeLocalSearchDbContext>(opt =>
            opt.UseSqlite("Data Source=:memory:"));

        // The Slice-0 in-memory grant store is registered FIRST (the stopgap the composition must replace).
        services.AddSingleton<IGrantStore, InMemoryGrantStore>();
        services.AddSingleton<IAuthorizationClosureReader>(new FixedAuthorizationClosure());

        // The per-subject crypto substrate the indexer + engine depend on (real types, dev key provider).
        var tenantKeys = new InMemoryTenantKeyProvider();
        var erasure = new InMemorySubjectErasureRegistry();
        services.AddSingleton<ISubjectErasureRegistry>(erasure);
        services.AddSingleton<ISubjectFieldEncryptor>(new SubjectKeyFieldEncryptor(tenantKeys, erasure));
        services.AddSingleton<ISubjectFieldDecryptor>(new SubjectKeyFieldDecryptor(
            tenantKeys, erasure, new SystemRecoveryClock(TimeProvider.System)));

        services.AddNodeKnowledgeGraphVectorSearch(provider, allowStub);
        return services.BuildServiceProvider();
    }

    [Fact(DisplayName = "composition: AddNodeKnowledgeGraphVectorSearch REPLACES the in-memory grant store with the durable NodeEfGrantStore (G-2)")]
    public void Composition_Replaces_GrantStore_With_NodeEf()
    {
        using var sp = BuildProvider(new StubKgEmbeddingProvider(64), allowStub: true);
        var grantStore = sp.GetRequiredService<IGrantStore>();
        Assert.IsType<NodeEfGrantStore>(grantStore);
    }

    [Fact(DisplayName = "composition: the G-2 same-transaction scope resolver is registered")]
    public void Composition_Registers_The_Transactional_Projection()
    {
        using var sp = BuildProvider(new StubKgEmbeddingProvider(64), allowStub: true);
        Assert.IsType<ClosureAuthorizedRecordSetProjection>(
            sp.GetRequiredService<IAuthorizedRecordSetProjection>());
    }

    [Fact(DisplayName = "composition: a host without the real-vec opt-in selects the native-free brute-force KNN engine")]
    public void Composition_Selects_BruteForce_When_Native_Is_Not_Opted_In()
    {
        var previous = Environment.GetEnvironmentVariable(Vec0Native.RealVecEnvVar);
        Environment.SetEnvironmentVariable(Vec0Native.RealVecEnvVar, null);
        try
        {
            // The staged native remains inert until the explicit real-vec opt-in is present.
            using var sp = BuildProvider(new StubKgEmbeddingProvider(64), allowStub: true);
            Assert.IsType<BruteForceKnnEngine>(sp.GetRequiredService<IVecKnnEngine>());
        }
        finally
        {
            Environment.SetEnvironmentVariable(Vec0Native.RealVecEnvVar, previous);
        }
    }

    [Fact(DisplayName = "composition: with NO embedding provider, the indexer is still registered but its ingest fails closed (provider.kg_floor_unavailable)")]
    public async Task Composition_No_Provider_Indexer_Fails_Closed()
    {
        await using var h = await VecTestHarness.CreateAsync();
        // Build a real (file-backed) indexer with no provider via the harness to actually exercise the throw.
        var indexer = h.IndexerNoProvider();
        var ex = await Assert.ThrowsAsync<KgFloorUnavailableException>(() =>
            indexer.IndexRecordAsync("inv-1", "tenant-A", "subj-1", "rent", SearchResidency.Cache));
        Assert.Contains(KgFloorUnavailableException.ReasonCode, ex.Message);
    }

    // ── Slice 1d wire-live ──────────────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "wire-live: the indexer + read service are registered + resolvable end-to-end when a provider is wired")]
    public void WireLive_Indexer_And_ReadService_Resolve()
    {
        using var sp = BuildProvider(new StubKgEmbeddingProvider(64), allowStub: true);
        // The indexer resolves (write side) and the read service resolves (read side) — the layer is reachable.
        Assert.NotNull(sp.GetRequiredService<NodeVecIndexer>());
        Assert.NotNull(sp.GetRequiredService<NodeVecSearchReadService>());
    }

    [Fact(DisplayName = "wire-live F1: the KgVecIndexSubjectErasurePropagator is registered into the erasure-flow propagator collection")]
    public void WireLive_F1_Propagator_Is_Registered()
    {
        using var sp = BuildProvider(new StubKgEmbeddingProvider(64), allowStub: true);
        // The erasure flow resolves IEnumerable<ISubjectErasurePropagator>; the KG propagator MUST be in it, or a
        // crypto-shred would leave the cleartext vec0 residue (the F1 gap). This is the composition-side proof
        // that the F1 wire is present (the functional purge proof is in VecErasureFlowF1Tests).
        var propagators = sp.GetServices<ISubjectErasurePropagator>();
        Assert.Contains(propagators, p => p is KgVecIndexSubjectErasurePropagator);
    }

    [Fact(DisplayName = "wire-live: with no provider, the read service is NOT registered (no real query surface), but the indexer + F1 propagator still are")]
    public void WireLive_No_Provider_Has_Indexer_And_Propagator_But_No_ReadService()
    {
        using var sp = BuildProvider(provider: null, allowStub: false);
        Assert.NotNull(sp.GetRequiredService<NodeVecIndexer>());
        Assert.Contains(sp.GetServices<ISubjectErasurePropagator>(), p => p is KgVecIndexSubjectErasurePropagator);
        // No provider ⇒ no query embedder ⇒ the read service is not registered (a query has nothing to embed with).
        Assert.Null(sp.GetService<NodeVecSearchReadService>());
    }

    [Fact(DisplayName = "legacy migration provider is install-secret and refuses application reads for an erased subject")]
    public async Task WireLive_RootSeedKeyProvider_Is_Install_Secret_And_Shred_Fails_Closed()
    {
        var tenant = Harborline.Api.Foundation.Assets.Common.TenantId.FromString("tenant-A");
        var subject = new SubjectId("subject-1");

        var seedA = new byte[32];
        var seedB = new byte[32];
        for (var i = 0; i < 32; i++) { seedA[i] = (byte)i; seedB[i] = (byte)(255 - i); }

        var provA = new RootSeedTenantKeyProvider(seedA);
        var provB = new RootSeedTenantKeyProvider(seedB);
        var stub = new InMemoryTenantKeyProvider();

        var keyA = (await provA.DeriveKeyAsync(tenant, "encrypted-field-aes", default)).ToArray();
        var keyB = (await provB.DeriveKeyAsync(tenant, "encrypted-field-aes", default)).ToArray();
        var keyStub = (await stub.DeriveKeyAsync(tenant, "encrypted-field-aes", default)).ToArray();

        // Different root seeds ⇒ different DEKs (install-secret), and NEITHER equals the dev-stub's shared-salt key.
        Assert.False(keyA.AsSpan().SequenceEqual(keyB));
        Assert.False(keyA.AsSpan().SequenceEqual(keyStub));

        // Crypto-shred fail-closed is preserved: a shredded subject's sub-key is un-derivable.
        var erasure = new InMemorySubjectErasureRegistry();
        await erasure.MarkErasedAsync(tenant, subject, default);
        await Assert.ThrowsAsync<SubjectErasedException>(() =>
            provA.DeriveSubjectKeyAsync(tenant, subject, "encrypted-field-aes", erasure, default));
    }
}
