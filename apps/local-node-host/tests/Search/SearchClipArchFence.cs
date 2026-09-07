using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Search;

/// <summary>
/// <b>SearchClipArchFence</b> — the G-1 fail-closed clip fence for KG-search Slice 0 (ADR 0135 KG-search
/// F3-lift amendment; the same no-mock-crypto / no-side-door pattern as
/// <c>DekPairingResolverArchFence</c>). It proves an UN-CLIPPED FTS5 / graph query is <b>structurally
/// impossible</b>, not merely discouraged:
/// <list type="number">
///   <item>the read service has NO constructor that omits the clip — the clip is a required dependency;</item>
///   <item>the SOLE producer of the query <c>WHERE</c> is <see cref="AuthorizedRecordScope"/>, and the SOLE
///     producer of THAT is <see cref="IAuthorizedRecordSetProjection"/> — the read service consumes only it;</item>
///   <item>the production assembly's ONLY <see cref="IAuthorizedRecordSetProjection"/> impl is the
///     grant-backed projection (no "no-clip" / "all-records" stand-in ships);</item>
///   <item>no production type OTHER than <see cref="NodeSearchReadService"/> references the FTS5
///     <c>search_fts</c> table / a raw <c>MATCH</c> — the raw query path is encapsulated;</item>
///   <item>no production type OTHER than the read service + the indexer reads the underlying
///     <c>search_nodes</c> CONTENT table (the same plaintext the FTS index mirrors) — closing the
///     clip-omitting side door the FTS-only fence (item 4) does not cover, and a non-vacuity check proving
///     the content-table fence actually fires on a planted un-clipped read; and</item>
///   <item>the leak test against the REAL grant-backed projection: a principal with no/wrong grant gets the
///     fail-closed Empty scope and the read returns NOTHING (a green leak test against a stand-in would be a
///     false positive — bug-1312 forward).</item>
/// </list>
/// </summary>
public sealed class SearchClipArchFence
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);
    private static readonly TenantId TenantA = TenantId.FromString("tenant-A");
    private static readonly ActorId Alice = new("alice");

    private static readonly Assembly ProductionAssembly = typeof(NodeSearchReadService).Assembly;

    // ── FENCE 1 — the read service has NO clip-omitting constructor (the clip is required) ───────────────

    [Fact(DisplayName = "G-1 fence: NodeSearchReadService has NO constructor that omits the clip — it is a required dependency")]
    public void ReadService_Has_No_Constructor_Without_The_Clip()
    {
        var ctors = typeof(NodeSearchReadService)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        Assert.NotEmpty(ctors);
        // EVERY public constructor must take the clip projection — there is no "no-clip" overload.
        Assert.All(ctors, c =>
            Assert.Contains(c.GetParameters(),
                p => typeof(IAuthorizedRecordSetProjection).IsAssignableFrom(p.ParameterType)));
    }

    // ── FENCE 2 — the production assembly's ONLY clip impl is the closure-backed projection ──────────────

    [Fact(DisplayName = "G-1 fence: the SHIPPED assembly's ONLY IAuthorizedRecordSetProjection impl is closure-backed (NO no-clip stand-in)")]
    public void ProductionAssembly_Clip_Is_ClosureBacked_Only()
    {
        Assert.Equal("Harborline.Api.LocalNodeHost", ProductionAssembly.GetName().Name);

        var clipImpls = ProductionAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && typeof(IAuthorizedRecordSetProjection).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // The ONLY shipped clip is the closure-backed projection. There is NO "all-records" / "no-clip"
        // alternative in production.
        Assert.Equal(new[] { nameof(ClosureAuthorizedRecordSetProjection) }, clipImpls);
    }

    // ── FENCE 3 — the DI wiring registers the closure-backed clip (the production override) ──────────────

    [Fact(DisplayName = "G-1 fence: AddNodeKnowledgeGraphSearch registers the closure-backed clip as the SOLE IAuthorizedRecordSetProjection")]
    public void Wiring_Registers_ClosureBacked_Clip()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGrantStore, InMemoryGrantStore>();
        services.AddSingleton<IAuthorizationClosureReader>(
            new Harborline.Api.LocalNodeHost.Tests.Identity.FixedAuthorizationClosure());
        services.AddNodeKnowledgeGraphSearch();

        using var sp = services.BuildServiceProvider();
        var clip = sp.GetRequiredService<IAuthorizedRecordSetProjection>();

        Assert.IsType<ClosureAuthorizedRecordSetProjection>(clip);
    }

    // ── FENCE 4 — the raw FTS5 MATCH path is encapsulated in the read service ONLY ───────────────────────

    [Fact(DisplayName = "G-1 fence: no production source file OTHER than NodeSearchReadService issues a raw FTS5 MATCH query")]
    public void Only_The_ReadService_Issues_A_Raw_Fts_Match()
    {
        // A raw FTS5 MATCH query against the index must live ONLY in a CLIPPED read service. Any OTHER
        // production source that issues `search_fts MATCH …` could be an un-clipped side door. Detect the
        // QUERY signature (`search_fts MATCH`) — NOT the bare table name (which appears legitimately in XML
        // doc-comments + in the migration's CREATE/trigger DDL). The migration dir is excluded outright
        // (it CREATEs the table; it does not read it). The graph-expansion CTE is also in the read service.
        // The allow-list has TWO clipped readers since Slice 1b: NodeSearchReadService (the Slice-0 FTS/graph
        // surface) AND NodeVecSearchReadService (the Slice-1b hybrid surface whose lexical leg is the SAME
        // clipped FTS read — it builds its WHERE via VecRecordClip from the fail-closed scope, so it is a
        // clip-bound reader, not a side door).
        var allowedFtsReaders = new[] { "NodeSearchReadService.cs", "NodeVecSearchReadService.cs" };
        var searchDir = LocateSearchSourceDir();
        var offenders = Directory
            .EnumerateFiles(searchDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !allowedFtsReaders.Contains(Path.GetFileName(f), StringComparer.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)) // the migration legitimately CREATEs search_fts (DDL, not a read).
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                // The actual FTS5 read signature: a MATCH against the search_fts table. Tolerant of
                // whitespace between the table name and the MATCH keyword.
                return System.Text.RegularExpressions.Regex.IsMatch(
                    text, @"search_fts\s+MATCH", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            })
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Only NodeSearchReadService may issue a raw FTS5 MATCH against search_fts (the clipped path). " +
            "Un-clipped FTS5 readers found: " + string.Join(", ", offenders));
    }

    // ── FENCE 5 — the REAL leak test against the REAL closure-backed clip (NOT a stand-in) ───────────────

    [Fact(DisplayName = "G-1 leak test (REAL clip): a principal with NO grant gets the fail-closed Empty scope — the read returns NOTHING")]
    public async Task Leak_Test_No_Grant_Is_FailClosed_Against_Real_Clip()
    {
        // The no-leak guarantee runs through the real closure projection, not a construction stand-in
        // (a green leak test against a stand-in would be a false positive; bug-1312).
        await using var store = await SearchTestStore.CreateAsync();
        var indexer = new NodeSearchIndexer(store.Factory);
        await indexer.IndexNodeAsync(new SearchNodeRow
        {
            RecordId = "inv-1", TenantId = "tenant-A", NodeType = "invoice",
            Title = "confidential rent invoice", Body = "tenant-A only", Residency = SearchResidency.Cache,
        });

        await using (var db = store.CreateContext())
        {
            const string definitionId = "10000000-0000-0000-0000-000000000099";
            db.AuthorizationDefinitions.Add(new AuthorizationDefinitionRow
            {
                DefinitionId = definitionId, Revision = 1, PublisherPackageId = "test",
                Operation = "records:read", ScopeType = 0, ScopeValue = "/",
            });
            db.AuthorizationOfferedRoles.Add(new AuthorizationOfferedRoleRow
            {
                DefinitionId = definitionId, Revision = 1,
                Vocabulary = TestSearchAuthorization.RecordsReader.Vocabulary,
                RoleName = TestSearchAuthorization.RecordsReader.Name,
            });
            await db.SaveChangesAsync();
        }
        var grants = new NodeEfGrantStore(store.Factory); // Alice initially holds NO grant.
        var clip = new ClosureAuthorizedRecordSetProjection();

        // And the read service returns nothing for an unauthorized principal.
        var svc = new NodeSearchReadService(store.Factory, clip);
        Assert.Empty(await svc.SearchAsync(TenantA, Alice, "rent", Now));

        // Contrast: a principal WITH an active grant for inv-1 DOES retrieve it (the clip is the boundary —
        // grant, not identity-claim, gates the read).
        await grants.AppendAsync(TenantA, TestSearchAuthorization.Grant(
            TenantA, Alice, ScopeExpression.Parse("/records/inv-1"), Now));
        var authorized = await svc.SearchAsync(TenantA, Alice, "rent", Now);
        Assert.Equal(new[] { "inv-1" }, authorized.Select(h => h.RecordId).ToArray());
    }

    // ── FENCE 6 — the search_nodes CONTENT table is read ONLY through the read service / indexer ──────────

    [Fact(DisplayName = "G-1 fence: no production source OTHER than the read service + indexer reads the search_nodes content table (the clip-omitting side door the FTS-only fence missed)")]
    public void Only_The_ReadService_And_Indexer_Read_The_Content_Table()
    {
        // FENCE 4 above proves the FTS5 `search_fts MATCH` path is encapsulated — but the SAME plaintext
        // title/body lives in the underlying `search_nodes` content table, exposed as the `Nodes` DbSet. A
        // future un-clipped reader of search_nodes (a plain EF `ctx.Nodes` query, or a raw
        // `FROM/JOIN search_nodes` SQL) returns the same content and would NOT be caught by the FTS-only
        // fence — so "an un-clipped read is structurally impossible" only held for the FTS path, not the
        // content table. This fence closes that side door: only the clipped read service (which always
        // applies the scope WHERE) and the indexer (which writes / existence-checks, never projecting content
        // OUT) may touch search_nodes / Nodes. Any other production source is a clip-omitting read path.
        var searchDir = LocateSearchSourceDir();

        // (a) Raw-SQL content-table reads: a `FROM`/`JOIN search_nodes` against the content table. Only a
        //     CLIPPED read service issues these (always inside the clip's WHERE). The DbContext maps the table
        //     via `ToTable("search_nodes")` (a STRING literal, not FROM/JOIN) and the migration CREATEs it —
        //     both excluded by the SQL-keyword-adjacency pattern + the migration-dir filter. Since Slice 1b the
        //     allow-list has TWO clipped readers: NodeSearchReadService (Slice-0) AND NodeVecSearchReadService
        //     (the Slice-1b hybrid surface — its lexical leg JOINs search_nodes inside the SAME VecRecordClip
        //     WHERE, so it is clip-bound, not a side door).
        var allowedRawNodeReaders = new[] { "NodeSearchReadService.cs", "NodeVecSearchReadService.cs" };
        var rawSqlOffenders = Directory
            .EnumerateFiles(searchDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !allowedRawNodeReaders.Contains(Path.GetFileName(f), StringComparer.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(
                File.ReadAllText(f),
                @"\b(?:FROM|JOIN)\s+search_nodes\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            rawSqlOffenders.Length == 0,
            "Only a clipped read service may issue a raw FROM/JOIN against the search_nodes content table " +
            "(the clipped path). Un-clipped content-table SQL readers found: " + string.Join(", ", rawSqlOffenders));

        // (b) EF DbSet content reads: a `ctx.Nodes` / `.Nodes` access. Only the read service (none today)
        //     and the indexer (upsert existence-check + delete — never a content projection returned to a
        //     caller) may reference the Nodes DbSet. The DbContext DECLARES the DbSet (`Set<SearchNodeRow>()`
        //     / `public DbSet<SearchNodeRow> Nodes =>`) — that declaration is excluded; we forbid CONSUMING
        //     `.Nodes` from anywhere else.
        var allowedDbSetConsumers = new[]
        {
            "NodeSearchReadService.cs",
            "NodeSearchIndexer.cs",
            "NodeLocalSearchDbContext.cs", // declares the DbSet; does not consume it.
        };
        var dbSetOffenders = Directory
            .EnumerateFiles(searchDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !allowedDbSetConsumers.Contains(Path.GetFileName(f), StringComparer.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(
                File.ReadAllText(f),
                @"\.Nodes\b",
                System.Text.RegularExpressions.RegexOptions.None))
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            dbSetOffenders.Length == 0,
            "Only the read service + indexer may reference the search_nodes `Nodes` DbSet. Other consumers " +
            "(potential clip-omitting content reads) found: " + string.Join(", ", dbSetOffenders));
    }

    // ── FENCE 7 — the content-table fence is NON-VACUOUS (it would FIRE on a planted side door) ───────────

    [Fact(DisplayName = "G-1 fence is non-vacuous: a planted un-clipped `FROM search_nodes` read IS detected by the content-table regex")]
    public void ContentTable_Fence_Is_NonVacuous()
    {
        // Guard against the fence silently passing because its pattern never matches anything (a vacuous
        // arch-test is worse than none — it asserts a guarantee it cannot actually check). Prove the regex
        // the fence uses DOES fire on a representative un-clipped content read, and does NOT fire on the
        // legitimate DbContext mapping (a string literal) — so a real side door would be caught.
        const string plantedSideDoor = "SELECT title, body FROM search_nodes WHERE tenant_id = $t;";
        const string legitimateMapping = "e.ToTable(\"search_nodes\");"; // the DbContext's table mapping.

        Assert.Matches(@"\b(?:FROM|JOIN)\s+search_nodes\b", plantedSideDoor);
        Assert.DoesNotMatch(@"\b(?:FROM|JOIN)\s+search_nodes\b", legitimateMapping);

        const string plantedDbSetRead = "var leak = await ctx.Nodes.ToListAsync();";
        Assert.Matches(@"\.Nodes\b", plantedDbSetRead);
    }

    [Fact(DisplayName = "ADR 0072 fence: no Data/Search production source orders by global bm25 corpus statistics")]
    public void Production_Search_Source_Has_No_Global_Bm25_Order()
    {
        var searchDir = LocateSearchSourceDir();
        var offenders = Directory
            .EnumerateFiles(searchDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => ContainsGlobalBm25Order(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Data/Search production source must rank only within the authorized clip. Global bm25 ordering " +
            "found in: " + string.Join(", ", offenders));

        const string scratchSource = "SELECT record_id FROM search_fts ORDER BY bm25(search_fts);";
        Assert.True(ContainsGlobalBm25Order(scratchSource));
    }

    private static bool ContainsGlobalBm25Order(string source) =>
        source.Contains("ORDER BY bm25(", StringComparison.OrdinalIgnoreCase);

    private static string LocateSearchSourceDir()
    {
        // Walk up from the test assembly location to the repo, then into the host's Data/Search source dir.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName, "apps", "local-node-host", "Data", "Search");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate apps/local-node-host/Data/Search from " + AppContext.BaseDirectory);
    }
}
