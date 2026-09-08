using System.Xml.Linq;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>Protects the API host from unused UI-layer project edges.</summary>
public sealed class HostProjectReferenceArchTests
{
    [Fact(DisplayName = "LocalNodeHost has no direct ui-core project reference")]
    public void LocalNodeHost_HasNoDirectUiCoreProjectReference()
    {
        var projectPath = LocateHostProject();
        var references = XDocument.Load(projectPath)
            .Descendants("ProjectReference")
            .Select(reference => ((string?)reference.Attribute("Include"))?.Replace('\\', '/'))
            .Where(include => include is not null)
            .ToArray();

        Assert.DoesNotContain(references, include =>
            include!.EndsWith("/packages/ui-core/Harborline.UICore.csproj", StringComparison.OrdinalIgnoreCase));
    }

    private static string LocateHostProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "apps",
                "local-node-host",
                "Harborline.LocalNodeHost.csproj");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the LocalNodeHost project from the test output.");
    }
}
