using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Ticket 205 slice 4 fix 1 fence: the route guard and the production PEP name the caller to the
/// authorization side through ONE resolution.
/// </summary>
/// <remarks>
/// The slice shipped with <c>RequestAuthorization</c> asking the gate about the canonical PARTY while
/// <c>SelectedSessionPermissionResolver</c> — and the grant store, and the closure reader — key on the
/// canonical PRINCIPAL, so every web-plane member with a live grant was refused. Nothing in the tree said
/// the two readings had to agree. This fence says it: the principal-to-actor mapping has exactly one
/// production site, <c>NodeGatePrincipal</c>, and both consumers go through it.
/// </remarks>
public sealed class SharedGatePrincipalResolutionArchTests
{
    private const string Helper = "apps/local-node-host/Data/Identity/NodeGatePrincipal.cs";

    /// <summary>The expression that turns a selected-session principal into the gate's/grant store's key.</summary>
    private static readonly Regex PrincipalToActor = new(
        @"new\s+ActorId\s*\(\s*[A-Za-z_][A-Za-z0-9_.]*\.PrincipalUserId\.Value\s*\)",
        RegexOptions.Compiled);

    /// <summary>Naming the caller to an authorization read by the attribution PARTY — the F1 defect.</summary>
    private static readonly Regex PartyToActor = new(
        @"new\s+ActorId\s*\([^)]*(?:CanonicalParty|NodeCallerParty)",
        RegexOptions.Compiled);

    [Fact]
    public void ThePrincipalToActorMappingHasExactlyOneProductionSite()
    {
        var root = RepositoryRoot();
        var sites = ProductionFiles(root)
            .Where(item => PrincipalToActor.IsMatch(File.ReadAllText(item.File)))
            .Select(item => item.Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([Helper], sites);
    }

    [Theory]
    [InlineData("apps/local-node-host/Health/RequestAuthorization.cs")]
    [InlineData("apps/local-node-host/Data/Identity/SelectedSessionPermissionResolver.cs")]
    public void TheRouteGuardAndThePepBothResolveThroughTheSharedHelper(string relative)
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Contains("NodeGatePrincipal.", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(PrincipalToActor, source);
        // The party is the attribution stamp; neither file may turn one into the actor an authorization
        // read is keyed by. (The PEP still READS CanonicalParty for the roster — that is not an actor.)
        Assert.DoesNotMatch(PartyToActor, source);
    }

    [Fact]
    public void ThePlantedDivergenceIsReported()
    {
        var root = Path.Combine(Path.GetTempPath(), "ticket-205-principal-" + Guid.NewGuid().ToString("N"));
        var planted = Path.Combine(root, "apps", "planted");
        Directory.CreateDirectory(planted);
        Directory.CreateDirectory(Path.Combine(root, "packages"));
        try
        {
            File.WriteAllText(
                Path.Combine(planted, "Diverged.cs"),
                "var actor = new ActorId(principal.PrincipalUserId.Value);");
            var sites = ProductionFiles(root)
                .Where(item => PrincipalToActor.IsMatch(File.ReadAllText(item.File)))
                .Select(item => item.Relative)
                .ToArray();

            Assert.Equal(["apps/planted/Diverged.cs"], sites);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IEnumerable<(string File, string Relative)> ProductionFiles(string root) =>
        new[] { "packages", "apps" }
            .Select(name => Path.Combine(root, name))
            .Where(Directory.Exists)
            .SelectMany(scanRoot => Directory.EnumerateFiles(scanRoot, "*.cs", SearchOption.AllDirectories))
            .Select(file => (File: file, Relative: Path.GetRelativePath(root, file).Replace('\\', '/')))
            .Where(item => !item.Relative.Split('/').Any(segment =>
                segment is "tests" or "bin" or "obj" or ".git" or ".claude"))
            .Where(item => !item.Relative.EndsWith(".Designer.cs", StringComparison.Ordinal)
                && !item.Relative.EndsWith(".g.cs", StringComparison.Ordinal)
                && !item.Relative.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase));

    private static string RepositoryRoot([CallerFilePath] string file = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "apps"))
                && Directory.Exists(Path.Combine(directory.FullName, "packages")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }
}
