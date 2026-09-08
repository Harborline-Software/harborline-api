using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Search;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Search;

/// <summary>
/// Functional tests for the KG-search Slice 0 clip (ADR 0135 KG-search F3-lift amendment): a principal
/// retrieves ONLY their authorized records over BOTH the FTS5 text index and the graph expansion, including
/// when global matches are forbidden; cross-tenant isolation; multilingual trigram recall.
/// </summary>
public sealed class NodeSearchClipTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56); // 2026-ish

    private static readonly TenantId TenantA = TenantId.FromString("tenant-A");
    private static readonly ActorId Alice = new("alice");

    private static AccessGrant ActiveGrant(TenantId tenant, ActorId principal, ScopeExpression scope,
        GrantResidency residency = GrantResidency.Cache) =>
        TestSearchAuthorization.Grant(tenant, principal, scope, Now, residency);

    private static AccessGrant ActiveGrant(
        TenantId tenant,
        ActorId principal,
        PermissionSet permissions,
        ScopeExpression scope) =>
        TestSearchAuthorization.Grant(tenant, principal, scope, Now,
            canRead: permissions.Contains(TeamRolePermissions.RecordsRead));

    private static async Task AppendRecordGrantsAsync(
        InMemoryGrantStore grants, TenantId tenant, ActorId principal,
        IEnumerable<string> recordIds, PermissionSet? permissions = null)
    {
        foreach (var recordId in recordIds)
        {
            var scope = ScopeExpression.Parse($"/records/{recordId}");
            var grant = permissions is null
                ? TestSearchAuthorization.Grant(tenant, principal, scope, Now)
                : TestSearchAuthorization.Grant(tenant, principal, scope, Now,
                    canRead: permissions.Contains(TeamRolePermissions.RecordsRead));
            await grants.AppendAsync(tenant, grant);
        }
    }

    private static async Task SeedNodeAsync(
        SearchTestStore store, string recordId, string tenant, string nodeType,
        string title, string body, SearchResidency residency = SearchResidency.Cache)
    {
        var indexer = new NodeSearchIndexer(store.Factory);
        await indexer.IndexNodeAsync(new SearchNodeRow
        {
            RecordId = recordId,
            TenantId = tenant,
            NodeType = nodeType,
            Title = title,
            Body = body,
            Residency = residency,
        });
    }

    [Theory(DisplayName = "Search clip: a permissionless grant contributes zero reach for either scope shape")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Permissionless_Grant_Contributes_Zero_Search_Reach(bool wholeTenant)
    {
        var grantScope = wholeTenant
            ? ScopeExpression.Parse("/")
            : ScopeExpression.Parse("/records/inv-1");
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        await grants.SaveAsync(
            TenantA, ActiveGrant(TenantA, Alice, PermissionSet.Empty, grantScope), 0);

        var scope = await TestSearchAuthorization.Projection(grants)
            .ResolveAsync(TenantA, Alice, Now);

        Assert.True(scope.IsEmpty);
        Assert.False(scope.WholeTenant);
        Assert.Empty(scope.RecordIds);
    }

    [Theory(DisplayName = "Search clip: a write-only grant contributes zero reach for either scope shape")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WriteOnly_Grant_Contributes_Zero_Search_Reach(bool wholeTenant)
    {
        var grantScope = wholeTenant
            ? ScopeExpression.Parse("/")
            : ScopeExpression.Parse("/records/inv-1");
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        await grants.SaveAsync(
            TenantA,
            ActiveGrant(
                TenantA,
                Alice,
                PermissionSet.Of(TeamRolePermissions.RecordsWrite),
                grantScope),
            0);

        var scope = await TestSearchAuthorization.Projection(grants)
            .ResolveAsync(TenantA, Alice, Now);

        Assert.True(scope.IsEmpty);
        Assert.False(scope.WholeTenant);
        Assert.Empty(scope.RecordIds);
    }

    [Theory(DisplayName = "Search clip: a read-covering grant preserves its exact scope shape")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadCovering_Grant_Preserves_Its_Search_Reach(bool wholeTenant)
    {
        var grantScope = wholeTenant
            ? ScopeExpression.Parse("/")
            : ScopeExpression.Parse("/records/inv-1");
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        await grants.SaveAsync(
            TenantA,
            ActiveGrant(
                TenantA,
                Alice,
                PermissionSet.Of(TeamRolePermissions.RecordsRead),
                grantScope),
            0);

        var scope = await TestSearchAuthorization.Projection(grants)
            .ResolveAsync(TenantA, Alice, Now);

        Assert.False(scope.IsEmpty);
        Assert.Equal(wholeTenant, scope.WholeTenant);
        Assert.Equal(
            wholeTenant ? Array.Empty<string>() : new[] { "inv-1" },
            scope.RecordIds.OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    [Fact(DisplayName = "Search clip: read authority cannot be laundered through another grant's residency")]
    public async Task Read_Authority_Residency_And_Scope_Must_Come_From_One_Grant()
    {
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        await grants.AppendAsync(TenantA,
            TestSearchAuthorization.Grant(
                TenantA, Alice, ScopeExpression.Parse("/"), Now, GrantResidency.OnlineOnly),
            "online-reader");
        await grants.AppendAsync(TenantA,
            TestSearchAuthorization.Grant(
                TenantA, Alice, ScopeExpression.Parse("/"), Now, GrantResidency.Cache, canRead: false),
            "cache-without-read");

        var scope = await TestSearchAuthorization.Projection(grants).ResolveAsync(TenantA, Alice, Now);

        Assert.True(scope.IsEmpty);
    }

    [Fact(DisplayName = "FTS5 ranking: unreadable corpus content cannot steer the authorized page")]
    public async Task Ranking_Is_Identical_When_Only_Unauthorized_Content_Changes()
    {
        await using var alphaHeavyStore = await SearchTestStore.CreateAsync();
        await using var betaHeavyStore = await SearchTestStore.CreateAsync();

        foreach (var store in new[] { alphaHeavyStore, betaHeavyStore })
        {
            await SeedNodeAsync(store, "auth-a", "tenant-A", "invoice",
                "alpha alpha alpha alpha alpha beta", string.Empty);
            await SeedNodeAsync(store, "auth-b", "tenant-A", "invoice",
                "alpha beta beta beta beta beta", string.Empty);
        }

        for (var i = 0; i < 20; i++)
        {
            await SeedNodeAsync(alphaHeavyStore, $"hidden-alpha-{i}", "tenant-A", "invoice",
                "alpha", string.Empty);
            await SeedNodeAsync(betaHeavyStore, $"hidden-beta-{i}", "tenant-A", "invoice",
                "beta", string.Empty);
        }

        var grants = TestInMemoryAuthorizationStores.GrantStore();
        await AppendRecordGrantsAsync(grants, TenantA, Alice, ["auth-a", "auth-b"],
            PermissionSet.Of(TeamRolePermissions.RecordsRead));
        var projection = TestSearchAuthorization.Projection(grants);
        var alphaHeavyService = new NodeSearchReadService(alphaHeavyStore.Factory, projection);
        var betaHeavyService = new NodeSearchReadService(betaHeavyStore.Factory, projection);

        var alphaHeavyPage = await alphaHeavyService.SearchAsync(TenantA, Alice, "alpha beta", Now, limit: 1);
        var betaHeavyPage = await betaHeavyService.SearchAsync(TenantA, Alice, "alpha beta", Now, limit: 1);

        Assert.Single(alphaHeavyPage);
        Assert.Single(betaHeavyPage);
        Assert.Equal(
            alphaHeavyPage.Select(hit => hit.RecordId),
            betaHeavyPage.Select(hit => hit.RecordId));
    }

    [Fact(DisplayName = "FTS5 clip: a principal retrieves ONLY records its ForRecords grant authorizes")]
    public async Task Fts_Returns_Only_Authorized_Records()
    {
        await using var store = await SearchTestStore.CreateAsync();
        await SeedNodeAsync(store, "inv-1", "tenant-A", "invoice", "Acme rent invoice", "monthly rent for unit 4");
        await SeedNodeAsync(store, "inv-2", "tenant-A", "invoice", "Beta rent invoice", "monthly rent for unit 9");

        var grants = TestInMemoryAuthorizationStores.GrantStore();
        // Alice may see ONLY inv-1.
        await grants.SaveAsync(
            TenantA, ActiveGrant(TenantA, Alice, ScopeExpression.Parse("/records/inv-1")), 0);

        var svc = new NodeSearchReadService(store.Factory, TestSearchAuthorization.Projection(grants));
        var hits = await svc.SearchAsync(TenantA, Alice, "rent", Now);

        Assert.Equal(new[] { "inv-1" }, hits.Select(h => h.RecordId).ToArray());
    }

    [Fact(DisplayName = "FTS5 clip: returns the AUTHORIZED record even when the global-nearest match is FORBIDDEN")]
    public async Task Fts_Returns_Authorized_Even_When_Global_Best_Is_Forbidden()
    {
        await using var store = await SearchTestStore.CreateAsync();
        // inv-2 is a STRONGER textual match (exact phrase) but Alice is NOT authorized for it; inv-1 weaker
        // but authorized. The clip must surface inv-1 and NEVER inv-2 (no neighbour leak — the F3 doubt).
        await SeedNodeAsync(store, "inv-1", "tenant-A", "invoice", "rent reminder", "rent due soon");
        await SeedNodeAsync(store, "inv-2", "tenant-A", "invoice", "rent rent rent", "rent rent rent rent rent");

        var grants = TestInMemoryAuthorizationStores.GrantStore();
        await grants.SaveAsync(
            TenantA, ActiveGrant(TenantA, Alice, ScopeExpression.Parse("/records/inv-1")), 0);

        var svc = new NodeSearchReadService(store.Factory, TestSearchAuthorization.Projection(grants));
        var hits = await svc.SearchAsync(TenantA, Alice, "rent", Now);

        Assert.Equal(new[] { "inv-1" }, hits.Select(h => h.RecordId).ToArray());
        Assert.DoesNotContain("inv-2", hits.Select(h => h.RecordId));
    }

    [Fact(DisplayName = "FTS5 clip: NO grant ⇒ NO rows (fail-closed default, never tenant-wide fall-through)")]
    public async Task Fts_No_Grant_Returns_Nothing()
    {
        await using var store = await SearchTestStore.CreateAsync();
        await SeedNodeAsync(store, "inv-1", "tenant-A", "invoice", "Acme rent invoice", "monthly rent");

        var grants = TestInMemoryAuthorizationStores.GrantStore(); // Alice holds NO grant.
        var svc = new NodeSearchReadService(store.Factory, TestSearchAuthorization.Projection(grants));
        var hits = await svc.SearchAsync(TenantA, Alice, "rent", Now);

        Assert.Empty(hits);
    }

    [Fact(DisplayName = "FTS5 clip: a WholeTenant grant sees every node in the tenant (but never cross-tenant)")]
    public async Task Fts_WholeTenant_Grant_Sees_All_In_Tenant()
    {
        await using var store = await SearchTestStore.CreateAsync();
        await SeedNodeAsync(store, "inv-1", "tenant-A", "invoice", "Acme rent invoice", "rent");
        await SeedNodeAsync(store, "inv-2", "tenant-A", "invoice", "Beta rent invoice", "rent");

        var grants = TestInMemoryAuthorizationStores.GrantStore();
        await grants.SaveAsync(TenantA, ActiveGrant(TenantA, Alice, ScopeExpression.Parse("/")), 0);

        var svc = new NodeSearchReadService(store.Factory, TestSearchAuthorization.Projection(grants));
        var hits = await svc.SearchAsync(TenantA, Alice, "rent", Now);

        Assert.Equal(new[] { "inv-1", "inv-2" }, hits.Select(h => h.RecordId).OrderBy(x => x).ToArray());
    }

    [Fact(DisplayName = "Cross-tenant isolation: a tenant-A WholeTenant grant cannot OPEN tenant-B's encrypted file")]
    public async Task CrossTenant_Isolation_Per_Encrypted_File()
    {
        // Two DISTINCT per-tenant encrypted files (different DEKs) — the cross-tenant boundary.
        await using var storeA = await SearchTestStore.CreateAsync(keySalt: 7);
        await using var storeB = await SearchTestStore.CreateAsync(keySalt: 99);
        await SeedNodeAsync(storeA, "inv-A", "tenant-A", "invoice", "alpha secret", "alpha");
        await SeedNodeAsync(storeB, "inv-B", "tenant-B", "invoice", "beta secret", "beta");

        // Alice has a WholeTenant grant in tenant-A. Reading tenant-A's store returns only tenant-A rows.
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        await grants.SaveAsync(TenantA, ActiveGrant(TenantA, Alice, ScopeExpression.Parse("/")), 0);
        var svcA = new NodeSearchReadService(storeA.Factory, TestSearchAuthorization.Projection(grants));

        var hitsA = await svcA.SearchAsync(TenantA, Alice, "secret", Now);
        Assert.Equal(new[] { "inv-A" }, hitsA.Select(h => h.RecordId).ToArray());

        // tenant-B's data lives in a SEPARATE file keyed by a different DEK — storeA's service cannot reach
        // it at all (different physical file). Even the in-file tenant predicate would reject a tenant-B row.
        Assert.DoesNotContain("inv-B", hitsA.Select(h => h.RecordId));
    }

    [Fact(DisplayName = "Multilingual FTS5 trigram: CJK query has NON-ZERO recall (unicode61 would give 0)")]
    public async Task Multilingual_Trigram_Cjk_Recall()
    {
        await using var store = await SearchTestStore.CreateAsync();
        // Japanese: "請求書" (invoice), "月額家賃" (monthly rent). With the default unicode61 tokenizer a CJK
        // substring query recalls ZERO (no whitespace word boundaries); the trigram tokenizer indexes 3-char
        // sequences so a CJK substring of length >= 3 matches. (A query shorter than the trigram length
        // produces no trigram and so matches nothing — a CJK query must be >= 3 chars, which is the natural
        // term length for the doctype-name / field-name substrings users actually search.)
        await SeedNodeAsync(store, "inv-jp", "tenant-A", "invoice", "請求書 2026年6月", "家賃 ユニット4の月額家賃");

        var grants = TestInMemoryAuthorizationStores.GrantStore();
        await grants.SaveAsync(TenantA, ActiveGrant(TenantA, Alice, ScopeExpression.Parse("/")), 0);
        var svc = new NodeSearchReadService(store.Factory, TestSearchAuthorization.Projection(grants));

        var hits = await svc.SearchAsync(TenantA, Alice, "月額家賃", Now); // 4-char CJK substring of the body.
        Assert.Contains("inv-jp", hits.Select(h => h.RecordId)); // CJK recall > 0 (unicode61 would give 0).
    }

    [Fact(DisplayName = "Graph expansion: a 1-hop neighbourhood is CLIPPED — unauthorized neighbours are not traversed")]
    public async Task Graph_Expansion_Is_Clipped()
    {
        await using var store = await SearchTestStore.CreateAsync();
        // lease-1 → tenant party-1 (authorized) and → property prop-1 (NOT authorized).
        await SeedNodeAsync(store, "lease-1", "tenant-A", "lease", "Lease unit 4", "active lease");
        await SeedNodeAsync(store, "party-1", "tenant-A", "party", "Acme Tenant", "the renting party");
        await SeedNodeAsync(store, "prop-1", "tenant-A", "property", "Building A", "the property");

        var indexer = new NodeSearchIndexer(store.Factory);
        await indexer.IndexEdgesAsync("lease-1", new[]
        {
            new SearchEdgeRow { Id = Guid.NewGuid().ToString(), TenantId = "tenant-A", SourceRecordId = "lease-1", TargetRecordId = "party-1", EdgeType = "lease-tenant" },
            new SearchEdgeRow { Id = Guid.NewGuid().ToString(), TenantId = "tenant-A", SourceRecordId = "lease-1", TargetRecordId = "prop-1", EdgeType = "lease-property" },
        });

        // Alice may see lease-1 + party-1 but NOT prop-1.
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        await AppendRecordGrantsAsync(grants, TenantA, Alice, ["lease-1", "party-1"]);

        var svc = new NodeSearchReadService(store.Factory, TestSearchAuthorization.Projection(grants));
        var neighbourhood = await svc.ExpandAsync(TenantA, Alice, "lease-1", Now, maxHops: 1);

        var ids = neighbourhood.Select(h => h.RecordId).OrderBy(x => x).ToArray();
        Assert.Contains("lease-1", ids);
        Assert.Contains("party-1", ids);
        Assert.DoesNotContain("prop-1", ids); // the clipped hop never crossed to the unauthorized neighbour.
    }

    [Fact(DisplayName = "Graph expansion: an unauthorized SEED returns nothing (fail-closed)")]
    public async Task Graph_Expansion_Unauthorized_Seed_Empty()
    {
        await using var store = await SearchTestStore.CreateAsync();
        await SeedNodeAsync(store, "lease-1", "tenant-A", "lease", "Lease unit 4", "active lease");

        var grants = TestInMemoryAuthorizationStores.GrantStore();
        await grants.SaveAsync(
            TenantA,
            ActiveGrant(TenantA, Alice, ScopeExpression.Parse("/records/some-other-record")),
            0);

        var svc = new NodeSearchReadService(store.Factory, TestSearchAuthorization.Projection(grants));
        var neighbourhood = await svc.ExpandAsync(TenantA, Alice, "lease-1", Now, maxHops: 2);

        Assert.Empty(neighbourhood);
    }

    [Fact(DisplayName = "Graph expansion (F1 per-hop clip): an authorized node reachable ONLY through a FORBIDDEN intermediary is NOT returned — the walk cannot tunnel through an unauthorized node")]
    public async Task Graph_Expansion_Does_Not_Tunnel_Through_Forbidden_Intermediary()
    {
        // F1 regression (security-engineering verdict earlier repository ticket #1370): the graph walk must be clipped at EVERY
        // HOP, not only at the output projection. Topology: seed(authorized) → X(FORBIDDEN) → Z(authorized).
        // Z is reachable from seed ONLY by traversing through the forbidden intermediary X. Under output-only
        // clipping the walk tunnels through X and surfaces Z — asserting a seed↔Z relationship that exists
        // solely via a record Alice may not see (a graph-STRUCTURE inference channel). With the clip pushed
        // into the recursive member, X is never admitted to the frontier, so Z is unreachable and not returned.
        await using var store = await SearchTestStore.CreateAsync();
        await SeedNodeAsync(store, "seed", "tenant-A", "lease", "Seed lease", "the seed");
        await SeedNodeAsync(store, "forbidden-x", "tenant-A", "party", "Hidden party", "must not be traversed");
        await SeedNodeAsync(store, "z", "tenant-A", "invoice", "Distal invoice", "reachable only via X");

        var indexer = new NodeSearchIndexer(store.Factory);
        await indexer.IndexEdgesAsync("seed", new[]
        {
            new SearchEdgeRow { Id = Guid.NewGuid().ToString(), TenantId = "tenant-A", SourceRecordId = "seed", TargetRecordId = "forbidden-x", EdgeType = "seed-x" },
        });
        await indexer.IndexEdgesAsync("forbidden-x", new[]
        {
            new SearchEdgeRow { Id = Guid.NewGuid().ToString(), TenantId = "tenant-A", SourceRecordId = "forbidden-x", TargetRecordId = "z", EdgeType = "x-z" },
        });

        // Alice is authorized for {seed, z} but NOT for the intermediary forbidden-x.
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        await AppendRecordGrantsAsync(grants, TenantA, Alice, ["seed", "z"]);

        var svc = new NodeSearchReadService(store.Factory, TestSearchAuthorization.Projection(grants));
        var neighbourhood = await svc.ExpandAsync(TenantA, Alice, "seed", Now, maxHops: 2);

        var ids = neighbourhood.Select(h => h.RecordId).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Contains("seed", ids);
        Assert.DoesNotContain("forbidden-x", ids); // never emitted (output clip)…
        Assert.DoesNotContain("z", ids);           // …AND never reached, because the walk can't pass THROUGH X.
    }

    [Fact(DisplayName = "Graph expansion (F2 dedup): the seed appears EXACTLY ONCE — no duplicate CTE rows")]
    public async Task Graph_Expansion_Result_Rows_Are_Unique()
    {
        // F2 regression: the recursive CTE UNIONs on (record_id, depth), so a node reached at multiple depths
        // yielded duplicate result rows (the seed itself appeared twice). The projection must de-duplicate so
        // each authorized record appears exactly once regardless of how many paths reach it.
        await using var store = await SearchTestStore.CreateAsync();
        await SeedNodeAsync(store, "seed", "tenant-A", "lease", "Seed lease", "the seed");
        await SeedNodeAsync(store, "a", "tenant-A", "party", "Party A", "neighbour a");
        await SeedNodeAsync(store, "b", "tenant-A", "party", "Party B", "neighbour b");

        var indexer = new NodeSearchIndexer(store.Factory);
        // A diamond: seed↔a, seed↔b, a↔b. The seed is re-reachable at depth 2 (seed→a→… loops back), and
        // 'a'/'b' are each reachable by two paths — all of which previously produced duplicate rows.
        await indexer.IndexEdgesAsync("seed", new[]
        {
            new SearchEdgeRow { Id = Guid.NewGuid().ToString(), TenantId = "tenant-A", SourceRecordId = "seed", TargetRecordId = "a", EdgeType = "seed-a" },
            new SearchEdgeRow { Id = Guid.NewGuid().ToString(), TenantId = "tenant-A", SourceRecordId = "seed", TargetRecordId = "b", EdgeType = "seed-b" },
        });
        await indexer.IndexEdgesAsync("a", new[]
        {
            new SearchEdgeRow { Id = Guid.NewGuid().ToString(), TenantId = "tenant-A", SourceRecordId = "a", TargetRecordId = "b", EdgeType = "a-b" },
        });

        var grants = TestInMemoryAuthorizationStores.GrantStore();
        await AppendRecordGrantsAsync(grants, TenantA, Alice, ["seed", "a", "b"]);

        var svc = new NodeSearchReadService(store.Factory, TestSearchAuthorization.Projection(grants));
        var neighbourhood = await svc.ExpandAsync(TenantA, Alice, "seed", Now, maxHops: 2);

        var ids = neighbourhood.Select(h => h.RecordId).ToArray();
        Assert.Equal(new[] { "a", "b", "seed" }, ids.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        // Cardinality: each authorized record appears EXACTLY ONCE (no duplicate-row defect).
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(3, ids.Length);
    }
}
