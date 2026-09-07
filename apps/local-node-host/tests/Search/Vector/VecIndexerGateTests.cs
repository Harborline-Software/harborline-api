using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Search.Vector;

/// <summary>
/// The M1 (no-fake-as-real) + G-3 (OnlineOnly⇒never-index) + G-5 (versioned projection / dimension) gates of the
/// KG-search Slice 1b vector indexer (ADR 0135 KG-search F3-lift amendment).
/// </summary>
public sealed class VecIndexerGateTests
{
    private const int Dim = 64;

    private static KgEmbeddingArtifact Artifact(
        string recordId, string model, int? vectorWidth = null, int? declaredDim = null, string? subject = "subj-1") =>
        new(
            RecordId: recordId,
            TenantId: "tenant-A",
            SubjectId: subject,
            Vector: Enumerable.Range(0, vectorWidth ?? Dim).Select(i => (float)((i % 3) - 1)).ToArray(),
            Dimension: declaredDim ?? vectorWidth ?? Dim,
            Model: model,
            ModelVersion: "1.0");

    // ── M1 — a stub-model artifact is REJECTED by a production indexer ────────────────────────────────────

    [Fact(DisplayName = "M1: a STUB-model artifact is REFUSED by a production indexer (allowStubModel=false) — fake can't pin as real")]
    public async Task M1_Stub_Model_Refused_By_Production_Indexer()
    {
        await using var h = await VecTestHarness.CreateAsync();
        var prodIndexer = new NodeVecIndexer(
            h.Store.Factory, new StubKgEmbeddingProvider(Dim), h.SubjectEncryptor,
            accelerationSink: null, allowStubModel: false);

        var ex = await Assert.ThrowsAsync<KgFloorUnavailableException>(() =>
            prodIndexer.IndexRecordAsync("inv-1", "tenant-A", "subj-1", "rent invoice", SearchResidency.Cache));

        Assert.Equal(KgModelFloorGate.StubModelSentinel, ex.RefusedModel);

        // And nothing landed in the index.
        await using var ctx = h.Store.CreateContext();
        Assert.Equal(0, await ctx.VecRows.CountAsync());
    }

    [Fact(DisplayName = "M1: an artifact with a FABRICATED 'bge-m3' label but produced by the stub is admitted ONLY because it self-identifies — a forged unknown model is REFUSED")]
    public async Task M1_Unknown_Model_Refused()
    {
        await using var h = await VecTestHarness.CreateAsync();
        var indexer = h.Indexer(allowStub: true, dimension: Dim);

        // An artifact claiming a model that is NOT a registered floor and NOT the known stub sentinel.
        var forged = Artifact("inv-x", model: "totally-made-up-model");
        var ex = await Assert.ThrowsAsync<KgFloorUnavailableException>(() =>
            indexer.IndexArtifactAsync(forged, SearchResidency.Cache));
        Assert.Equal("totally-made-up-model", ex.RefusedModel);
    }

    [Fact(DisplayName = "M1: a non-opted-in host with NO provider fails closed (provider.kg_floor_unavailable) — never indexes an unverifiable artifact")]
    public async Task M1_No_Provider_Fails_Closed()
    {
        await using var h = await VecTestHarness.CreateAsync();
        var indexer = h.IndexerNoProvider();

        var ex = await Assert.ThrowsAsync<KgFloorUnavailableException>(() =>
            indexer.IndexRecordAsync("inv-1", "tenant-A", "subj-1", "rent invoice", SearchResidency.Cache));
        Assert.Contains(KgFloorUnavailableException.ReasonCode, ex.Message);
    }

    [Fact(DisplayName = "M1: a REGISTERED floor id (bge-m3) at the correct dimension IS admitted")]
    public async Task M1_Registered_Floor_Admitted()
    {
        await using var h = await VecTestHarness.CreateAsync();
        // allowStub is irrelevant — this is a REAL floor id. Use a 1024-dim artifact (the bge-m3 dimension).
        var indexer = new NodeVecIndexer(
            h.Store.Factory, embedder: null, h.SubjectEncryptor, accelerationSink: null, allowStubModel: false);

        var real = Artifact("inv-real", KgModelFloor.BgeM3.Id, vectorWidth: KgModelFloor.BgeM3.Dimension);
        await indexer.IndexArtifactAsync(real, SearchResidency.Cache);

        await using var ctx = h.Store.CreateContext();
        var row = await ctx.VecRows.SingleAsync();
        Assert.Equal("inv-real", row.RecordId);
        Assert.Equal(KgModelFloor.BgeM3.Id, row.Model);
    }

    // ── G-5 — model / dimension mismatch is a hard fault ──────────────────────────────────────────────────

    [Fact(DisplayName = "G-5: a bge-m3 artifact whose vector width != the floor dimension is REJECTED (dimension mismatch)")]
    public async Task G5_Dimension_Mismatch_Rejected()
    {
        await using var h = await VecTestHarness.CreateAsync();
        var indexer = new NodeVecIndexer(
            h.Store.Factory, embedder: null, h.SubjectEncryptor, accelerationSink: null, allowStubModel: false);

        // bge-m3 declares 1024 dims; hand it a 64-wide vector — a hard G-5 fault.
        var wrong = Artifact("inv-wrong", KgModelFloor.BgeM3.Id, vectorWidth: 64, declaredDim: 64);
        var ex = await Assert.ThrowsAsync<KgFloorUnavailableException>(() =>
            indexer.IndexArtifactAsync(wrong, SearchResidency.Cache));
        Assert.Contains("dimension", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "G-5: an artifact whose declared Dimension disagrees with its actual vector width is REJECTED")]
    public async Task G5_Declared_Vs_Actual_Width_Mismatch_Rejected()
    {
        await using var h = await VecTestHarness.CreateAsync();
        var indexer = h.Indexer(allowStub: true, dimension: Dim);

        // Stub model (admitted by allowStub) but declared dimension lies about the vector width.
        var lying = Artifact("inv-lie", KgModelFloorGate.StubModelSentinel, vectorWidth: Dim, declaredDim: Dim + 8);
        await Assert.ThrowsAsync<KgFloorUnavailableException>(() =>
            indexer.IndexArtifactAsync(lying, SearchResidency.Cache));
    }

    [Fact(DisplayName = "G-5: every indexed row carries model + modelVersion + dimension (versioned projection)")]
    public async Task G5_Row_Carries_Model_Version_Dimension()
    {
        await using var h = await VecTestHarness.CreateAsync();
        await h.Indexer(allowStub: true, dimension: Dim)
            .IndexRecordAsync("inv-1", "tenant-A", "subj-1", "rent invoice", SearchResidency.Cache);

        await using var ctx = h.Store.CreateContext();
        var row = await ctx.VecRows.SingleAsync();
        Assert.Equal(KgModelFloorGate.StubModelSentinel, row.Model);
        Assert.False(string.IsNullOrEmpty(row.ModelVersion));
        Assert.Equal(Dim, row.Dimension);
    }

    // ── G-3 — OnlineOnly ⇒ never-index (vec path) ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "G-3: the vector indexer REFUSES an OnlineOnly record — no embedding enters the index")]
    public async Task G3_OnlineOnly_Never_Indexed()
    {
        await using var h = await VecTestHarness.CreateAsync();
        var indexer = h.Indexer(allowStub: true, dimension: Dim);

        var indexed = await indexer.IndexRecordAsync(
            "inv-online", "tenant-A", "subj-1", "online-only secret", SearchResidency.OnlineOnly);

        Assert.False(indexed); // refused.
        await using var ctx = h.Store.CreateContext();
        Assert.Equal(0, await ctx.VecRows.CountAsync());
    }

    [Fact(DisplayName = "G-3: a record flipping to OnlineOnly DELETES its pre-existing indexed embedding (delete-on-revoke)")]
    public async Task G3_Flip_To_OnlineOnly_Deletes_Existing()
    {
        await using var h = await VecTestHarness.CreateAsync();
        var indexer = h.Indexer(allowStub: true, dimension: Dim);

        await indexer.IndexRecordAsync("inv-1", "tenant-A", "subj-1", "cache rent", SearchResidency.Cache);
        await using (var ctx = h.Store.CreateContext())
        {
            Assert.Equal(1, await ctx.VecRows.CountAsync());
        }

        // The grant is revoked + re-issued online-only ⇒ residency flips ⇒ the embedding must be purged.
        var stillIndexed = await indexer.IndexRecordAsync(
            "inv-1", "tenant-A", "subj-1", "cache rent", SearchResidency.OnlineOnly);
        Assert.False(stillIndexed);

        await using (var ctx = h.Store.CreateContext())
        {
            Assert.Equal(0, await ctx.VecRows.CountAsync());
        }
    }
}
