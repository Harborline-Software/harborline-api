using System.Runtime.CompilerServices;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// T-304 / T-540 (DES-0029 ck-7 plan S7): the api's own rule-engine copy (<c>Harborline.Api.Foundation.RuleEngine</c>)
/// is retiring. The Forms submit gate and the Forms submit routes run on the platform runtime, and since slice 2
/// so do the Forms render projection, definition-save rule admission, verification runner and the remaining T-304
/// consumers. Only the Documents walker and its merge-context adapter (the walker is the adapter's sole caller) remain,
/// because T-520 retires the walker by deletion (DES-0052 :466); a new api-copy consumer fails.
/// </summary>
public sealed class ApiRuleEngineConsumerArchTests
{
    private const string Copy = "Harborline.Api.Foundation.RuleEngine";

    /// <summary>The walker and its adapter, which T-520 retires by deletion rather than by runtime migration.</summary>
    internal static readonly string[] RemainingConsumers =
    [
        "packages/foundation-documents/Merge/DocumentMergeContextAdapter.cs",
        "packages/foundation-documents/Rendering/DocumentRenderWalker.cs",
    ];

    [Fact(DisplayName = "T-304: only the Documents walker and its adapter still name the api rule-engine copy")]
    public void ApiRuleEngineConsumersAreTheListedSet()
    {
        var discovered = ProductionConsumers();
        Assert.Equal(RemainingConsumers.Order(StringComparer.Ordinal), discovered);
        Assert.DoesNotContain("packages/foundation-forms-engine/SubmitValidationGate.cs", discovered);
        Assert.DoesNotContain("apps/local-node-host/Health/FormsRoutes.cs", discovered);
        Assert.DoesNotContain("apps/local-node-host/Health/WebSession/SelectedFormSubmitRoutes.cs", discovered);
        // T-304 slice 2.
        Assert.DoesNotContain("packages/foundation-forms-engine/FormEngine.cs", discovered);
        Assert.DoesNotContain("packages/foundation-forms-engine/RuleCompileAdmission.cs", discovered);
        Assert.DoesNotContain("packages/foundation-forms-engine/PlatformRuleContract.cs", discovered);
        Assert.DoesNotContain("apps/local-node-host/Data/Configuration/VerificationCandidateWorld.cs", discovered);
        Assert.DoesNotContain("apps/local-node-host/Data/Configuration/VerificationRunner.cs", discovered);
        Assert.DoesNotContain("apps/local-node-host/Data/Authorization/StandingCatalogue.cs", discovered);
        Assert.DoesNotContain("apps/local-node-host/Data/PackProjection/PackSeedProjector.cs", discovered);
        Assert.DoesNotContain("apps/local-node-host/Health/AuthorizationAdminRoutes.cs", discovered);
        Assert.DoesNotContain("apps/local-node-host/Health/Catalogue.cs", discovered);
        Assert.DoesNotContain("apps/local-node-host/Health/HostedAuthorizationAdminApiEndpoint.cs", discovered);
        Assert.DoesNotContain("apps/local-node-host/Program.cs", discovered);
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
