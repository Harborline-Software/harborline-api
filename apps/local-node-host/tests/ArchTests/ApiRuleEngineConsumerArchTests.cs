using System.Runtime.CompilerServices;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// T-304 / T-540 (DES-0029 ck-7 plan S7): the api's own rule-engine copy (<c>Harborline.Api.Foundation.RuleEngine</c>)
/// is retiring. The Forms submit gate and the Forms submit routes run on the platform runtime; every production
/// file still naming the api copy is listed here, so a new consumer fails this test and a migrated one must
/// leave the list. T-540 deletes the copy once the list is empty.
/// </summary>
public sealed class ApiRuleEngineConsumerArchTests
{
    private const string Copy = "Harborline.Api.Foundation.RuleEngine";

    /// <summary>The remaining consumers T-540 repoints. Each is a live api-copy caller, not a doc mention.</summary>
    internal static readonly string[] RemainingConsumers =
    [
        "apps/local-node-host/Data/Authorization/StandingCatalogue.cs",
        "apps/local-node-host/Data/Configuration/VerificationCandidateWorld.cs",
        "apps/local-node-host/Data/Configuration/VerificationRunner.cs",
        "apps/local-node-host/Data/PackProjection/PackSeedProjector.cs",
        "apps/local-node-host/Health/AuthorizationAdminRoutes.cs",
        "apps/local-node-host/Health/Catalogue.cs",
        "apps/local-node-host/Health/HostedAuthorizationAdminApiEndpoint.cs",
        "apps/local-node-host/Program.cs",
        "packages/foundation-documents/Merge/DocumentMergeContextAdapter.cs",
        "packages/foundation-documents/Rendering/DocumentRenderWalker.cs",
        "packages/foundation-forms-engine/FormEngine.cs",
        "packages/foundation-forms-engine/RuleCompileAdmission.cs",
    ];

    [Fact(DisplayName = "T-304: the production files naming the api rule-engine copy are exactly the listed T-540 consumers")]
    public void ApiRuleEngineConsumersAreTheListedSet()
    {
        var discovered = ProductionConsumers();
        Assert.Equal(RemainingConsumers.Order(StringComparer.Ordinal), discovered);
        Assert.DoesNotContain("packages/foundation-forms-engine/SubmitValidationGate.cs", discovered);
        Assert.DoesNotContain("apps/local-node-host/Health/FormsRoutes.cs", discovered);
        Assert.DoesNotContain("apps/local-node-host/Health/WebSession/SelectedFormSubmitRoutes.cs", discovered);
    }

    private static string[] ProductionConsumers()
    {
        var root = RepositoryRoot();
        return new[] { "apps", "packages" }
            .SelectMany(area => Directory.EnumerateFiles(Path.Combine(root, area), "*.cs", SearchOption.AllDirectories))
            .Select(path => (path, relative: Path.GetRelativePath(root, path).Replace('\\', '/')))
            .Where(file => !file.relative.Contains("/tests/", StringComparison.Ordinal)
                && !file.relative.Contains("/obj/", StringComparison.Ordinal)
                && !file.relative.Contains("/bin/", StringComparison.Ordinal)
                && !file.relative.StartsWith("packages/foundation-rule-engine/", StringComparison.Ordinal))
            .Where(file => File.ReadLines(file.path).Any(line =>
                !line.TrimStart().StartsWith("//", StringComparison.Ordinal) && line.Contains(Copy, StringComparison.Ordinal)))
            .Select(file => file.relative)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        var hostRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!)!;
        var repositoryRoot = Path.GetDirectoryName(Path.GetDirectoryName(hostRoot)!)!;
        Assert.True(Directory.Exists(Path.Combine(repositoryRoot, "packages")), $"repository root not found from {thisFile}");
        return repositoryRoot;
    }
}
