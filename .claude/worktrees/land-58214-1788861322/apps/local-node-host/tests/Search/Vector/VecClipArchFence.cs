using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Data.Search.Vector.Sqlite;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Search.Vector;

/// <summary>
/// <b>VecClipArchFence</b> — the G-1 fail-closed clip fence for the KG-search Slice 1b VECTOR path (ADR 0135
/// KG-search F3-lift amendment). The Slice-0 <c>SearchClipArchFence</c> proved an un-clipped FTS5 / graph read is
/// structurally impossible; this extends that to the new vec0 KNN + <c>search_vec_rows</c> surfaces, plus fences
/// the M1 no-fake-as-real gate and the G-6 per-subject-keyed index. It proves an un-clipped vector read — and an
/// unverified-model index write — is structurally impossible, not merely discouraged:
/// <list type="number">
///   <item>the SOLE producer of a vec query's <c>record_id</c> clip is <see cref="VecRecordClip"/>;</item>
///   <item>no production source OTHER than the vec0 engine issues a raw <c>vec0 MATCH</c> (the
///     <c>… embedding MATCH …</c> query signature);</item>
///   <item>no production source OTHER than the indexer / brute-force engine / acceleration sink reads
///     <c>search_vec_rows</c> (the per-subject-encrypted content table) — the same content-table side-door
///     closure as the Slice-0 fence, and a non-vacuity check;</item>
///   <item>the M1 gate (<see cref="KgModelFloorGate"/>) is the registered-floor allow-list, and the stub
///     sentinel is NOT a registered floor (a fake can't pin as real); and</item>
///   <item>the per-subject crypto is the load-bearing G-6 boundary: a row's embedding is sealed under the
///     record's subject sub-key.</item>
/// </list>
/// </summary>
public sealed class VecClipArchFence
{
    private static readonly Assembly ProductionAssembly = typeof(NodeVecIndexer).Assembly;

    // ── FENCE 1 — the read service has NO clip-omitting constructor (the G-2 resolver is required) ───────────

    [Fact(DisplayName = "vec G-1 fence: NodeVecSearchReadService REQUIRES the transactional scope resolver — no clip-omitting ctor")]
    public void VecReadService_Requires_The_Clip_Resolver()
    {
        var ctors = typeof(NodeVecSearchReadService)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        Assert.NotEmpty(ctors);
        Assert.All(ctors, c =>
            Assert.Contains(c.GetParameters(),
                p => typeof(IAuthorizedRecordSetProjection).IsAssignableFrom(p.ParameterType)));
    }

    // ── FENCE 2 — no raw vec0 MATCH outside the vec0 engine ──────────────────────────────────────────────────

    [Fact(DisplayName = "vec G-1 fence: no production source OTHER than Vec0KnnEngine issues a raw `embedding MATCH` (vec0 KNN) query")]
    public void Only_Vec0Engine_Issues_A_Raw_Vec0_Match()
    {
        var dir = LocateVectorSourceDir();
        var offenders = Directory
            .EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals("Vec0KnnEngine.cs", StringComparison.Ordinal))
            .Where(f => NonDocLines(f).Any(line => System.Text.RegularExpressions.Regex.IsMatch(
                line, @"embedding\s+MATCH", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Only Vec0KnnEngine may issue a raw vec0 `embedding MATCH` KNN. Un-clipped vec0 readers: "
            + string.Join(", ", offenders));
    }

    // ── FENCE 3 — the vec query's record clip has ONE producer (VecRecordClip) ─────────────────────────────

    [Fact(DisplayName = "vec G-1 fence: the SOLE producer of a vec query's record_id IN(...) clip is VecRecordClip")]
    public void VecRecordClip_Is_The_Sole_Clip_Producer()
    {
        var dir = LocateVectorSourceDir();
        // A `record_id IN (` literal CONSTRUCTED in CODE anywhere OTHER than VecRecordClip would be a hand-rolled
        // clip that could omit the scope. Both engines + the read service build their clip via
        // VecRecordClip.Build(...). XML-doc-comment lines (`///`) legitimately DESCRIBE the clip — they are not
        // code, so they are excluded (a doc-comment that names the clip pattern is the intended documentation).
        var offenders = Directory
            .EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals("VecRecordClip.cs", StringComparison.Ordinal))
            .Where(f => NonDocLines(f).Any(line => System.Text.RegularExpressions.Regex.IsMatch(
                line, @"record_id\s+IN\s*\(", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Only VecRecordClip may produce a vec `record_id IN (...)` clip in CODE. Hand-rolled clip producers: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// The file's lines with any COMMENT content removed (whole-line <c>///</c> / <c>//</c> lines dropped, and
    /// trailing <c>// …</c> comments stripped) — so a fence matches actual CODE, not the prose in a doc-comment
    /// or an inline comment that legitimately DESCRIBES the clip pattern.
    /// </summary>
    private static System.Collections.Generic.IEnumerable<string> NonDocLines(string path)
    {
        foreach (var raw in File.ReadLines(path))
        {
            var trimmed = raw.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue; // whole-line comment (covers both /// and //).
            }
            // Strip a trailing line-comment (// …). Not perfect for a // inside a string literal, but our SQL
            // never embeds the token "//", so this is safe for the clip-construction fences.
            var commentIdx = raw.IndexOf("//", StringComparison.Ordinal);
            yield return commentIdx >= 0 ? raw[..commentIdx] : raw;
        }
    }

    // ── FENCE 4 — the encrypted content table is read ONLY by the indexer / brute-force engine / sink ───────

    [Fact(DisplayName = "vec G-1 fence: no production source OTHER than the indexer + brute-force engine + acceleration sink reads search_vec_rows / the VecRows DbSet")]
    public void Only_Allowed_Consumers_Read_The_VecRows_Table()
    {
        var dir = LocateVectorSourceDir();

        // (a) raw SQL: a FROM/JOIN against search_vec_rows (in CODE, not a doc-comment description).
        var rawSqlOffenders = Directory
            .EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsAllowedVecRowsConsumer(Path.GetFileName(f)))
            .Where(f => NonDocLines(f).Any(line => System.Text.RegularExpressions.Regex.IsMatch(
                line, @"\b(?:FROM|JOIN)\s+search_vec_rows\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            rawSqlOffenders.Length == 0,
            "Only the indexer + brute-force engine may FROM/JOIN search_vec_rows. Offenders: "
            + string.Join(", ", rawSqlOffenders));

        // (b) the EF DbSet: a `.VecRows` access.
        var dbSetOffenders = Directory
            .EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsAllowedVecRowsDbSetConsumer(Path.GetFileName(f)))
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(
                File.ReadAllText(f), @"\.VecRows\b"))
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            dbSetOffenders.Length == 0,
            "Only the indexer may consume the VecRows DbSet. Offenders: " + string.Join(", ", dbSetOffenders));
    }

    [Fact(DisplayName = "vec G-1 fence is non-vacuous: the search_vec_rows / VecRows regexes DO fire on a planted un-clipped read")]
    public void VecRows_Fence_Is_NonVacuous()
    {
        const string plantedSql = "SELECT * FROM search_vec_rows WHERE tenant_id = $t;";
        Assert.Matches(@"\b(?:FROM|JOIN)\s+search_vec_rows\b", plantedSql);
        Assert.DoesNotMatch(@"\b(?:FROM|JOIN)\s+search_vec_rows\b", "e.ToTable(\"search_vec_rows\");");

        const string plantedDbSet = "var leak = await ctx.VecRows.ToListAsync();";
        Assert.Matches(@"\.VecRows\b", plantedDbSet);

        const string plantedMatch = "WHERE embedding MATCH $q AND k = $k";
        Assert.Matches(@"embedding\s+MATCH", plantedMatch);
    }

    // ── FENCE 5 — M1: the stub sentinel is NOT a registered floor (a fake can't pin as real) ────────────────

    [Fact(DisplayName = "M1 fence: the stub sentinel is NOT a registered floor; the only registered floor is bge-m3")]
    public void M1_Stub_Is_Not_A_Registered_Floor()
    {
        Assert.False(KgModelFloorGate.IsRegisteredFloor(KgModelFloorGate.StubModelSentinel));
        Assert.False(KgModelFloorGate.IsRegisteredFloor("unknown-model"));
        Assert.False(KgModelFloorGate.IsRegisteredFloor(null));
        Assert.True(KgModelFloorGate.IsRegisteredFloor(KgModelFloor.BgeM3.Id));
        Assert.Equal(new[] { "bge-m3" }, KgModelFloor.Registered.Select(f => f.Id).ToArray());
    }

    // ── FENCE 6 — G-6: the per-subject encryptor is a REQUIRED indexer dependency (no plaintext-embedding path) ──

    [Fact(DisplayName = "G-6 fence: NodeVecIndexer REQUIRES the per-subject field encryptor — no path writes a plaintext embedding")]
    public void Indexer_Requires_The_Subject_Encryptor()
    {
        var ctors = typeof(NodeVecIndexer).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        Assert.NotEmpty(ctors);
        Assert.All(ctors, c =>
            Assert.Contains(c.GetParameters(),
                p => p.ParameterType == typeof(Harborline.Api.Foundation.Recovery.Crypto.ISubjectFieldEncryptor)));
    }

    [Fact(DisplayName = "production assembly is Harborline.Api.LocalNodeHost (the fences scan the shipped host)")]
    public void Production_Assembly_Is_The_Host()
    {
        Assert.Equal("Harborline.Api.LocalNodeHost", ProductionAssembly.GetName().Name);
    }

    private static bool IsAllowedVecRowsConsumer(string fileName) =>
        fileName is "NodeVecIndexer.cs" or "BruteForceKnnEngine.cs" or "NodeLocalSearchDbContext.cs";

    private static bool IsAllowedVecRowsDbSetConsumer(string fileName) =>
        // The indexer writes/reads VecRows; the DbContext DECLARES the DbSet (excluded — it does not consume it).
        fileName is "NodeVecIndexer.cs" or "NodeLocalSearchDbContext.cs";

    private static string LocateVectorSourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "apps", "local-node-host", "Data", "Search", "Vector");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate apps/local-node-host/Data/Search/Vector from " + AppContext.BaseDirectory);
    }
}
