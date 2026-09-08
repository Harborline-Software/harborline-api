using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Data.Sqlite;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Search.Vector;

/// <summary>
/// RRF fusion, binary quantization, multilingual, and the hybrid (dense + lexical → RRF) read service tests for
/// KG-search Slice 1b (ADR 0135 KG-search F3-lift amendment).
/// </summary>
public sealed class VecRrfAndQuantTests
{
    private const int Dim = 64;
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);
    private static readonly TenantId TenantA = TenantId.FromString("tenant-A");
    private static readonly ActorId Alice = new("alice");

    private static AccessGrant WholeTenantGrant() =>
        TestSearchAuthorization.Grant(TenantA, Alice, ScopeExpression.Parse("/"), Now);

    // ── RRF fusion (unit) ─────────────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "RRF: a record near the top of BOTH lists outranks one near the top of only ONE")]
    public void Rrf_Fuses_By_Rank()
    {
        // dense: [x, a, b]; lexical: [y, a, c]. 'a' is rank 1 in both; x/y rank 0 in one each.
        var dense = new[] { "x", "a", "b" };
        var lexical = new[] { "y", "a", "c" };

        var fused = Rrf.Fuse(new[] { dense, lexical });

        // 'a' appears in both (1/(60+1) + 1/(60+1)); x and y each appear once at rank 0 (1/(60+0)).
        // 1/61 + 1/61 = 0.0328 > 1/60 = 0.0167 — 'a' wins.
        Assert.Equal("a", fused[0]);
    }

    [Fact(DisplayName = "RRF: an empty leg is ignored; the other leg's order is preserved")]
    public void Rrf_Tolerates_Empty_Leg()
    {
        var dense = new[] { "a", "b", "c" };
        var fused = Rrf.Fuse(new[] { dense, Array.Empty<string>() });
        Assert.Equal(new[] { "a", "b", "c" }, fused.ToArray());
    }

    // ── Binary quantization (unit) ────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "binary-quant: sign-bit packing + Hamming distance is interactive and correct (identical codes ⇒ distance 0)")]
    public void BinaryQuant_Pack_And_Hamming()
    {
        var a = new float[] { 0.5f, -0.2f, 0.9f, -0.1f, 0.0f, 0.3f, -0.7f, 0.4f };
        var b = (float[])a.Clone();
        var packedA = BinaryQuantization.Pack(a);
        var packedB = BinaryQuantization.Pack(b);
        Assert.Equal(0, BinaryQuantization.Hamming(packedA, packedB));

        // Flip one sign ⇒ exactly one differing bit.
        b[0] = -b[0]; // 0.5 → -0.5 crosses the sign threshold.
        Assert.Equal(1, BinaryQuantization.Hamming(packedA, BinaryQuantization.Pack(b)));
    }

    [Fact(DisplayName = "binary-quant: a 1024-dim vector packs to 128 bytes (24x smaller than 4096-byte float32) — the scale unlock")]
    public void BinaryQuant_Is_24x_Smaller()
    {
        var v = Enumerable.Range(0, 1024).Select(i => (float)(i % 2 == 0 ? 1 : -1)).ToArray();
        var packed = BinaryQuantization.Pack(v);
        Assert.Equal(128, packed.Length); // 1024 bits / 8 = 128 bytes vs 1024*4 = 4096 bytes float32.
    }

    [Fact(DisplayName = "binary-quant: differing-length codes are a hard fault (a dimension/model mismatch — G-5)")]
    public void BinaryQuant_Length_Mismatch_Faults()
    {
        Assert.Throws<ArgumentException>(() =>
            BinaryQuantization.Hamming(new byte[16], new byte[8]));
    }

    // ── hybrid read service (dense + lexical → RRF) ───────────────────────────────────────────────────────

    [Fact(DisplayName = "hybrid: the clipped dense + lexical legs fuse to a single RRF ranking of ONLY authorized records")]
    public async Task Hybrid_Fuses_Dense_And_Lexical_Clipped()
    {
        await using var h = await VecTestHarness.CreateAsync();

        // Seed BOTH the vector index (dense leg) and the FTS/search_nodes index (lexical leg) for two records.
        var vecIndexer = h.Indexer(allowStub: true, dimension: Dim);
        await vecIndexer.IndexRecordAsync("inv-1", "tenant-A", "subj-1", "acme rent invoice unit four", SearchResidency.Cache);
        await vecIndexer.IndexRecordAsync("inv-2", "tenant-A", "subj-2", "beta rent invoice unit nine", SearchResidency.Cache);

        var ftsIndexer = new NodeSearchIndexer(h.Store.Factory);
        await ftsIndexer.IndexNodeAsync(new SearchNodeRow
        {
            RecordId = "inv-1", TenantId = "tenant-A", NodeType = "invoice",
            Title = "Acme rent invoice", Body = "acme rent invoice unit four", Residency = SearchResidency.Cache,
        });
        await ftsIndexer.IndexNodeAsync(new SearchNodeRow
        {
            RecordId = "inv-2", TenantId = "tenant-A", NodeType = "invoice",
            Title = "Beta rent invoice", Body = "beta rent invoice unit nine", Residency = SearchResidency.Cache,
        });

        await h.GrantStore.SaveAsync(TenantA, WholeTenantGrant(), 0);

        var svc = new NodeVecSearchReadService(
            h.Store.Factory, h.ScopeResolver, h.StubEmbedder(Dim), h.KnnEngine);

        var hits = await svc.HybridSearchAsync(TenantA, Alice, "acme rent invoice unit four", Now);

        // Both records returned (whole-tenant grant), fused; inv-1 (exact text match in BOTH legs) ranks first.
        Assert.NotEmpty(hits);
        Assert.Equal("inv-1", hits[0].RecordId);
        Assert.Contains(hits, x => x.RecordId == "inv-1" && x.DenseRank is not null && x.LexicalRank is not null);
    }

    [Fact(DisplayName = "hybrid: a principal with NO grant gets an empty fused result (fail-closed across both legs)")]
    public async Task Hybrid_No_Grant_Is_Empty()
    {
        await using var h = await VecTestHarness.CreateAsync();
        await h.Indexer(allowStub: true, dimension: Dim)
            .IndexRecordAsync("inv-1", "tenant-A", "subj-1", "rent", SearchResidency.Cache);

        var svc = new NodeVecSearchReadService(
            h.Store.Factory, h.ScopeResolver, h.StubEmbedder(Dim), h.KnnEngine);
        Assert.Empty(await svc.HybridSearchAsync(TenantA, Alice, "rent", Now));
    }

    [Fact(DisplayName = "hybrid ranking: unreadable corpus content cannot steer the authorized page")]
    public async Task Hybrid_Ranking_Is_Identical_When_Only_Unauthorized_Content_Changes()
    {
        await using var alphaHeavy = await VecTestHarness.CreateAsync(keySalt: 21);
        await using var betaHeavy = await VecTestHarness.CreateAsync(keySalt: 22);
        await SeedInvertedCorpusAsync(alphaHeavy, "alpha");
        await SeedInvertedCorpusAsync(betaHeavy, "beta");

        var alphaPage = await LexicalOnlyService(alphaHeavy)
            .HybridSearchAsync(TenantA, Alice, "alpha beta", Now, limit: 2);
        var betaPage = await LexicalOnlyService(betaHeavy)
            .HybridSearchAsync(TenantA, Alice, "alpha beta", Now, limit: 2);

        Assert.Equal(new[] { "auth-a", "auth-b" }, alphaPage.Select(hit => hit.RecordId).ToArray());
        Assert.Equal(alphaPage.Select(hit => hit.RecordId), betaPage.Select(hit => hit.RecordId));
    }

    [Fact(DisplayName = "grounding ranking: unreadable corpus content cannot steer the authorized page")]
    public async Task Grounding_Ranking_Is_Identical_When_Only_Unauthorized_Content_Changes()
    {
        await using var alphaHeavy = await VecTestHarness.CreateAsync(keySalt: 23);
        await using var betaHeavy = await VecTestHarness.CreateAsync(keySalt: 24);
        await SeedInvertedCorpusAsync(alphaHeavy, "alpha");
        await SeedInvertedCorpusAsync(betaHeavy, "beta");

        var alphaPage = await LexicalOnlyService(alphaHeavy)
            .RetrieveClippedGroundingAsync(TenantA, Alice, "alpha beta", Now, limit: 2);
        var betaPage = await LexicalOnlyService(betaHeavy)
            .RetrieveClippedGroundingAsync(TenantA, Alice, "alpha beta", Now, limit: 2);

        Assert.Equal(new[] { "auth-a", "auth-b" }, alphaPage.Rows.Select(row => row.RecordId).ToArray());
        Assert.Equal(alphaPage.Rows.Select(row => row.RecordId), betaPage.Rows.Select(row => row.RecordId));
    }

    private static NodeVecSearchReadService LexicalOnlyService(VecTestHarness h) =>
        new(h.Store.Factory, h.ScopeResolver, h.StubEmbedder(Dim), new EmptyKnnEngine());

    private static async Task SeedInvertedCorpusAsync(VecTestHarness h, string hiddenTerm)
    {
        var fts = new NodeSearchIndexer(h.Store.Factory);
        await fts.IndexNodeAsync(new SearchNodeRow
        {
            RecordId = "auth-a", TenantId = "tenant-A", NodeType = "invoice",
            Title = "alpha alpha alpha alpha alpha beta", Body = string.Empty,
            Residency = SearchResidency.Cache,
        });
        await fts.IndexNodeAsync(new SearchNodeRow
        {
            RecordId = "auth-b", TenantId = "tenant-A", NodeType = "invoice",
            Title = "alpha beta beta beta beta beta", Body = string.Empty,
            Residency = SearchResidency.Cache,
        });
        for (var i = 0; i < 20; i++)
        {
            await fts.IndexNodeAsync(new SearchNodeRow
            {
                RecordId = $"hidden-{hiddenTerm}-{i}", TenantId = "tenant-A", NodeType = "invoice",
                Title = hiddenTerm, Body = string.Empty, Residency = SearchResidency.Cache,
            });
        }

        await h.GrantStore.AppendAsync(TenantA,
            TestSearchAuthorization.Grant(
                TenantA, Alice, ScopeExpression.Parse("/records/auth-a"), Now),
            "rrf-auth-a");
        await h.GrantStore.AppendAsync(TenantA,
            TestSearchAuthorization.Grant(
                TenantA, Alice, ScopeExpression.Parse("/records/auth-b"), Now),
            "rrf-auth-b");
    }

    private sealed class EmptyKnnEngine : IVecKnnEngine
    {
        public Task<IReadOnlyList<VecKnnHit>> KnnAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string tenantId,
            AuthorizedRecordScope scope,
            IReadOnlyList<float> queryVector,
            int k,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VecKnnHit>>(Array.Empty<VecKnnHit>());
    }

    // ── Reranker rescore (Slice 1d) ─────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "rerank: the cross-encoder reranker RE-ORDERS the RRF-fused result (a reversing reranker flips the order — proves the rescore is honored)")]
    public async Task Rerank_Reorders_The_Fused_Result()
    {
        await using var h = await VecTestHarness.CreateAsync();
        var vecIndexer = h.Indexer(allowStub: true, dimension: Dim);
        await vecIndexer.IndexRecordAsync("inv-1", "tenant-A", "subj-1", "acme rent invoice unit four", SearchResidency.Cache);
        await vecIndexer.IndexRecordAsync("inv-2", "tenant-A", "subj-2", "beta rent invoice unit nine", SearchResidency.Cache);

        var ftsIndexer = new NodeSearchIndexer(h.Store.Factory);
        await ftsIndexer.IndexNodeAsync(new SearchNodeRow
        {
            RecordId = "inv-1", TenantId = "tenant-A", NodeType = "invoice",
            Title = "Acme rent invoice", Body = "acme rent invoice unit four", Residency = SearchResidency.Cache,
        });
        await ftsIndexer.IndexNodeAsync(new SearchNodeRow
        {
            RecordId = "inv-2", TenantId = "tenant-A", NodeType = "invoice",
            Title = "Beta rent invoice", Body = "beta rent invoice unit nine", Residency = SearchResidency.Cache,
        });

        await h.GrantStore.SaveAsync(TenantA, WholeTenantGrant(), 0);

        // Baseline (NoOp reranker): inv-1 ranks first (exact text in both legs).
        var baseline = new NodeVecSearchReadService(
            h.Store.Factory, h.ScopeResolver, h.StubEmbedder(Dim), h.KnnEngine, reranker: null);
        var baseHits = await baseline.HybridSearchAsync(TenantA, Alice, "acme rent invoice unit four", Now);
        Assert.Equal("inv-1", baseHits[0].RecordId);

        // With a REVERSING reranker: the output order flips — proving the read service honors the rerank rescore
        // (the reranker re-orders the already-authorized, RRF-fused set; it is the final relevance stage).
        var reversing = new NodeVecSearchReadService(
            h.Store.Factory, h.ScopeResolver, h.StubEmbedder(Dim), h.KnnEngine, new ReversingReranker());
        var revHits = await reversing.HybridSearchAsync(TenantA, Alice, "acme rent invoice unit four", Now);

        Assert.Equal(baseHits.Select(x => x.RecordId).Reverse(), revHits.Select(x => x.RecordId));
        // The reranker only RE-ORDERS — the SET is identical to the clipped baseline (no record added/removed).
        Assert.Equal(
            baseHits.Select(x => x.RecordId).OrderBy(x => x, StringComparer.Ordinal),
            revHits.Select(x => x.RecordId).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact(DisplayName = "rerank: a reranker that tries to ADD a forbidden id cannot leak it — the read service re-applies the clip post-rerank")]
    public async Task Rerank_Cannot_Add_A_Forbidden_Record()
    {
        await using var h = await VecTestHarness.CreateAsync();
        var vecIndexer = h.Indexer(allowStub: true, dimension: Dim);
        await vecIndexer.IndexRecordAsync("inv-allowed", "tenant-A", "subj-1", "acme rent invoice", SearchResidency.Cache);
        await vecIndexer.IndexRecordAsync("inv-forbidden", "tenant-A", "subj-2", "acme rent invoice", SearchResidency.Cache);

        var ftsIndexer = new NodeSearchIndexer(h.Store.Factory);
        await ftsIndexer.IndexNodeAsync(new SearchNodeRow
        {
            RecordId = "inv-allowed", TenantId = "tenant-A", NodeType = "invoice",
            Title = "Acme rent invoice", Body = "acme rent invoice", Residency = SearchResidency.Cache,
        });

        // Alice may see ONLY inv-allowed.
        await h.GrantStore.AppendAsync(TenantA, TestSearchAuthorization.Grant(
            TenantA, Alice, ScopeExpression.Parse("/records/inv-allowed"), Now));

        // A malicious reranker that tries to inject the forbidden id — the read service's post-rerank clip
        // re-check drops it (belt-and-braces; the candidate set the reranker received never contained it).
        var svc = new NodeVecSearchReadService(
            h.Store.Factory, h.ScopeResolver, h.StubEmbedder(Dim), h.KnnEngine,
            new ForbiddenInjectingReranker("inv-forbidden"));
        var hits = await svc.HybridSearchAsync(TenantA, Alice, "acme rent invoice", Now);

        Assert.DoesNotContain(hits, x => x.RecordId == "inv-forbidden");
        Assert.Contains(hits, x => x.RecordId == "inv-allowed");
    }

    /// <summary>A reranker that reverses the candidate order (proves the read service honors the rescore).</summary>
    private sealed class ReversingReranker : IKgReranker
    {
        public Task<IReadOnlyList<string>> RerankAsync(
            string queryText, IReadOnlyList<RerankCandidate> candidates, int topK,
            System.Threading.CancellationToken ct = default)
        {
            var ids = candidates.Select(c => c.RecordId).Reverse().Take(Math.Max(0, topK)).ToList();
            return Task.FromResult<IReadOnlyList<string>>(ids);
        }
    }

    /// <summary>A malicious reranker that injects a forbidden id — the read service's post-rerank clip must drop it.</summary>
    private sealed class ForbiddenInjectingReranker : IKgReranker
    {
        private readonly string _forbidden;
        public ForbiddenInjectingReranker(string forbidden) => _forbidden = forbidden;

        public Task<IReadOnlyList<string>> RerankAsync(
            string queryText, IReadOnlyList<RerankCandidate> candidates, int topK,
            System.Threading.CancellationToken ct = default)
        {
            var ids = new List<string> { _forbidden };
            ids.AddRange(candidates.Select(c => c.RecordId));
            return Task.FromResult<IReadOnlyList<string>>(ids);
        }
    }

    [Fact(DisplayName = "multilingual: CJK query text embeds + indexes + fuses without error (the deterministic path is script-agnostic)")]
    public async Task Multilingual_Cjk_Roundtrips()
    {
        await using var h = await VecTestHarness.CreateAsync();
        var vecIndexer = h.Indexer(allowStub: true, dimension: Dim);
        // Japanese: "家賃の請求書" (rent invoice).
        await vecIndexer.IndexRecordAsync("inv-jp", "tenant-A", "subj-1", "家賃の請求書 ユニット四", SearchResidency.Cache);

        var ftsIndexer = new NodeSearchIndexer(h.Store.Factory);
        await ftsIndexer.IndexNodeAsync(new SearchNodeRow
        {
            RecordId = "inv-jp", TenantId = "tenant-A", NodeType = "invoice",
            Title = "家賃の請求書", Body = "家賃の請求書 ユニット四", Residency = SearchResidency.Cache,
        });

        await h.GrantStore.SaveAsync(TenantA, WholeTenantGrant(), 0);

        var svc = new NodeVecSearchReadService(
            h.Store.Factory, h.ScopeResolver, h.StubEmbedder(Dim), h.KnnEngine);
        var hits = await svc.HybridSearchAsync(TenantA, Alice, "家賃", Now);

        Assert.Contains(hits, x => x.RecordId == "inv-jp");
    }
}
