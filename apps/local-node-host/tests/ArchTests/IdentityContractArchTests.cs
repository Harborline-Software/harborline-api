using System.Reflection;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

public sealed class IdentityContractArchTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void TenantIdentityHasOneDefinitionAndAllMarkersUseThePlatformContract()
    {
        var assemblies = Directory.EnumerateFiles(AppContext.BaseDirectory, "Harborline*.dll")
            .Select(Assembly.LoadFrom).ToArray();
        var types = assemblies.SelectMany(assembly => assembly.GetTypes()).ToArray();
        Assert.Equal(typeof(TenantId), Assert.Single(types, type => type.Name == nameof(TenantId)));
        Assert.Equal("Harborline.Contracts", typeof(TenantId).Assembly.GetName().Name);
        var implementors = types.Where(type => !type.IsInterface &&
            typeof(Harborline.Api.Foundation.MultiTenancy.ITenantScoped).IsAssignableFrom(type)).ToArray();
        Assert.NotEmpty(implementors);
        output.WriteLine("Platform marker implementors: " + implementors.Count(type =>
            type.Assembly.GetName().Name!.StartsWith("Harborline.Api.", StringComparison.Ordinal) &&
            !type.Assembly.GetName().Name!.EndsWith(".Tests", StringComparison.Ordinal)));
        output.WriteLine("Tenant assembly: " + typeof(TenantId).Assembly.FullName);
        Assert.All(implementors, type => Assert.True(
            typeof(Harborline.Foundation.MultiTenancy.ITenantScoped).IsAssignableFrom(type), type.FullName));
        Assert.Equal(typeof(TenantId), typeof(Harborline.Foundation.MultiTenancy.ITenantScoped)
            .GetProperty(nameof(Harborline.Foundation.MultiTenancy.ITenantScoped.TenantId))!.PropertyType);

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Harborline.Api.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var declarations = Sources(root.FullName).SelectMany(file =>
            CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot().DescendantNodes()
                .OfType<BaseTypeDeclarationSyntax>()
                .Where(type => type.Identifier.ValueText == nameof(TenantId))
                .Select(type => $"{file}:{type.GetLocation().GetLineSpan().StartLinePosition.Line + 1}"));
        Assert.Empty(declarations);
    }

    private static IEnumerable<string> Sources(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs")) yield return file;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith('.') || name is "bin" or "obj" or "node_modules" or "dist") continue;
            foreach (var file in Sources(child)) yield return file;
        }
    }
}
