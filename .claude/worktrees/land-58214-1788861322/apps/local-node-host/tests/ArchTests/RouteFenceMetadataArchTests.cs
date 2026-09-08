using System.Text.RegularExpressions;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Keeps route-fence evidence inseparable from the filter that enforces the matching policy.
/// </summary>
public sealed class RouteFenceMetadataArchTests
{
    private static readonly HashSet<string> FenceHelperPaths = new(StringComparer.Ordinal)
    {
        Path.Combine("Health", "AdmissionWebPlaneRouteFence.cs"),
        Path.Combine("Health", "DeviceReachableProductDataRouteFence.cs"),
        Path.Combine("Health", "PreAuthOperationalRouteFence.cs"),
        Path.Combine("Health", "SelectedSessionProductRouteFence.cs"),
        Path.Combine("Health", "WebPlaneUnavailableRouteFence.cs"),
    };

    /// <summary>The fence-helper skip rows, for the ticket-257 vacuity property.</summary>
    internal static string[] FenceHelperPathRows() => [.. FenceHelperPaths.Order(StringComparer.Ordinal)];

    /// <summary>
    /// The same scan with the skip list BYPASSED: every host file that really performs a marker
    /// operation. A skip row that names nothing here is dead width pre-authorising a future file.
    /// </summary>
    internal static string[] DiscoveredMarkerOperationFiles() =>
        [.. ScanForMarkerOperationsOutsideFenceHelpers(skipFenceHelpers: false).Order(StringComparer.Ordinal)];

    private static readonly string MarkerDefinitionPath =
        Path.Combine("Health", "RouteFenceMetadata.cs");

    private static readonly Regex MarkerConstruction = new(
        @"new\s+RouteFenceMetadata\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex MarkerStamp = new(
        @"(?:\.WithMetadata\s*(?:<\s*RouteFenceMetadata\b|\([^;]*\bRouteFenceMetadata\b)|" +
        @"\.Metadata\s*\.\s*Add\s*\([^;]*\bRouteFenceMetadata\b)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex MarkerAlias = new(
        @"\busing\s+\w+\s*=\s*(?:global::)?(?:[\w.]+\.)?RouteFenceMetadata\s*;",
        RegexOptions.Compiled);

    private static readonly Regex MarkerPartialDeclaration = new(
        @"\bpartial\s+(?:class|record(?:\s+class)?)\s+RouteFenceMetadata\b",
        RegexOptions.Compiled);

    // Raw FilterFactories is fenced because ordering and removal on that list are the only
    // remaining ways to separate a stamped marker from the filter that enforces it. Ordinary
    // AddEndpointFilter usage remains unrestricted and composes inside the fence.
    private static readonly Regex RawFilterFactoriesReference = new(
        @"\bFilterFactories\b",
        RegexOptions.Compiled);

    [Fact(DisplayName = "Route-fence marker operations stay inside structural fence helpers")]
    public void MarkerOperations_ExistOnlyInFenceHelpers()
    {
        var offenders = ScanForMarkerOperationsOutsideFenceHelpers();

        Assert.True(
            offenders.Count == 0,
            "RouteFenceMetadata operations and raw FilterFactories references can only exist inside " +
            "the structural fence helper files. Move this endpoint into the matching Map*Group() " +
            "helper instead:\n  " + string.Join("\n  ", offenders));
    }

    [Fact(DisplayName = "Route-fence marker scanner still reports an offender")]
    public void MarkerOperations_Scanner_Still_Reports_An_Offender()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ticket-066-route-fence-arch-" + Guid.NewGuid().ToString("N"));
        var health = Path.Combine(root, "Health");
        Directory.CreateDirectory(health);
        try
        {
            File.WriteAllText(
                Path.Combine(health, "Offender.cs"),
                "var marker = new RouteFenceMetadata(RouteFenceKind.SelectedSessionProduct);");

            Assert.Equal(
                [Path.Combine("Health", "Offender.cs")],
                ScanForMarkerOperationsOutsideFenceHelpers(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static List<string> ScanForMarkerOperationsOutsideFenceHelpers(
        string? sourceRoot = null,
        bool skipFenceHelpers = true)
    {
        var hostRoot = sourceRoot ?? LocateHostSourceRoot();
        var excludedSegments = new[]
        {
            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}",
        };
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(hostRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(hostRoot, file);
            if (skipFenceHelpers && FenceHelperPaths.Contains(relativePath)) continue;
            if (excludedSegments.Any(segment => file.Contains(segment, StringComparison.Ordinal))) continue;

            var source = File.ReadAllText(file);
            var forbiddenPartial = relativePath != MarkerDefinitionPath &&
                MarkerPartialDeclaration.IsMatch(source);
            if (MarkerConstruction.IsMatch(source) ||
                MarkerStamp.IsMatch(source) ||
                MarkerAlias.IsMatch(source) ||
                forbiddenPartial ||
                RawFilterFactoriesReference.IsMatch(source))
            {
                offenders.Add(relativePath);
            }
        }

        return offenders;
    }

    private static string LocateHostSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var direct = Path.Combine(directory.FullName, "Harborline.LocalNodeHost.csproj");
            if (File.Exists(direct)) return directory.FullName;

            var nested = Path.Combine(
                directory.FullName,
                "apps",
                "local-node-host",
                "Harborline.LocalNodeHost.csproj");
            if (File.Exists(nested)) return Path.GetDirectoryName(nested)!;

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the local-node-host source root from " + AppContext.BaseDirectory);
    }
}
