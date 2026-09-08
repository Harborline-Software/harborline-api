using System.Text.RegularExpressions;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

public sealed class AuthorizationAdminWriterBoundaryArchTests
{
    private static readonly (string Name, Regex Pattern)[] ForbiddenDependencies =
    {
        ("configuration store", new Regex(@"\bIAuthorizationConfigurationStore\b", RegexOptions.CultureInvariant)),
        ("validated write", new Regex(@"\bValidatedAuthorizationConfigurationWrite\b", RegexOptions.CultureInvariant)),
        ("EF DbContext", new Regex(@"\bDbContext\b", RegexOptions.CultureInvariant)),
        ("direct commit", new Regex(@"\.CommitAsync\s*\(", RegexOptions.CultureInvariant)),
        ("authorization tables", new Regex(@"\bAuthorization(?:Definitions|OfferedRoles|BindingRevisions|BindingRoles|Roles)\b", RegexOptions.CultureInvariant)),
    };

    [Fact]
    public void AdministrationRouteFamily_UsesTheWriterAndNeverCommitsStoresDirectly()
    {
        var healthRoot = Path.Combine(LocateHostRoot(), "Health");
        var familyFiles = Directory.EnumerateFiles(healthRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("AuthorizationAdmin", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                // Authorized holders projection: reads the grant snapshot and roster; no configuration
                // write. Keep it in the family scanned below, under every forbidden-dependency rule.
                "AccessHoldersRead.cs",
                "AuthorizationAdminRoutes.cs",
                "HostedAuthorizationAdminApiEndpoint.cs",
                "LocalNodeEndpointMapping.cs",
            },
            familyFiles.Select(Path.GetFileName));
        Assert.Contains(
            "AuthorizationDefinitionWriter",
            File.ReadAllText(Path.Combine(healthRoot, "AuthorizationAdminRoutes.cs")),
            StringComparison.Ordinal);
        Assert.Empty(ScanFiles(familyFiles));
    }

    [Fact]
    public void WriterWallScanner_ReportsAPlantedOffenderThroughTheSameFilePath()
    {
        var scratch = Path.GetTempFileName();
        try
        {
            File.WriteAllText(
                scratch,
                "IAuthorizationConfigurationStore store; ValidatedAuthorizationConfigurationWrite write; "
                + "DbContext db; db.AuthorizationDefinitions.Add(row); await store.CommitAsync(write);");

            var violations = ScanFiles([scratch]);

            Assert.Equal(5, violations.Count);
            Assert.All(ForbiddenDependencies, rule =>
                Assert.Contains(violations, violation => violation.Rule == rule.Name));
        }
        finally
        {
            File.Delete(scratch);
        }
    }

    private static IReadOnlyList<Violation> ScanFiles(IEnumerable<string> paths)
    {
        var violations = new List<Violation>();
        foreach (var path in paths)
        {
            var source = File.ReadAllText(path);
            foreach (var (name, pattern) in ForbiddenDependencies)
            {
                if (pattern.IsMatch(source)) violations.Add(new Violation(path, name));
            }
        }
        return violations;
    }

    private static string LocateHostRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "apps", "local-node-host");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate apps/local-node-host.");
    }

    private sealed record Violation(string Path, string Rule);
}
