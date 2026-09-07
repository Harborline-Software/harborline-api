using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Harborline.Api.LocalNodeHost.Data.Identity;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// ADR 0066 clause 7 — the last-usable-administrator invariant is enforced at the DATA LAYER, and there is
/// exactly one data layer to enforce it in.
/// </summary>
/// <remarks>
/// <para>
/// The invariant's documented failure mode is not "nobody wrote the check": it is "somebody added a SECOND
/// path". The ADR's prior art is Microsoft Entra's equivalent invariant, whose documented hole is that
/// another path could disable the account the invariant was protecting. Counting removal paths in review
/// does not survive the next feature; pinning them in a test does.
/// </para>
/// <para>
/// So two things are pinned here: nothing outside <see cref="NodeAdministratorAuthority"/> may write the
/// authority log, and every removal shape ADR 0066 clause 7 names must exist as a distinct event a caller is
/// forced to choose from. Adding a sixth removal path means adding an enum member, which means this test
/// tells you the invariant now covers it.
/// </para>
/// </remarks>
public sealed class AdministratorAuthorityBoundaryArchTests
{
    [Fact(DisplayName = "ADR 0066 c7: only NodeAdministratorAuthority writes the administrator log")]
    public void Only_The_Authority_Writes_The_Administrator_Log()
    {
        // Scanned across the WHOLE repository, not just this project: a writer in another assembly that
        // takes IDbContextFactory<NodeLocalRosterDbContext> is exactly as able to bypass the invariant as
        // one next door, and the narrower scan said nothing about it.
        var writers = EnumerateProductionSource(RepositoryRoot())
            .Where(file => Regex.IsMatch(
                CodeOnly(File.ReadAllText(file)),
                // The EF writer shapes, plus the table name in raw SQL. `AdministratorAuthority` catches the
                // DbSet however it is reached (a local, a property, an interpolated FromSql).
                @"AdministratorAuthority\b[^;\r\n]*\.\s*(Add|AddRange|Remove|RemoveRange|Update|ExecuteDelete|ExecuteUpdate)"
                + @"|(INSERT\s+INTO|UPDATE|DELETE\s+FROM)\s+""?administrator_authority",
                RegexOptions.IgnoreCase))
            .Select(file => Path.GetFileName(file) ?? file)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Exactly one writer. A second one would be a removal path that never sees the invariant — the
        // Entra hole, reproduced.
        //
        // WHAT THIS TEST DOES NOT CATCH, stated rather than implied: it is a source-text scan, so a writer
        // that reaches the table through a name this regex does not spell — a dynamically built SQL string,
        // a generic repository over the entity type, EF bulk extensions, or a direct SqliteCommand on the
        // file — passes. It pins the shapes a normal contributor would write, which is the failure mode the
        // ADR's prior art actually describes; it is not proof of exclusivity.
        Assert.Equal(["NodeAdministratorAuthority.cs"], writers);
    }

    [Fact(DisplayName = "ADR 0066 c7: every named removal path is a distinct, checked event")]
    public void Every_Named_Removal_Path_Is_A_Distinct_Event()
    {
        // Clause 7: "every path that can remove OR DISABLE a grant — revoke, expire, disable,
        // delete-principal, role change, import, restore, and sync". Expire is SetExpiryAsync (it changes a
        // date rather than the event); the rest are events. Import, restore and sync share one event because
        // they are one shape at this layer: a projection asserting a replacement roster.
        //
        // InstallerWindowOpened is the odd one out and is pinned here so it stays odd: it is the clause-6
        // window anchor, not a statement about any party, and AppendRemovalAsync rejects it explicitly.
        Assert.Equal(
            [
                AdministratorAuthorityEvent.Established,
                AdministratorAuthorityEvent.Revoked,
                AdministratorAuthorityEvent.Disabled,
                AdministratorAuthorityEvent.PrincipalDeleted,
                AdministratorAuthorityEvent.Demoted,
                AdministratorAuthorityEvent.ReplacedByProjection,
                AdministratorAuthorityEvent.InstallerWindowOpened,
            ],
            Enum.GetValues<AdministratorAuthorityEvent>());
    }

    [Fact(DisplayName = "ADR 0066 c8: recovery and bootstrap are distinct provenance values")]
    public void Recovery_And_Bootstrap_Are_Distinct_Provenance_Values()
    {
        // If these ever collapse into one value, "recovery is never a re-arm of the installer" becomes
        // unstatable — the seal is defined in terms of the bootstrap value alone.
        Assert.NotEqual(AdministratorProvenance.Bootstrap, AdministratorProvenance.Recovery);
        Assert.Equal(
            [
                AdministratorProvenance.None,
                AdministratorProvenance.Bootstrap,
                AdministratorProvenance.Recovery,
            ],
            Enum.GetValues<AdministratorProvenance>());
    }

    [Fact(DisplayName = "ADR 0066 c8: the recovery path is not reachable over HTTP")]
    public void The_Recovery_Path_Is_Not_An_Http_Route()
    {
        // Clause 8 requires an offline entry point. A route — even an authenticated one — would restore the
        // standing administrator-creation endpoint the migration step exists to remove.
        var routeShaped = EnumerateProductionSource(RepositoryRoot())
            .Where(file => Regex.IsMatch(
                CodeOnly(File.ReadAllText(file)),
                @"Map(Get|Post|Put|Patch|Delete)\s*\([^)]*" + Regex.Escape(AdministratorRecoveryCommand.Verb)))
            .ToArray();

        Assert.Empty(routeShaped);
    }

    private static readonly string[] ExcludedSegments =
        ["tests", "obj", "bin", "node_modules", ".git", ".claude", "artifacts"];

    // Matched against the path RELATIVE to the root: the repository itself can live under a directory
    // named like one of these (a git worktree under .claude/worktrees does), and an absolute-path match
    // would then exclude every file in the repository and pass by finding nothing.
    private static IEnumerable<string> EnumerateProductionSource(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(root, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => ExcludedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase)));

    /// <summary>Strip comments so a doc comment naming a forbidden shape does not read as that shape.</summary>
    private static string CodeOnly(string source)
    {
        var withoutBlockComments = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlockComments, @"//.*?$", string.Empty, RegexOptions.Multiline);
    }

    private static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        var archTestsDir = Path.GetDirectoryName(thisFile)!;
        var testsDir = Path.GetDirectoryName(archTestsDir)!;
        var hostRoot = Path.GetDirectoryName(testsDir)!;
        Assert.True(
            File.Exists(Path.Combine(hostRoot, "Program.cs"))
                && File.Exists(Path.Combine(hostRoot, "LocalNodeOptions.cs")),
            $"Could not locate the node-host project root from '{thisFile}'.");
        var repositoryRoot = Path.GetDirectoryName(Path.GetDirectoryName(hostRoot)!)!;
        Assert.True(
            Directory.Exists(Path.Combine(repositoryRoot, "apps"))
                && Directory.Exists(Path.Combine(repositoryRoot, "packages")),
            $"Could not locate the repository root from '{thisFile}'.");
        return repositoryRoot;
    }
}
