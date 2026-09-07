using System.Reflection;
using System.Xml.Linq;

using HostUnderTest = Harborline.Api.NodeHosting.NodeHost;
using HostContractUnderTest = Harborline.Api.NodeHosting.INodeHost;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>G-R1(a): app-facing projects have one node assembly edge and a closed host API.</summary>
public sealed class NodeHostingArchTests
{
    [Fact(DisplayName = "Harborline .NET has one LocalNodeHost project reference and the two-member host API")]
    public void ReferenceAppEdition_HasOneNodeReference_AndExactLifecycleSurface()
    {
        var editionRoot = LocateEditionRoot();
        var repositoryRoot = Directory.GetParent(editionRoot)!.Parent!.FullName;
        var scanRoots = new[]
        {
            editionRoot,
            Path.Combine(repositoryRoot, "packages", "client-dotnet"),
        };
        var nodeReferences = scanRoots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
            .SelectMany(path => XDocument.Load(path)
                .Descendants("ProjectReference")
                .Where(reference => Path.GetFileName(
                                        ((string?)reference.Attribute("Include"))?.Replace('\\', '/')) ==
                                    "Harborline.LocalNodeHost.csproj")
                // Normalised the same way the Include attribute is just above: the assertion is about
                // path identity, not separator style, and GetRelativePath yields '\' on Windows.
                .Select(_ => Path.GetRelativePath(editionRoot, path).Replace('\\', '/')))
            .ToArray();

        Assert.Equal(["Harborline.Api.NodeHosting.csproj"], nodeReferences);

        var expected = new[]
        {
            (Name: "StartAsync", ReturnType: typeof(Task<(Uri NodeAddress, string SessionToken)>)),
            (Name: "StopAsync", ReturnType: typeof(Task)),
        };
        var actual = typeof(HostContractUnderTest).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => (Name: method.Name, ReturnType: method.ReturnType,
                Parameters: method.GetParameters().Select(parameter => parameter.ParameterType).ToArray()))
            .OrderBy(signature => signature.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected.Select(signature => signature.Name), actual.Select(signature => signature.Name));
        Assert.Equal(expected.Select(signature => signature.ReturnType), actual.Select(signature => signature.ReturnType));
        Assert.All(actual, signature => Assert.Equal([typeof(CancellationToken)], signature.Parameters));
        Assert.Equal(
            expected.Select(signature => signature.Name),
            typeof(HostUnderTest).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(method => method.Name)
                .Order(StringComparer.Ordinal));
        Assert.All(
            typeof(HostUnderTest).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method =>
            {
                Assert.Contains(method.Name, expected.Select(signature => signature.Name));
                Assert.Equal(
                    expected.Single(signature => signature.Name == method.Name).ReturnType,
                    method.ReturnType);
                Assert.Equal(
                    [typeof(CancellationToken)],
                    method.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
            });

        var expectedExported = new[] { typeof(HostContractUnderTest), typeof(HostUnderTest) }
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();
        var actualExported = typeof(HostUnderTest).Assembly.GetExportedTypes()
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedExported, actualExported);
    }

    private static string LocateEditionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "apps", "node-hosting");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate apps/node-hosting from the test output.");
    }
}
