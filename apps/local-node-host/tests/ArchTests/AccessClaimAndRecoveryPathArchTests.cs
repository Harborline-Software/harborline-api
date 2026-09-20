using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Harborline.Api.LocalNodeHost.Data.Identity;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// T-585 item 4 — one Access claim path and one recovery path. A second path to a claim is a finding
/// naming BOTH.
/// </summary>
/// <remarks>
/// <para>
/// DES-0007 <c>platform-package-eng-8</c> (claim lifecycle: mint, expire, consume once, audit; after
/// success the route is absent) and <c>-eng-9</c> (recovery: re-import only from the original source,
/// never a vendor backdoor). DES-0032 §6: "Bootstrap is a state, not a key."
/// </para>
/// <para>
/// Each family already has its own local fence — <c>BootstrapAuthorityArchTests</c> pins who may reach
/// issuance, <c>AdministratorAuthorityBoundaryArchTests</c> pins that recovery is not an HTTP route,
/// <c>AuthorizationGateArchTests</c> pins the invitation-bootstrap capability's two decisions. What none
/// of them can see is a NEW path: a second accept route beside an existing one, or a second caller of an
/// issuer, is invisible to a fence that only knows about the path it was written for. This file
/// enumerates the families together, so the second path fails here naming both.
/// </para>
/// <para>
/// What this does NOT catch, stated rather than implied: it reads declared route constants and source
/// text. A route mapped from an interpolated string, or a claim minted through reflection, is not a shape
/// this sees. It pins the shapes a contributor writes, which is the failure mode the ADR's prior art
/// describes; it is not proof of exclusivity.
/// </para>
/// </remarks>
public sealed class AccessClaimAndRecoveryPathArchTests
{
    /// <summary>One declared HTTP route constant: where it is declared and the path it declares.</summary>
    private sealed record DeclaredRoute(string File, string Name, string Path)
    {
        public override string ToString() => $"{Name} = \"{Path}\" ({File})";
    }

    /// <summary>
    /// The families, and the ONE path each is allowed. The vocabulary is what a new route for the same
    /// family would spell; the expectation is the route that exists. A second match changes the set and
    /// the assertion prints every member of it.
    /// </summary>
    private static readonly (string Family, string Vocabulary, string Expected)[] ClaimRouteFamilies =
    [
        // The first-authority bind. FounderBindRoutes' own header says it is "the ONE route that reaches
        // InstallationFounderBindingService", and that it is deliberately reachable by no caller.
        ("first authority", @"founder-bind", "/api/session/founder-bind"),
        // The account-setup claim: one route mints the invitation, one consumes it.
        ("account-setup claim issue", @"admin/invitations", "/api/session/admin/invitations"),
        ("account-setup claim consume", @"account-setup-accept", "/api/session/account-setup-accept"),
        // Recovery. One consume route; the ADR 0066 clause 8 administrator recovery has no route at all,
        // which the case below asserts separately.
        ("recovery consume", @"recovery-accept", "/api/session/recovery-accept"),
    ];

    /// <summary>
    /// The production files allowed to reach each claim issuer. The declaring file and the composition
    /// root are excluded: declaring an interface is not a path to a claim, and registering it is not
    /// either — only a caller is.
    /// </summary>
    private static readonly (string Family, string Issuer, string[] Expected)[] ClaimIssuerCallers =
    [
        ("first authority", "IBootstrapClaimIssuer",
            ["BootstrapClaimRedemption.cs", "InstallationFounderBootstrapCeremony.cs"]),
        // Issued today by nothing: the interface is registered and has no caller and no route. That is a
        // fact worth pinning rather than rounding to "one" — the day a caller appears, this names it.
        ("account credential recovery", "IRecoveryInvitationIssuer", ["RecoveryInvitationIssuer.cs"]),
        ("account-setup claim", "IAccountSetupInvitationIssuer",
            ["AccountSetupInvitationIssuer.cs", "AdminTeamAccessAuthority.cs"]),
    ];

    private static readonly Regex RouteConstant = new(
        @"const\s+string\s+(?<name>[A-Za-z][A-Za-z0-9]*)\s*=\s*""(?<path>/api/[^""]+)""",
        RegexOptions.CultureInvariant);

    [Fact(DisplayName = "T-585 item 4: each Access claim family has exactly one route")]
    public void A_Second_Route_To_A_Claim_Names_Both_Paths()
    {
        var declared = DeclaredRoutes();
        Assert.NotEmpty(declared);

        foreach (var (family, vocabulary, expected) in ClaimRouteFamilies)
        {
            var matches = declared
                .Where(route => Regex.IsMatch(route.Path, vocabulary, RegexOptions.IgnoreCase))
                .OrderBy(route => route.Path, StringComparer.Ordinal)
                .ToArray();

            // The message is the finding: every path in the family, so a reader sees the second one and
            // the one it appeared beside rather than a count.
            Assert.True(
                matches.Length == 1 && matches[0].Path == expected,
                $"Access claim family '{family}' must have exactly one path ({expected}); found "
                + $"{matches.Length}: {string.Join(" | ", matches.Select(route => route.ToString()))}");
        }
    }

    [Fact(DisplayName = "T-585 item 4: administrator recovery stays off the claim routes entirely")]
    public void The_Administrator_Recovery_Path_Is_Not_A_Claim_Route()
    {
        // ADR 0066 clause 8's offline entry point, checked from the route side rather than the map side:
        // a route constant naming the verb would be a second recovery path beside the subcommand, and
        // AdministratorAuthorityBoundaryArchTests.The_Recovery_Path_Is_Not_An_Http_Route only sees a Map
        // call that spells the verb inline.
        var routed = DeclaredRoutes()
            .Where(route => route.Path.Contains(AdministratorRecoveryCommand.Verb, StringComparison.OrdinalIgnoreCase)
                || route.Path.Contains("recover-administrator", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(
            routed.Length == 0,
            "The administrator recovery path is offline (ADR 0066 clause 8); these routes reach it: "
            + string.Join(" | ", routed.Select(route => route.ToString())));
    }

    [Fact(DisplayName = "T-585 item 4: each claim issuer has exactly the callers it is allowed")]
    public void A_Second_Caller_Of_A_Claim_Issuer_Names_Both_Callers()
    {
        var production = EnumerateProductionSource(RepositoryRoot()).ToArray();
        Assert.NotEmpty(production);

        foreach (var (family, issuer, expected) in ClaimIssuerCallers)
        {
            var callers = production
                .Where(file => CodeOnly(File.ReadAllText(file)).Contains(issuer, StringComparison.Ordinal))
                // The composition root registers every service in the host; a registration is not a path
                // to a claim, and including it would make this assertion about DI rather than about minting.
                .Select(file => Path.GetFileName(file))
                .Where(name => name != "Program.cs")
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.True(
                callers.SequenceEqual(expected, StringComparer.Ordinal),
                $"Claim family '{family}': {issuer} must be reached by exactly "
                + $"[{string.Join(", ", expected)}]; it is reached by [{string.Join(", ", callers)}]");
        }
    }

    private static IReadOnlyList<DeclaredRoute> DeclaredRoutes() =>
        EnumerateProductionSource(RepositoryRoot())
            .SelectMany(file => RouteConstant
                .Matches(CodeOnly(File.ReadAllText(file)))
                .Select(match => new DeclaredRoute(
                    Path.GetFileName(file),
                    match.Groups["name"].Value,
                    match.Groups["path"].Value)))
            .ToArray();

    private static readonly string[] ExcludedSegments =
        ["tests", "obj", "bin", "node_modules", ".git", ".claude", ".codex", "artifacts"];

    // Matched against the path RELATIVE to the root: a git worktree can live under a directory named like
    // one of these, and an absolute-path match would exclude the whole repository and pass by finding
    // nothing. Same reasoning as AdministratorAuthorityBoundaryArchTests.
    private static IEnumerable<string> EnumerateProductionSource(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(root, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => ExcludedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase)));

    /// <summary>Strip comments so a doc comment naming a route or an issuer does not read as one.</summary>
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
            File.Exists(Path.Combine(repositoryRoot, "Harborline.Api.slnx")),
            $"Could not locate the repository root from '{thisFile}'.");
        return repositoryRoot;
    }
}
