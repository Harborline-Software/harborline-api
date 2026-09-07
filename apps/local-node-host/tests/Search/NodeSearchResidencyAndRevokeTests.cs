using System;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Search;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Search;

/// <summary>
/// G-2 (revoke-TOCTOU) + G-3 (OnlineOnly⇒never-index + delete-on-revoke) functional tests for KG-search
/// Slice 0 (ADR 0135 KG-search F3-lift amendment).
/// </summary>
public sealed class NodeSearchResidencyAndRevokeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);
    private static readonly TenantId TenantA = TenantId.FromString("tenant-A");
    private static readonly ActorId Alice = new("alice");

    private static AccessGrant ActiveGrant(ScopeExpression scope, GrantResidency residency = GrantResidency.Cache,
        DateTimeOffset? revokedAt = null) =>
        TestSearchAuthorization.Grant(TenantA, Alice, scope, Now, residency,
            revocation: revokedAt is null ? null : new GrantRevocation(
                new ActorId("owner"), revokedAt.Value,
                new GrantReason(GrantReasonCodes.RevocationOffboarding)));

    // ── G-3: OnlineOnly ⇒ never-index (the structural insert-time half) ──────────────────────────────────

    [Fact(DisplayName = "G-3 (structural): the indexer REFUSES to write an OnlineOnly node — it never enters the index")]
    public async Task G3_OnlineOnly_Node_Is_Never_Indexed()
    {
        await using var store = await SearchTestStore.CreateAsync();
        var indexer = new NodeSearchIndexer(store.Factory);

        var indexed = await indexer.IndexNodeAsync(new SearchNodeRow
        {
            RecordId = "inv-online",
            TenantId = "tenant-A",
            NodeType = "invoice",
            Title = "online-only secret",
            Body = "must never enter the local index",
            Residency = SearchResidency.OnlineOnly,
        });

        Assert.False(indexed); // refused.

        await using var ctx = store.CreateContext();
        Assert.Equal(0, await ctx.Nodes.CountAsync()); // no node row.
        // And no FTS5 row either (the trigger only fires on an inserted node).
        var ftsCount = await CountFtsAsync(store);
        Assert.Equal(0, ftsCount);
    }

    [Fact(DisplayName = "G-3 (transition): a node already indexed is DELETED when its residency flips to OnlineOnly")]
    public async Task G3_Revoke_To_OnlineOnly_Deletes_Indexed_Node()
    {
        await using var store = await SearchTestStore.CreateAsync();
        var indexer = new NodeSearchIndexer(store.Factory);

        // First indexed as Cache-resident — it IS in the index (+ FTS row).
        Assert.True(await indexer.IndexNodeAsync(new SearchNodeRow
        {
            RecordId = "inv-1", TenantId = "tenant-A", NodeType = "invoice",
            Title = "rent invoice", Body = "monthly rent", Residency = SearchResidency.Cache,
        }));
        Assert.Equal(1, await CountFtsAsync(store));

        // Residency flips to OnlineOnly → the indexed node + its FTS row are removed.
        await indexer.OnResidencyChangedAsync(
            "tenant-A",
            "inv-1",
            GrantResidency.OnlineOnly,
            TestAuthorization.AllowedDecision(new TenantId("tenant-A"), "inv-1"));

        await using var ctx = store.CreateContext();
        Assert.Equal(0, await ctx.Nodes.CountAsync());
        Assert.Equal(0, await CountFtsAsync(store));
    }

    [Fact(DisplayName = "G-3 (clip half): an OnlineOnly GRANT contributes NOTHING to the authorized scope")]
    public async Task G3_OnlineOnly_Grant_Authorizes_Nothing()
    {
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        await grants.SaveAsync(
            TenantA,
            ActiveGrant(ScopeExpression.Parse("/records/inv-1"), GrantResidency.OnlineOnly),
            0);

        var clip = TestSearchAuthorization.Projection(grants);
        var scope = await clip.ResolveAsync(TenantA, Alice, Now);

        Assert.True(scope.IsEmpty); // an online-only grant authorizes nothing in the local index.
        Assert.False(scope.Authorizes("inv-1"));
    }

    // ── G-2: revoke-TOCTOU — the scope reflects the grant's active state at the resolve instant ──────────

    [Fact(DisplayName = "G-2: a revoked grant authorizes NOTHING — the scope is fail-closed Empty")]
    public async Task G2_Revoked_Grant_Authorizes_Nothing()
    {
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        // A grant that was revoked BEFORE the resolve instant.
        await grants.SaveAsync(
            TenantA,
            ActiveGrant(ScopeExpression.Parse("/records/inv-1"), revokedAt: Now.AddMinutes(-5)),
            0);

        var clip = TestSearchAuthorization.Projection(grants);
        var scope = await clip.ResolveAsync(TenantA, Alice, Now);

        Assert.True(scope.IsEmpty); // a revoked grant Reaches nothing — fail-closed.
    }

    [Fact(DisplayName = "G-2: a revoke landing BEFORE resolution is SEEN — the read returns nothing afterward")]
    public async Task G2_Revoke_Before_Resolution_Is_Seen_By_Read()
    {
        await using var store = await SearchTestStore.CreateAsync();
        var indexer = new NodeSearchIndexer(store.Factory);
        await indexer.IndexNodeAsync(new SearchNodeRow
        {
            RecordId = "inv-1", TenantId = "tenant-A", NodeType = "invoice",
            Title = "rent invoice", Body = "monthly rent", Residency = SearchResidency.Cache,
        });

        var grants = TestInMemoryAuthorizationStores.GrantStore();
        var grant = ActiveGrant(ScopeExpression.Parse("/records/inv-1"));
        await grants.SaveAsync(TenantA, grant, 0);

        var svc = new NodeSearchReadService(store.Factory, TestSearchAuthorization.Projection(grants));

        // While active, the read returns inv-1.
        var before = await svc.SearchAsync(TenantA, Alice, "rent", Now);
        Assert.Equal(new[] { "inv-1" }, before.Select(h => h.RecordId).ToArray());

        // Revoke lands. The NEXT read resolves the scope in the same read transaction as the scan, sees the
        // revoke, and returns nothing — the clip cannot be widened by a stale allow-set.
        await grants.RevokeAsync(TenantA, grant.GrantId, 1, Now.AddMinutes(1));
        var after = await svc.SearchAsync(TenantA, Alice, "rent", Now.AddMinutes(2));
        Assert.Empty(after);
    }

    [Fact(DisplayName = "G-2: an EXPIRED grant (past its validity window) authorizes nothing at the read instant")]
    public async Task G2_Expired_Grant_Authorizes_Nothing()
    {
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        var expiring = TestSearchAuthorization.Grant(TenantA, Alice,
            ScopeExpression.Parse("/records/inv-1"), Now, validTo: Now.AddDays(-1));
        await grants.SaveAsync(TenantA, expiring, 0);

        var clip = TestSearchAuthorization.Projection(grants);
        var scope = await clip.ResolveAsync(TenantA, Alice, Now);

        Assert.True(scope.IsEmpty);
    }

    private static async Task<int> CountFtsAsync(SearchTestStore store)
    {
        await using var ctx = store.CreateContext();
        var conn = (Microsoft.Data.Sqlite.SqliteConnection)ctx.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM search_fts;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
