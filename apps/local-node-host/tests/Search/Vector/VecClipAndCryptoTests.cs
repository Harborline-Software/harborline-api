using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Search.Vector;

/// <summary>
/// The security core of KG-search Slice 1b (ADR 0135 KG-search F3-lift amendment): the EXACT clip on the vec
/// path (no unclipped vec read; returns k from the allowed set even when the global-nearest are forbidden), the
/// G-2 same-transaction revoke-TOCTOU closure, and the G-6 erase-subject crypto-shred proof — all run against
/// the REAL encrypted per-subject-keyed index via the native-free brute-force KNN engine (which applies the same
/// exact clip + per-subject decrypt as the production vec0 path).
/// </summary>
public sealed class VecClipAndCryptoTests
{
    private const int Dim = 64;
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);
    private static readonly TenantId TenantA = TenantId.FromString("tenant-A");
    private static readonly ActorId Alice = new("alice");

    private static AccessGrant ActiveGrant(
        ScopeExpression scope, GrantResidency residency = GrantResidency.Cache, DateTimeOffset? revokedAt = null) =>
        TestSearchAuthorization.Grant(TenantA, Alice, scope, Now, residency,
            revocation: revokedAt is null ? null : new GrantRevocation(
                new ActorId("owner"), revokedAt.Value,
                new GrantReason(GrantReasonCodes.RevocationOffboarding)));

    /// <summary>Resolve the scope + run a clipped KNN inside ONE read transaction (the G-2 path), returning ids nearest-first.</summary>
    private static async Task<IReadOnlyList<string>> ClippedKnnAsync(
        VecTestHarness h, string queryText, int k = 10)
    {
        var queryArtifact = await h.StubEmbedder(Dim).EmbedAsync("__q__", "tenant-A", null, queryText);
        var queryVec = queryArtifact.Vector;

        await using var ctx = h.Store.CreateContext();
        var connection = (SqliteConnection)ctx.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync();

        var scope = await h.ScopeResolver
            .ResolveAsync(connection, tx, TenantA, Alice, Now, default);
        if (scope.IsEmpty)
        {
            return Array.Empty<string>();
        }

        var hits = await h.KnnEngine.KnnAsync(connection, tx, "tenant-A", scope, queryVec, k, default);
        return hits.Select(x => x.RecordId).ToArray();
    }

    // ── G-1 — the EXACT clip on the vec path ──────────────────────────────────────────────────────────────

    [Fact(DisplayName = "G-1 vec clip: KNN returns ONLY records the principal's ForRecords grant authorizes")]
    public async Task Vec_Knn_Returns_Only_Authorized()
    {
        await using var h = await VecTestHarness.CreateAsync();
        var indexer = h.Indexer(allowStub: true, dimension: Dim);
        await indexer.IndexRecordAsync("inv-1", "tenant-A", "subj-1", "alpha rent invoice", SearchResidency.Cache);
        await indexer.IndexRecordAsync("inv-2", "tenant-A", "subj-2", "alpha rent invoice", SearchResidency.Cache);

        await h.GrantStore.SaveAsync(
            TenantA, ActiveGrant(ScopeExpression.Parse("/records/inv-1")), 0);

        var ids = await ClippedKnnAsync(h, "alpha rent invoice");
        Assert.Equal(new[] { "inv-1" }, ids);
    }

    [Fact(DisplayName = "G-1 vec clip is a TRUE PRE-FILTER: returns the authorized record even when the GLOBAL-NEAREST vectors are all FORBIDDEN (no neighbour leak)")]
    public async Task Vec_Knn_Returns_Authorized_When_Global_Nearest_Forbidden()
    {
        await using var h = await VecTestHarness.CreateAsync();
        var indexer = h.Indexer(allowStub: true, dimension: Dim);

        // inv-forbidden-* embed the EXACT query text (Hamming distance 0 — globally nearest). inv-allowed embeds
        // DIFFERENT text (farther). Alice may see ONLY inv-allowed. A post-filter would return the global top-k
        // (all forbidden) → 0 rows; a TRUE pre-filter returns inv-allowed (the spike R-1 proof).
        await indexer.IndexRecordAsync("inv-forbidden-1", "tenant-A", "s1", "the exact query phrase here", SearchResidency.Cache);
        await indexer.IndexRecordAsync("inv-forbidden-2", "tenant-A", "s2", "the exact query phrase here", SearchResidency.Cache);
        await indexer.IndexRecordAsync("inv-allowed", "tenant-A", "s3", "a totally different far away text", SearchResidency.Cache);

        await h.GrantStore.SaveAsync(
            TenantA, ActiveGrant(ScopeExpression.Parse("/records/inv-allowed")), 0);

        var ids = await ClippedKnnAsync(h, "the exact query phrase here", k: 5);
        Assert.Equal(new[] { "inv-allowed" }, ids);
        Assert.DoesNotContain("inv-forbidden-1", ids);
        Assert.DoesNotContain("inv-forbidden-2", ids);
    }

    [Fact(DisplayName = "G-1 vec clip: NO grant ⇒ NO vec rows (fail-closed, never a tenant-wide fall-through)")]
    public async Task Vec_No_Grant_Returns_Nothing()
    {
        await using var h = await VecTestHarness.CreateAsync();
        await h.Indexer(allowStub: true, dimension: Dim)
            .IndexRecordAsync("inv-1", "tenant-A", "subj-1", "alpha rent", SearchResidency.Cache);

        // Alice holds NO grant.
        var ids = await ClippedKnnAsync(h, "alpha rent");
        Assert.Empty(ids);
    }

    [Fact(DisplayName = "G-1 vec clip: read authority cannot be laundered through another grant's residency")]
    public async Task Vec_Read_Authority_Residency_And_Scope_Must_Come_From_One_Grant()
    {
        await using var h = await VecTestHarness.CreateAsync();
        await h.GrantStore.AppendAsync(
            TenantA, ActiveGrant(ScopeExpression.Parse("/"), GrantResidency.OnlineOnly), "online-reader");
        await h.GrantStore.AppendAsync(
            TenantA,
            TestSearchAuthorization.Grant(
                TenantA, Alice, ScopeExpression.Parse("/"), Now, GrantResidency.Cache, canRead: false),
            "cache-without-read");

        await using var ctx = h.Store.CreateContext();
        var connection = (SqliteConnection)ctx.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync();

        var scope = await h.ScopeResolver.ResolveAsync(
            connection, tx, TenantA, Alice, Now, default);

        Assert.True(scope.IsEmpty);
    }

    [Fact(DisplayName = "G-1 vec clip: a WholeTenant grant sees all tenant rows but NEVER cross-tenant")]
    public async Task Vec_WholeTenant_Stays_In_Tenant()
    {
        await using var h = await VecTestHarness.CreateAsync();
        var indexer = h.Indexer(allowStub: true, dimension: Dim);
        await indexer.IndexRecordAsync("a-1", "tenant-A", "subj-1", "shared text", SearchResidency.Cache);
        await indexer.IndexRecordAsync("a-2", "tenant-A", "subj-2", "shared text", SearchResidency.Cache);
        // A row in a DIFFERENT tenant (same file is per-tenant in prod; here we just plant a foreign tenant_id).
        await indexer.IndexRecordAsync("b-1", "tenant-B", "subj-3", "shared text", SearchResidency.Cache);

        await h.GrantStore.SaveAsync(TenantA, ActiveGrant(ScopeExpression.Parse("/")), 0);

        var ids = await ClippedKnnAsync(h, "shared text");
        Assert.Contains("a-1", ids);
        Assert.Contains("a-2", ids);
        Assert.DoesNotContain("b-1", ids); // tenant predicate keeps the WholeTenant scope in-tenant.
    }

    // ── G-2 — same-transaction revoke-TOCTOU closure ─────────────────────────────────────────────────────

    [Fact(DisplayName = "G-2: a revoke that has landed before the scan is SEEN — the revoked grant authorizes nothing (same-snapshot)")]
    public async Task G2_Landed_Revoke_Is_Seen()
    {
        await using var h = await VecTestHarness.CreateAsync();
        await h.Indexer(allowStub: true, dimension: Dim)
            .IndexRecordAsync("inv-1", "tenant-A", "subj-1", "alpha rent", SearchResidency.Cache);

        var grant = ActiveGrant(ScopeExpression.Parse("/records/inv-1"));
        await h.GrantStore.SaveAsync(TenantA, grant, 0);

        // Before: Alice sees inv-1.
        Assert.Equal(new[] { "inv-1" }, await ClippedKnnAsync(h, "alpha rent"));

        // Revoke lands. The next scan resolves grants in its OWN transaction and sees the revocation.
        await h.GrantStore.RevokeAsync(TenantA, grant.GrantId, 1, Now.AddSeconds(-1));

        Assert.Empty(await ClippedKnnAsync(h, "alpha rent"));
    }

    [Fact(DisplayName = "G-2: the grant read + the KNN scan run in ONE transaction over the SAME file (single snapshot — a mid-query revoke cannot widen the materialized allow-set)")]
    public async Task G2_Grant_Read_And_Scan_Share_One_Transaction()
    {
        await using var h = await VecTestHarness.CreateAsync();
        await h.Indexer(allowStub: true, dimension: Dim)
            .IndexRecordAsync("inv-1", "tenant-A", "subj-1", "alpha rent", SearchResidency.Cache);
        await h.GrantStore.SaveAsync(
            TenantA, ActiveGrant(ScopeExpression.Parse("/records/inv-1")), 0);

        await using var ctx = h.Store.CreateContext();
        var connection = (SqliteConnection)ctx.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync();

        // Grant read inside the transaction.
        var scope = await h.ScopeResolver.ResolveAsync(connection, tx, TenantA, Alice, Now, default);
        Assert.False(scope.IsEmpty);

        // The KNN scan inside the SAME transaction sees a consistent snapshot — the grant read it just did and
        // the scan are one snapshot, so no mid-query revoke (which would be a separate write) can widen this set.
        var queryArtifact = await h.StubEmbedder(Dim).EmbedAsync("__q__", "tenant-A", null, "alpha rent");
        var hits = await h.KnnEngine.KnnAsync(connection, tx, "tenant-A", scope, queryArtifact.Vector, 10, default);
        Assert.Equal(new[] { "inv-1" }, hits.Select(x => x.RecordId).ToArray());
    }

    // ── G-6 — erase-subject ⇒ vectors gone/unreadable (crypto-shred; the index is NOT a shred bypass) ──────

    [Fact(DisplayName = "G-6: crypto-shredding a subject makes their embeddings UNREADABLE — they vanish from KNN while other subjects remain")]
    public async Task G6_Erase_Subject_Makes_Embeddings_Unreadable()
    {
        await using var h = await VecTestHarness.CreateAsync();
        var indexer = h.Indexer(allowStub: true, dimension: Dim);
        await indexer.IndexRecordAsync("inv-alice", "tenant-A", "subject-alice", "alpha rent invoice", SearchResidency.Cache);
        await indexer.IndexRecordAsync("inv-bob", "tenant-A", "subject-bob", "alpha rent invoice", SearchResidency.Cache);

        await h.GrantStore.SaveAsync(TenantA, ActiveGrant(ScopeExpression.Parse("/")), 0);

        // Before erasure: both records are retrievable.
        var before = await ClippedKnnAsync(h, "alpha rent invoice");
        Assert.Contains("inv-alice", before);
        Assert.Contains("inv-bob", before);

        // Crypto-shred subject-alice (destroy their per-subject key via the erasure tombstone).
        await h.Erasure.MarkErasedAsync(TenantA, new SubjectId("subject-alice"));

        // After: subject-alice's embedding is undecryptable (the sub-key is gone) ⇒ it drops out; subject-bob's
        // is untouched. The index is NOT a shred bypass — even though the ciphertext row still physically exists,
        // it cannot be read.
        var after = await ClippedKnnAsync(h, "alpha rent invoice");
        Assert.DoesNotContain("inv-alice", after);
        Assert.Contains("inv-bob", after);
    }

    [Fact(DisplayName = "G-6: PurgeSubjectAsync physically removes a shredded subject's vec rows (durable purge of the partially-invertible blob)")]
    public async Task G6_Purge_Subject_Removes_Rows()
    {
        await using var h = await VecTestHarness.CreateAsync();
        var indexer = h.Indexer(allowStub: true, dimension: Dim);
        await indexer.IndexRecordAsync("inv-alice-1", "tenant-A", "subject-alice", "rent one", SearchResidency.Cache);
        await indexer.IndexRecordAsync("inv-alice-2", "tenant-A", "subject-alice", "rent two", SearchResidency.Cache);
        await indexer.IndexRecordAsync("inv-bob", "tenant-A", "subject-bob", "rent three", SearchResidency.Cache);

        var purged = await indexer.PurgeSubjectAsync("tenant-A", "subject-alice");
        Assert.Equal(2, purged);

        await using var ctx = h.Store.CreateContext();
        var remaining = await ctx.VecRows.Select(v => v.RecordId).ToListAsync();
        Assert.Equal(new[] { "inv-bob" }, remaining);
    }

    [Fact(DisplayName = "G-6: indexing a record for an ALREADY-erased subject fails closed — the embedding is never written")]
    public async Task G6_Index_For_Erased_Subject_Fails_Closed()
    {
        await using var h = await VecTestHarness.CreateAsync();
        await h.Erasure.MarkErasedAsync(TenantA, new SubjectId("subject-gone"));
        var indexer = h.Indexer(allowStub: true, dimension: Dim);

        await Assert.ThrowsAsync<FieldEncryptionDeniedAtIndexException>(() =>
            indexer.IndexRecordAsync("inv-gone", "tenant-A", "subject-gone", "rent", SearchResidency.Cache));

        await using var ctx = h.Store.CreateContext();
        Assert.Equal(0, await ctx.VecRows.CountAsync());
    }
}
