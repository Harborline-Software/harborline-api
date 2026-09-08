using System.Xml.Linq;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Enforces the Harborline API's project-reference tier boundaries directly from the
/// repository's <c>*.csproj</c> graph.
/// </summary>
/// <remarks>
/// <para>
/// The package tiers form an inward dependency model: kernel must not depend on
/// foundation or blocks, and foundation must not depend on blocks. The contracts
/// lane is purer still and may reference only projects beneath
/// <c>packages/contracts/</c>.
/// </para>
/// <para>
/// Package projects must never reach outward into applications. These assertions
/// inspect declared <c>ProjectReference</c> edges rather than loaded assemblies, so
/// unloaded packages and test projects remain inside the architecture fence.
/// </para>
/// </remarks>
public sealed class ApiTierDependencyArchTests
{
    // Harborline.Api.Kernel self-describes in its csproj as a "Kernel contract facade ...
    // type-forwarded wrappers over Harborline.Api.Foundation primitives". During the
    // migration era the entire kernel tier therefore deliberately sits on Foundation.
    // Each edge is pinned until the facade inversion lands, at which point
    // this list must shrink to empty. One edge per line keeps debt-removal diffs visible.
    private static readonly HashSet<(string Source, string Target)> KernelFoundationFacadeExemptions =
    [
        ("packages/kernel-audit/Harborline.Kernel.Audit.csproj", "packages/foundation/Harborline.Foundation.csproj"),
        // Ticket 199: authorized audit copies the carried gate decision; the reverse edge was verified absent.
        ("packages/kernel-audit/Harborline.Kernel.Audit.csproj", "packages/foundation-authorization/Harborline.Foundation.Authorization.csproj"),
        // Ticket 199: AuditRecord carries typed Actor/Target/Act identity primitives from the admitted decision.
        ("packages/kernel-audit/Harborline.Kernel.Audit.csproj", "packages/foundation-identity-atlas/Harborline.Foundation.IdentityAtlas.csproj"),
        ("packages/kernel-audit/Harborline.Kernel.Audit.csproj", "packages/foundation-multitenancy/Harborline.Foundation.MultiTenancy.csproj"),
        ("packages/kernel-buckets/Harborline.Kernel.Buckets.csproj", "packages/foundation/Harborline.Foundation.csproj"),
        ("packages/kernel-crdt/Harborline.Kernel.Crdt.csproj", "packages/foundation/Harborline.Foundation.csproj"),
        ("packages/kernel-event-bus/Harborline.Kernel.EventBus.csproj", "packages/foundation/Harborline.Foundation.csproj"),
        ("packages/kernel-lease/Harborline.Kernel.Lease.csproj", "packages/foundation/Harborline.Foundation.csproj"),
        ("packages/kernel-ledger/Harborline.Kernel.Ledger.csproj", "packages/foundation/Harborline.Foundation.csproj"),
        ("packages/kernel-runtime/Harborline.Kernel.Runtime.csproj", "packages/foundation/Harborline.Foundation.csproj"),
        ("packages/kernel-runtime/Harborline.Kernel.Runtime.csproj", "packages/foundation-localfirst/Harborline.Foundation.LocalFirst.csproj"),
        ("packages/kernel-schema-registry/Harborline.Kernel.SchemaRegistry.csproj", "packages/foundation/Harborline.Foundation.csproj"),
        ("packages/kernel-security/Harborline.Kernel.Security.csproj", "packages/foundation/Harborline.Foundation.csproj"),
        ("packages/kernel-security/Harborline.Kernel.Security.csproj", "packages/foundation-localfirst/Harborline.Foundation.LocalFirst.csproj"),
        ("packages/kernel-signatures/Harborline.Kernel.Signatures.csproj", "packages/foundation/Harborline.Foundation.csproj"),
        // Ticket 199: constructing AuditRecord requires its carried-authority public signature at compile time.
        ("packages/kernel-signatures/Harborline.Kernel.Signatures.csproj", "packages/foundation-authorization/Harborline.Foundation.Authorization.csproj"),
        // Ticket 199: constructing AuditRecord requires its typed Actor/Target/Act public signature at compile time.
        ("packages/kernel-signatures/Harborline.Kernel.Signatures.csproj", "packages/foundation-identity-atlas/Harborline.Foundation.IdentityAtlas.csproj"),
        ("packages/kernel-signatures/Harborline.Kernel.Signatures.csproj", "packages/foundation-taxonomy/Harborline.Foundation.Taxonomy.csproj"),
        ("packages/kernel-sync/Harborline.Kernel.Sync.csproj", "packages/foundation/Harborline.Foundation.csproj"),
        ("packages/kernel/Harborline.Kernel.csproj", "packages/foundation/Harborline.Foundation.csproj"),
    ];

    /// <summary>Ticket 257: the exemption rows, for the shared vacuous-row property.</summary>
    internal static string[] KernelFoundationFacadeExemptionRows() =>
        KernelFoundationFacadeExemptions.Select(row => $"{row.Source} -> {row.Target}").ToArray();

    /// <summary>Ticket 257: the restricted kernel edges the csproj-graph discovery actually finds.</summary>
    internal static string[] DiscoveredRestrictedKernelEdges() =>
        ReadProjectReferenceGraph()
            .Where(edge => edge.SourceTier == "kernel" &&
                           (edge.TargetTier == "foundation" || edge.TargetTier == "blocks"))
            .Select(edge => $"{edge.Source} -> {edge.Target}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// R1 prevents kernel projects from acquiring new foundation or blocks dependencies
    /// and makes every retired migration-era facade exemption fail visibly.
    /// </summary>
    [Fact(DisplayName = "R1: kernel references no foundation or blocks projects")]
    public void R1_KernelReferencesNoFoundationOrBlocksProjects()
    {
        var edges = ReadProjectReferenceGraph();
        var actualRestrictedEdges = edges
            .Where(edge => edge.SourceTier == "kernel" &&
                           (edge.TargetTier == "foundation" || edge.TargetTier == "blocks"))
            .ToArray();

        var staleExemptions = KernelFoundationFacadeExemptions
            .Where(exemption => !actualRestrictedEdges.Any(edge =>
                edge.Source == exemption.Source && edge.Target == exemption.Target))
            .OrderBy(exemption => exemption.Source, StringComparer.Ordinal)
            .ThenBy(exemption => exemption.Target, StringComparer.Ordinal)
            .ToArray();

        if (staleExemptions.Length > 0)
        {
            throw new InvalidOperationException(
                $"R1 stale exemption detected ({staleExemptions.Length} entries); remove debt entries that no longer exist:\n" +
                string.Join("\n", staleExemptions.Select(exemption =>
                    $"{exemption.Source} -> {exemption.Target} (kernel -> foundation)")));
        }

        AssertNoViolations(
            "R1: kernel references no foundation or blocks projects",
            actualRestrictedEdges.Where(edge =>
                !KernelFoundationFacadeExemptions.Contains((edge.Source, edge.Target))));
    }

    /// <summary>
    /// R2 prevents foundation projects from reaching outward into the blocks tier.
    /// </summary>
    [Fact(DisplayName = "R2: foundation references no blocks projects")]
    public void R2_FoundationReferencesNoBlocksProjects()
    {
        AssertNoViolations(
            "R2: foundation references no blocks projects",
            ReadProjectReferenceGraph().Where(edge =>
                edge.SourceTier == "foundation" && edge.TargetTier == "blocks"));
    }

    /// <summary>
    /// R3 prevents every project beneath <c>packages/</c> from depending on an
    /// application project beneath <c>apps/</c>.
    /// </summary>
    [Fact(DisplayName = "R3: packages reference no apps projects")]
    public void R3_PackagesReferenceNoAppsProjects()
    {
        AssertNoViolations(
            "R3: packages reference no apps projects",
            ReadProjectReferenceGraph().Where(edge =>
                edge.Source.StartsWith("packages/", StringComparison.Ordinal) && edge.TargetTier == "apps"));
    }

    /// <summary>
    /// R4 keeps the contracts lane pure by allowing its projects to reference only
    /// other projects beneath <c>packages/contracts/</c>.
    /// </summary>
    [Fact(DisplayName = "R4: contracts reference only contracts projects")]
    public void R4_ContractsReferenceOnlyContractsProjects()
    {
        AssertNoViolations(
            "R4: contracts reference only contracts projects",
            ReadProjectReferenceGraph().Where(edge =>
                edge.SourceTier == "contracts" && edge.TargetTier != "contracts"));
    }

    /// <summary>
    /// Reads all package and application project files, excluding generated and
    /// vendored directory segments, and resolves every declared project reference to
    /// a repository-relative edge.
    /// </summary>
    private static IReadOnlyList<ProjectReferenceEdge> ReadProjectReferenceGraph()
    {
        var repositoryRoot = LocateRepositoryRoot();
        var scanRoots = new[]
        {
            Path.Combine(repositoryRoot, "packages"),
            Path.Combine(repositoryRoot, "apps"),
        };

        return scanRoots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
            .Where(path => !HasExcludedDirectorySegment(repositoryRoot, path))
            .SelectMany(projectPath =>
            {
                var source = ToRepositoryRelativePath(repositoryRoot, projectPath);
                return XDocument.Load(projectPath)
                    .Descendants("ProjectReference")
                    .Select(reference => ((string?)reference.Attribute("Include"))?.Replace('\\', '/'))
                    .Where(include => !string.IsNullOrWhiteSpace(include))
                    .Select(include => Path.GetFullPath(include!, Path.GetDirectoryName(projectPath)!))
                    .Select(targetPath => new ProjectReferenceEdge(
                        source,
                        ToRepositoryRelativePath(repositoryRoot, targetPath),
                        ClassifyTier(source),
                        ClassifyTier(ToRepositoryRelativePath(repositoryRoot, targetPath))));
            })
            .OrderBy(edge => edge.Source, StringComparer.Ordinal)
            .ThenBy(edge => edge.Target, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Reports whether a project path passes through a <c>bin</c>, <c>obj</c>, or
    /// <c>node_modules</c> directory segment.
    /// </summary>
    private static bool HasExcludedDirectorySegment(string repositoryRoot, string path)
    {
        var segments = Path.GetRelativePath(repositoryRoot, path)
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment =>
            segment is "bin" or "obj" or "node_modules");
    }

    /// <summary>
    /// Converts an absolute path into the normalized, forward-slash repository path
    /// used for classification, exemptions, and failure diagnostics.
    /// </summary>
    private static string ToRepositoryRelativePath(string repositoryRoot, string path) =>
        Path.GetRelativePath(repositoryRoot, Path.GetFullPath(path)).Replace('\\', '/');

    /// <summary>
    /// Classifies a normalized repository-relative project path according to the
    /// manifest's apps, contracts, kernel, foundation, blocks, and other tiers.
    /// </summary>
    private static string ClassifyTier(string path)
    {
        if (path.StartsWith("apps/", StringComparison.Ordinal)) return "apps";
        if (path.StartsWith("packages/contracts/", StringComparison.Ordinal)) return "contracts";

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || segments[0] != "packages") return "other";

        var package = segments[1];
        if (package == "kernel" || package.StartsWith("kernel-", StringComparison.Ordinal)) return "kernel";
        if (package == "foundation" || package.StartsWith("foundation-", StringComparison.Ordinal)) return "foundation";
        if (package.StartsWith("blocks-", StringComparison.Ordinal)) return "blocks";
        return "other";
    }

    /// <summary>
    /// Throws one detailed architecture failure containing every violating source and
    /// target edge, including both classified tiers.
    /// </summary>
    private static void AssertNoViolations(string ruleName, IEnumerable<ProjectReferenceEdge> candidates)
    {
        var violations = candidates
            .OrderBy(edge => edge.Source, StringComparer.Ordinal)
            .ThenBy(edge => edge.Target, StringComparer.Ordinal)
            .Select(FormatEdge)
            .ToArray();

        if (violations.Length > 0)
        {
            throw new InvalidOperationException(
                $"{ruleName} failed ({violations.Length} violating edges):\n" +
                string.Join("\n", violations));
        }
    }

    /// <summary>
    /// Formats an edge with the stable path and tier detail required for actionable
    /// architecture-test failures.
    /// </summary>
    private static string FormatEdge(ProjectReferenceEdge edge) =>
        $"{edge.Source} -> {edge.Target} ({edge.SourceTier} -> {edge.TargetTier})";

    /// <summary>
    /// Locates the repository root by walking upward from the test output until both
    /// the <c>packages</c> and <c>apps</c> directories are present.
    /// </summary>
    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "packages")) &&
                Directory.Exists(Path.Combine(directory.FullName, "apps")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root containing both packages and apps from the test output.");
    }

    /// <summary>
    /// Represents one normalized <c>ProjectReference</c> edge and the tier assigned to
    /// each endpoint.
    /// </summary>
    private sealed record ProjectReferenceEdge(
        string Source,
        string Target,
        string SourceTier,
        string TargetTier);
}
