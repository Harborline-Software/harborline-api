using System;
using System.IO;
using System.Linq;
using System.Reflection;

using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Generation;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Search.Generation;

/// <summary>
/// <b>GenerationClipArchFence</b> — the structural fences for KG-search Slice 2-foundation (ADR 0135 — the safe
/// interim generative GraphRAG). Proves the load-bearing safety properties are STRUCTURAL (un-bypassable), not
/// merely code-reviewed (the same no-mock-crypto / no-side-door discipline as <c>VecClipArchFence</c>):
/// <list type="number">
///   <item><b>G-G2</b> — the grounding assembler's grounding-producing method takes an
///     <see cref="ClippedGrounding"/> (which carries the <see cref="AuthorizedRecordScope"/>), NOT a bare
///     id-list — the clip is carried structurally;</item>
///   <item><b>G-G2</b> — the assembler does NOT read the <c>search_nodes</c> content table directly (the
///     clipped read service is the SOLE content reader; the <c>SearchClipArchFence</c> forbids any other);</item>
///   <item><b>firewall / proposal-only</b> — the generation provider seam (<see cref="IKgGenerationProvider"/>)
///     has NO autonomous-action verb (no send/apply/act); its only output is a <see cref="KgGenerationProposal"/>
///     — proposal-only is structural;</item>
///   <item><b>M-G1</b> — the stub sentinel is NOT a registered generation floor (a fake can't pin as a genuine
///     grounded answer); and</item>
///   <item><b>fail-closed</b> — the <see cref="GroundedProposalService"/> requires the clipped read service (no
///     no-grounding constructor that bypasses the clip).</item>
/// </list>
/// </summary>
public sealed class GenerationClipArchFence
{
    private static readonly Assembly ProductionAssembly = typeof(GroundedProposalService).Assembly;

    // ── FENCE 1 — the assembler takes an AuthorizedRecordScope (ClippedGrounding), not a bare id-list ─────

    [Fact(DisplayName = "G-G2 fence: GroundingAssembler.Assemble takes a ClippedGrounding (carrying the AuthorizedRecordScope) — never a bare id-list")]
    public void Assembler_Takes_The_Clipped_Scope_Not_A_Bare_IdList()
    {
        var assembleMethods = typeof(GroundingAssembler)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == nameof(GroundingAssembler.Assemble))
            .ToArray();

        Assert.NotEmpty(assembleMethods);
        // EVERY public Assemble overload must take a ClippedGrounding (which CARRIES the AuthorizedRecordScope).
        // There is NO overload that assembles grounding from a bare IEnumerable<string> id-list with no scope.
        Assert.All(assembleMethods, m =>
            Assert.Contains(m.GetParameters(), p => p.ParameterType == typeof(ClippedGrounding)));

        // ClippedGrounding genuinely carries the scope (so "takes a ClippedGrounding" == "takes the scope").
        Assert.NotNull(typeof(ClippedGrounding).GetProperty(nameof(ClippedGrounding.Scope)));
        Assert.Equal(typeof(AuthorizedRecordScope),
            typeof(ClippedGrounding).GetProperty(nameof(ClippedGrounding.Scope))!.PropertyType);
    }

    // ── FENCE 2 — the Generation source dir reads NO content table directly (clip side-door closure) ──────

    [Fact(DisplayName = "G-G2 fence: no Generation source reads search_nodes / the Nodes DbSet directly (the clipped read service is the SOLE content reader)")]
    public void Generation_Does_Not_Read_The_Content_Table_Directly()
    {
        var dir = LocateGenerationSourceDir();

        // A raw FROM/JOIN search_nodes, or a `.Nodes` DbSet access, anywhere under Generation/ would be a
        // clip-omitting content read (the SearchClipArchFence already forbids non-read-service readers fleet-wide;
        // this fence keeps the Generation layer specifically clean — grounding text comes ONLY through the read
        // service's clipped RetrieveClippedGroundingAsync).
        var rawSqlOffenders = Directory
            .EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => NonDocLines(f).Any(line => System.Text.RegularExpressions.Regex.IsMatch(
                line, @"\b(?:FROM|JOIN)\s+search_nodes\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            rawSqlOffenders.Length == 0,
            "No Generation source may read search_nodes directly (use the clipped read service). Offenders: "
            + string.Join(", ", rawSqlOffenders));

        var dbSetOffenders = Directory
            .EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f), @"\.Nodes\b"))
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            dbSetOffenders.Length == 0,
            "No Generation source may consume the search_nodes Nodes DbSet directly. Offenders: "
            + string.Join(", ", dbSetOffenders));
    }

    [Fact(DisplayName = "G-G2 content-table fence is non-vacuous: the regex DOES fire on a planted un-clipped read")]
    public void ContentTable_Fence_Is_NonVacuous()
    {
        const string planted = "SELECT title FROM search_nodes WHERE tenant_id = $t;";
        Assert.Matches(@"\b(?:FROM|JOIN)\s+search_nodes\b", planted);
        Assert.DoesNotMatch(@"\b(?:FROM|JOIN)\s+search_nodes\b", "e.ToTable(\"search_nodes\");");
        Assert.Matches(@"\.Nodes\b", "var leak = await ctx.Nodes.ToListAsync();");
    }

    // ── FENCE 3 — the generation provider seam has NO autonomous-action verb (proposal-only is structural) ─

    [Fact(DisplayName = "proposal-only fence: IKgGenerationProvider has NO send/apply/act verb — its only output is a KgGenerationProposal")]
    public void Generation_Seam_Has_No_Autonomous_Action_Verb()
    {
        var methods = typeof(IKgGenerationProvider)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName) // exclude the Model property getter
            .ToArray();

        // The ONLY method is GenerateAsync, and it returns a Task<KgGenerationProposal> — there is no
        // send/apply/act/post/execute verb on the seam. Proposal-only is structural: the model's output cannot be
        // anything but a proposal handed back to the caller.
        Assert.All(methods, m =>
            Assert.DoesNotMatch(
                @"(?i)send|apply|act|post|execute|commit|approve|confirm",
                m.Name));

        var generate = typeof(IKgGenerationProvider).GetMethod(nameof(IKgGenerationProvider.GenerateAsync));
        Assert.NotNull(generate);
        Assert.Equal(typeof(System.Threading.Tasks.Task<KgGenerationProposal>), generate!.ReturnType);

        // And the GroundedProposalService's only public output verb is ProposeAsync → a proposal (no act surface).
        var svcMethods = typeof(GroundedProposalService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ToArray();
        Assert.All(svcMethods, n =>
            Assert.DoesNotMatch(@"(?i)send|apply|act\b|post|execute|commit|approve", n));
        Assert.Contains(nameof(GroundedProposalService.ProposeAsync), svcMethods);
    }

    // ── FENCE 4 — M-G1: the stub sentinel is NOT a registered floor ──────────────────────────────────────

    [Fact(DisplayName = "M-G1 fence: the stub sentinel is NOT a registered generation floor; the only floor is qwen2.5-7b-instruct")]
    public void M_G1_Stub_Is_Not_A_Registered_Floor()
    {
        Assert.False(KgGenerateFloorGate.IsRegisteredFloor(KgGenerateFloorGate.StubModelSentinel));
        Assert.False(KgGenerateFloorGate.IsRegisteredFloor("unknown-model"));
        Assert.False(KgGenerateFloorGate.IsRegisteredFloor(null));
        Assert.True(KgGenerateFloorGate.IsRegisteredFloor(KgGenerateFloor.Qwen25.Id));
        Assert.Equal(new[] { "qwen2.5-7b-instruct" }, KgGenerateFloor.Registered.Select(f => f.Id).ToArray());
    }

    // ── FENCE 5 — the service REQUIRES the clipped read service (no clip-bypassing ctor) ─────────────────

    [Fact(DisplayName = "fail-closed fence: GroundedProposalService REQUIRES the clipped read service (no no-grounding ctor)")]
    public void Service_Requires_The_Clipped_Read_Service()
    {
        var ctors = typeof(GroundedProposalService).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        Assert.NotEmpty(ctors);
        Assert.All(ctors, c =>
            Assert.Contains(c.GetParameters(), p => p.ParameterType == typeof(NodeVecSearchReadService)));
        // and it requires the GroundingAssembler service (the G-G2 scope dependency).
        Assert.All(ctors, c =>
            Assert.Contains(c.GetParameters(), p => p.ParameterType == typeof(GroundingAssembler)));
    }

    [Fact(DisplayName = "production assembly is Harborline.Api.LocalNodeHost (the fences scan the shipped host)")]
    public void Production_Assembly_Is_The_Host()
    {
        Assert.Equal("Harborline.Api.LocalNodeHost", ProductionAssembly.GetName().Name);
    }

    private static System.Collections.Generic.IEnumerable<string> NonDocLines(string path)
    {
        foreach (var raw in File.ReadLines(path))
        {
            var trimmed = raw.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }
            var commentIdx = raw.IndexOf("//", StringComparison.Ordinal);
            yield return commentIdx >= 0 ? raw[..commentIdx] : raw;
        }
    }

    private static string LocateGenerationSourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "apps", "local-node-host", "Data", "Search", "Generation");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate apps/local-node-host/Data/Search/Generation from " + AppContext.BaseDirectory);
    }
}
