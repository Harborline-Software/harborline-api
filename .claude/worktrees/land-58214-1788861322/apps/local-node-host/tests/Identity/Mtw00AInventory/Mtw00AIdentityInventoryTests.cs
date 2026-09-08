using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Harborline.Api.LocalNodeHost.Tests.Identity.Mtw00AInventory;

/// <summary>
/// MTW-00A discovery evidence: a deterministic, test-generated inventory of the current identity,
/// grant, roster, hosted-endpoint, legacy-consumer, legacy-v1-migration-source, and
/// capability-admission owners that a later multi-tenant-web card must reason about.
/// </summary>
/// <remarks>
/// <para>
/// This is DISCOVERY EVIDENCE ONLY. The inventory enumerates the current owners of each concern by
/// scanning the real source tree with fixed, sorted, deterministic rules; it asserts no runtime
/// behavior, opens no store, and touches no route/UI/schema. It is <b>not</b> multi-tenant-web
/// readiness evidence.
/// </para>
/// <para>
/// The generated artifact <c>mtw-00a-identity-inventory.generated.md</c> is committed next to this
/// test. The test regenerates the inventory from live source and compares it byte-for-byte against
/// the committed artifact, so deleting (or altering) any single discovered owner from the artifact
/// fails the gate. Regenerate after an intentional source change with the environment variable
/// <c>MTW00A_REGEN=1</c>.
/// </para>
/// </remarks>
[Trait("PlanCard", "MTW-00A")]
public sealed class Mtw00AIdentityInventoryTests
{
    [Fact(DisplayName =
        "MTW-00A: the identity/grant/roster/endpoint/state/migration/capability inventory is " +
        "deterministic (two live-scan runs are byte-identical)")]
    public void Inventory_IsDeterministic()
    {
        var first = Mtw00AInventoryGenerator.Generate();
        var second = Mtw00AInventoryGenerator.Generate();

        Assert.Equal(first, second);
    }

    [Fact(DisplayName =
        "MTW-00A: every enumerated category discovered at least one owner (a broken scan cannot " +
        "silently pass)")]
    public void Inventory_EveryCategory_IsNonEmpty()
    {
        var categories = Mtw00AInventoryGenerator.Discover();

        var emptyCategories = categories
            .Where(category => category.Owners.Count == 0)
            .Select(category => category.Key)
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            emptyCategories.Length == 0,
            "One or more MTW-00A inventory categories discovered no owners, so the scan is broken " +
            "or a subsystem moved: " + string.Join(", ", emptyCategories));
    }

    [Fact(DisplayName =
        "MTW-00A: the live-scan inventory matches the committed artifact byte-for-byte; deleting any " +
        "discovered owner from the artifact fails this gate")]
    public void Inventory_MatchesCommittedArtifact()
    {
        var expected = Mtw00AInventoryGenerator.Generate();
        var artifactPath = Mtw00AInventoryGenerator.ArtifactPath();

        if (ShouldRegenerate)
        {
            File.WriteAllText(artifactPath, expected, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        Assert.True(
            File.Exists(artifactPath),
            $"The committed MTW-00A inventory artifact is missing at '{artifactPath}'. " +
            "Regenerate it with MTW00A_REGEN=1.");

        var committed = Normalize(File.ReadAllText(artifactPath));

        Assert.True(
            string.Equals(expected, committed, StringComparison.Ordinal),
            "The committed MTW-00A inventory drifted from the live source scan. An owner was added, " +
            "removed, or renamed, or the artifact was hand-edited. Review the source change and " +
            "regenerate the artifact with MTW00A_REGEN=1 in the same reviewed change.\n" +
            FirstDifference(expected, committed));
    }

    [Fact(DisplayName =
        "MTW-00A: the gate bites — removing one discovered owner line from the artifact is detected " +
        "as a mismatch")]
    public void DeletingOneDiscoveredOwner_FailsTheComparison()
    {
        var generated = Mtw00AInventoryGenerator.Generate();
        var ownerLines = generated
            .Split('\n')
            .Where(static line => line.StartsWith("- ", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(ownerLines);

        // Delete exactly one discovered owner line and prove the byte-comparison rejects it.
        var victim = ownerLines[0];
        var index = generated.IndexOf(victim + "\n", StringComparison.Ordinal);
        Assert.True(index >= 0, "Owner line not found for the deletion proof.");
        var mutated = generated.Remove(index, victim.Length + 1);

        Assert.NotEqual(generated, mutated);
    }

    private static bool ShouldRegenerate =>
        string.Equals(
            Environment.GetEnvironmentVariable("MTW00A_REGEN"), "1", StringComparison.Ordinal);

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n");

    private static string FirstDifference(string expected, string committed)
    {
        var expectedLines = expected.Split('\n');
        var committedLines = committed.Split('\n');
        var max = Math.Max(expectedLines.Length, committedLines.Length);
        for (var i = 0; i < max; i++)
        {
            var e = i < expectedLines.Length ? expectedLines[i] : "<missing>";
            var c = i < committedLines.Length ? committedLines[i] : "<missing>";
            if (!string.Equals(e, c, StringComparison.Ordinal))
            {
                return $"  first difference at line {i + 1}:\n    live scan: {e}\n    committed: {c}";
            }
        }

        return "  (no line difference; trailing-content or length mismatch)";
    }
}

/// <summary>
/// Live-scan generator for the MTW-00A discovery inventory. Pure read-only enumeration of the source
/// tree with fixed rules; every output is sorted with <see cref="StringComparer.Ordinal"/> and
/// contains no timestamp, GUID, or machine-specific path.
/// </summary>
internal static class Mtw00AInventoryGenerator
{
    /// <summary>One enumerated inventory category and its sorted owner lines.</summary>
    internal readonly record struct Category(string Key, string Scan, IReadOnlyList<string> Owners);

    private const string ArtifactFileName = "mtw-00a-identity-inventory.generated.md";

    // Fixed legacy-v1 authority seams the current single-founder web mode binds (DSV Stage 0):
    // one founder credential + in-memory sessions, the install-global roster, and the
    // active-team -> tenant resolver. Category 6 records their declaring files.
    //
    // Ticket 194 removed StaticNodeUserContext from this list because the type is DELETED, not
    // renamed: it answered the soft-close override permission with a constant and had no consumer
    // left once ticket 205 slice 5 moved that override onto an AuthorizationGate decision. The
    // install-constant actor id it declared survives as ActiveTeamAuthorizationContext.LocalUserId,
    // which the category-5 legacy-consumer scan below still inventories by that spelling.
    private static readonly string[] LegacyV1AuthorityTypes =
    [
        "NodeWebSessionAuthority",
        "NodeTeamRoster",
        "NodeTenant",
    ];

    // Reproduced verbatim from HostedWebLegacyAuthorityDebtArchTests so category 5 stays aligned with
    // the reviewed ADR 0160 R3 legacy-authority debt scanner (same literal seam spellings).
    private static readonly Regex GlobalTenantAuthority = new(
        @"\bIActiveTeamAccessor\b|NodeTenant\s*\.\s*Resolve\s*\(|" +
        @"ActiveTeamTenantContext\s*\.\s*ProjectTenantId\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex StaticActorAuthority = new(
        @"ActiveTeamAuthorizationContext\s*\.\s*(?:NodeOperator|LocalUserId)\b|" +
        @"CurrentPrincipalSignatureRoutes\s*\.\s*ResolveCurrentPrincipal\s*\(|" +
        @"\bEnvironment\s*\.\s*UserName\b|\bLocalOperatorUserId\s*=\s*\""local\""",
        RegexOptions.Compiled);

    /// <summary>Absolute path of the committed inventory artifact (next to this source file).</summary>
    internal static string ArtifactPath([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, ArtifactFileName);

    /// <summary>Enumerate every category with its sorted, repo-relative owner list.</summary>
    internal static IReadOnlyList<Category> Discover([CallerFilePath] string thisFile = "")
    {
        var hostRoot = NodeHostProjectRoot(thisFile);
        var repoRoot = RepoRoot(hostRoot);
        var capabilityRoot = Path.Combine(repoRoot, "packages", "foundation-capability-admission");

        return
        [
            new Category(
                "category-1-installation-identity-stores-and-records",
                "apps/local-node-host/Data/Identity/**/*.cs excluding Migrations/",
                SortedRepoPaths(
                    repoRoot,
                    EnumerateCs(Path.Combine(hostRoot, "Data", "Identity"))
                        .Where(file => !IsUnder(file, Path.Combine(hostRoot, "Data", "Identity", "Migrations"))))),

            new Category(
                "category-2-grant-store-surface",
                "apps/local-node-host/Data/Search/**/*Grant*.cs",
                SortedRepoPaths(
                    repoRoot,
                    EnumerateCs(Path.Combine(hostRoot, "Data", "Search"))
                        .Where(file => Path.GetFileName(file).Contains("Grant", StringComparison.Ordinal)))),

            new Category(
                "category-3-roster-authority",
                "apps/local-node-host/Data/Roster/**/*.cs excluding Migrations/, plus Enrollment/NodeTeamRoster.cs",
                SortedRepoPaths(
                    repoRoot,
                    EnumerateCs(Path.Combine(hostRoot, "Data", "Roster"))
                        .Where(file => !IsUnder(file, Path.Combine(hostRoot, "Data", "Roster", "Migrations")))
                        .Append(Path.Combine(hostRoot, "Enrollment", "NodeTeamRoster.cs")))),

            new Category(
                "category-4-hosted-web-endpoints",
                "apps/local-node-host/Health/WebSession/**/*.cs, plus Health/SharedHostedWebApp.cs",
                SortedRepoPaths(
                    repoRoot,
                    EnumerateCs(Path.Combine(hostRoot, "Health", "WebSession"))
                        .Append(Path.Combine(hostRoot, "Health", "SharedHostedWebApp.cs")))),

            new Category(
                "category-5-legacy-active-team-and-static-actor-consumers",
                "apps/local-node-host/{Health,Feed}/**/*.cs matching the ADR0153-R3 legacy-authority " +
                "regexes; each line is PATH<TAB>tenant-global-hits<TAB>static-actor-hits",
                ScanLegacyConsumers(hostRoot, repoRoot)),

            new Category(
                "category-6-legacy-v1-migration-sources",
                "declaring file of each fixed legacy-v1 authority type; each line is PATH<TAB>declared-type",
                ScanLegacyV1Sources(hostRoot, repoRoot)),

            new Category(
                "category-7-foundation-capability-admission-substrate",
                "packages/foundation-capability-admission/**/*.cs excluding tests/",
                SortedRepoPaths(
                    repoRoot,
                    EnumerateCs(capabilityRoot)
                        .Where(file => !IsUnder(file, Path.Combine(capabilityRoot, "tests"))))),
        ];
    }

    /// <summary>Render the full, deterministic inventory artifact text (LF-terminated lines).</summary>
    internal static string Generate([CallerFilePath] string thisFile = "")
    {
        var builder = new StringBuilder();
        builder.Append("# MTW-00A Identity / Grant / Roster / Endpoint / State / Migration / Capability Inventory\n");
        builder.Append('\n');
        builder.Append("Deterministic discovery snapshot generated by the MTW-00A test\n");
        builder.Append("(`Harborline.Api.LocalNodeHost.Tests`). Regenerate with `MTW00A_REGEN=1`.\n");
        builder.Append('\n');
        builder.Append("This is DISCOVERY EVIDENCE ONLY. It enumerates the current owners a later\n");
        builder.Append("multi-tenant-web card must reason about, by scanning the real source tree with\n");
        builder.Append("fixed, sorted rules. It asserts no runtime behavior and is NOT multi-tenant-web\n");
        builder.Append("readiness evidence.\n");

        foreach (var category in Discover(thisFile))
        {
            builder.Append('\n');
            builder.Append("## ").Append(category.Key).Append('\n');
            builder.Append("scan: ").Append(category.Scan).Append('\n');
            builder.Append("owner-count: ").Append(category.Owners.Count).Append('\n');
            foreach (var owner in category.Owners)
            {
                builder.Append("- ").Append(owner).Append('\n');
            }
        }

        return builder.ToString();
    }

    private static IReadOnlyList<string> ScanLegacyConsumers(string hostRoot, string repoRoot)
    {
        var entries = new List<string>();
        foreach (var surface in new[] { "Health", "Feed" })
        {
            var surfaceRoot = Path.Combine(hostRoot, surface);
            if (!Directory.Exists(surfaceRoot))
            {
                continue;
            }

            foreach (var file in EnumerateCs(surfaceRoot))
            {
                var source = File.ReadAllText(file);
                var globalTenant = GlobalTenantAuthority.Matches(source).Count;
                var staticActor = StaticActorAuthority.Matches(source).Count;
                if (globalTenant == 0 && staticActor == 0)
                {
                    continue;
                }

                entries.Add($"{RepoRelative(repoRoot, file)}\t{globalTenant}\t{staticActor}");
            }
        }

        entries.Sort(StringComparer.Ordinal);
        return entries;
    }

    private static IReadOnlyList<string> ScanLegacyV1Sources(string hostRoot, string repoRoot)
    {
        var entries = new List<string>();
        foreach (var file in EnumerateCs(hostRoot))
        {
            if (IsUnder(file, Path.Combine(hostRoot, "tests")))
            {
                continue;
            }

            var source = File.ReadAllText(file);
            foreach (var type in LegacyV1AuthorityTypes)
            {
                var declaration = new Regex(
                    @"\b(class|interface|record|struct|enum)\s+" + Regex.Escape(type) + @"\b");
                if (declaration.IsMatch(source))
                {
                    entries.Add($"{RepoRelative(repoRoot, file)}\t{type}");
                }
            }
        }

        entries.Sort(StringComparer.Ordinal);
        return entries;
    }

    private static IEnumerable<string> EnumerateCs(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(file => !IsUnderNamed(file, "obj") && !IsUnderNamed(file, "bin"))
            : [];

    private static IReadOnlyList<string> SortedRepoPaths(string repoRoot, IEnumerable<string> files)
    {
        var paths = files
            .Where(File.Exists)
            .Select(file => RepoRelative(repoRoot, file))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    private static string RepoRelative(string repoRoot, string file) =>
        Path.GetRelativePath(repoRoot, Path.GetFullPath(file)).Replace('\\', '/');

    private static bool IsUnder(string file, string directory)
    {
        var full = Path.GetFullPath(file);
        var dir = Path.GetFullPath(directory);
        return full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    // Excludes bin/obj at any depth. Never uses a bare "bin" segment match that could short-circuit a
    // legitimate path (fleet-conventions: never treat a bare "bin" as a walk marker).
    private static bool IsUnderNamed(string file, string segment)
    {
        var marker = Path.DirectorySeparatorChar + segment + Path.DirectorySeparatorChar;
        return Path.GetFullPath(file).Contains(marker, StringComparison.Ordinal);
    }

    private static string NodeHostProjectRoot(string thisFile)
    {
        // thisFile = .../apps/local-node-host/tests/Identity/Mtw00AInventory/Mtw00AIdentityInventoryTests.cs
        var inventoryDir = Path.GetDirectoryName(thisFile)!;   // .../tests/Identity/Mtw00AInventory
        var identityDir = Path.GetDirectoryName(inventoryDir)!; // .../tests/Identity
        var testsDir = Path.GetDirectoryName(identityDir)!;     // .../tests
        var hostRoot = Path.GetDirectoryName(testsDir)!;        // .../apps/local-node-host
        Assert.True(
            File.Exists(Path.Combine(hostRoot, "Harborline.LocalNodeHost.csproj")),
            $"Could not locate the node-host project root from '{thisFile}' (resolved '{hostRoot}').");
        return hostRoot;
    }

    private static string RepoRoot(string hostRoot)
    {
        var appsDir = Path.GetDirectoryName(hostRoot)!; // .../apps
        var repoRoot = Path.GetDirectoryName(appsDir)!; // repo root
        Assert.True(
            Directory.Exists(Path.Combine(repoRoot, "packages", "foundation-capability-admission")),
            $"Could not locate the repo root from host root '{hostRoot}' (resolved '{repoRoot}').");
        return repoRoot;
    }
}
