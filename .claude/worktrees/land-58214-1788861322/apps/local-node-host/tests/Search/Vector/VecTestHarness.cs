using System;
using System.Threading.Tasks;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Recovery.Crypto;
using Harborline.Api.Foundation.Recovery;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.Foundation.Recovery.TenantKey;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Data.Search.Vector.BruteForce;
using Harborline.Api.LocalNodeHost.Data.Authorization;

namespace Harborline.Api.LocalNodeHost.Tests.Search.Vector;

/// <summary>
/// Test harness for the KG-search Slice 1b secure vector layer — stands up a REAL SQLCipher-encrypted search
/// store (vec_rows + grants + FTS) plus the per-subject crypto substrate (the real
/// <see cref="InMemoryTenantKeyProvider"/> + <see cref="SubjectKeyFieldEncryptor"/> /
/// <see cref="SubjectKeyFieldDecryptor"/> + <see cref="InMemorySubjectErasureRegistry"/>), so G-6 crypto-shred,
/// M1, the exact clip, and RRF are exercised against the actual encrypted index, not a fake.
/// </summary>
public sealed class VecTestHarness : IAsyncDisposable
{
    private VecTestHarness(SearchTestStore store, InMemorySubjectErasureRegistry erasure)
    {
        Store = store;
        Erasure = erasure;
        var tenantKeys = new InMemoryTenantKeyProvider();
        SubjectEncryptor = new SubjectKeyFieldEncryptor(tenantKeys, erasure);
        SubjectDecryptor = new SubjectKeyFieldDecryptor(
            tenantKeys, erasure, new SystemRecoveryClock(TimeProvider.System));
        KnnEngine = new BruteForceKnnEngine(SubjectDecryptor, NodeDecryptCapability.Factory);
    }

    /// <summary>The encrypted search store (vec_rows + grants + FTS migrated).</summary>
    public SearchTestStore Store { get; }

    /// <summary>The crypto-shred register (mark a subject erased to prove G-6).</summary>
    public InMemorySubjectErasureRegistry Erasure { get; }

    /// <summary>The real per-subject field encryptor (G-6) the indexer seals embeddings with.</summary>
    public ISubjectFieldEncryptor SubjectEncryptor { get; }

    /// <summary>The real per-subject field decryptor the brute-force KNN engine reads with.</summary>
    public ISubjectFieldDecryptor SubjectDecryptor { get; }

    /// <summary>The native-free brute-force KNN engine (this Intel CI host has no vec0 native).</summary>
    public BruteForceKnnEngine KnnEngine { get; }

    /// <summary>The durable EF grant store (G-2) over the same encrypted file as the index.</summary>
    public NodeEfGrantStore GrantStore => new(Store.Factory);

    /// <summary>The shared transaction-aware closure projection.</summary>
    public ClosureAuthorizedRecordSetProjection ScopeResolver => new();

    /// <summary>Create a harness (a fresh encrypted store per instance — the per-tenant file boundary).</summary>
    public static async Task<VecTestHarness> CreateAsync(byte keySalt = 11)
    {
        var store = await SearchTestStore.CreateAsync(keySalt);
        await using (var context = store.CreateContext())
        {
            const string definitionId = "10000000-0000-0000-0000-000000000001";
            context.AuthorizationDefinitions.Add(new AuthorizationDefinitionRow
            {
                DefinitionId = definitionId,
                Revision = 1,
                PublisherPackageId = "test",
                Operation = TeamRolePermissions.RecordsRead,
                ScopeType = (int)ScopeExpression.Parse("/").Type,
                ScopeValue = "/",
            });
            context.AuthorizationOfferedRoles.Add(new AuthorizationOfferedRoleRow
            {
                DefinitionId = definitionId,
                Revision = 1,
                Vocabulary = TestSearchAuthorization.RecordsReader.Vocabulary,
                RoleName = TestSearchAuthorization.RecordsReader.Name,
            });
            await context.SaveChangesAsync();
        }
        return new VecTestHarness(store, new InMemorySubjectErasureRegistry());
    }

    /// <summary>
    /// Build a vector indexer over this harness with a deterministic stub provider. <paramref name="allowStub"/>
    /// must be true to admit the self-identifying stub model (the M1 test escape); a production indexer leaves it
    /// false and rejects the stub.
    /// </summary>
    public NodeVecIndexer Indexer(bool allowStub = true, int dimension = 64) =>
        new(
            Store.Factory,
            new StubKgEmbeddingProvider(dimension),
            SubjectEncryptor,
            accelerationSink: null,
            allowStubModel: allowStub);

    /// <summary>A vector indexer with NO embedding provider — the fail-closed (provider.kg_floor_unavailable) host.</summary>
    public NodeVecIndexer IndexerNoProvider() =>
        new(Store.Factory, embedder: null, SubjectEncryptor, accelerationSink: null, allowStubModel: false);

    /// <summary>
    /// A vector indexer wired to a <paramref name="sink"/> simulating the derived cleartext <c>vec0</c>
    /// acceleration table (the partially-invertible cache F1 must purge on shred — the real native is
    /// host-gated, so the F1 erasure-flow wire is proven against this recording sink).
    /// </summary>
    public NodeVecIndexer IndexerWithSink(IVecAccelerationSink sink, bool allowStub = true, int dimension = 64) =>
        new(Store.Factory, new StubKgEmbeddingProvider(dimension), SubjectEncryptor, sink, allowStub);

    /// <summary>The deterministic stub embedder used to embed query text in read tests.</summary>
    public StubKgEmbeddingProvider StubEmbedder(int dimension = 64) => new(dimension);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Store.DisposeAsync();
}
