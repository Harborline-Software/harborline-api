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

    /// <summary>
    /// T-537 (DES-0029 ck-4): the tenancy context has one definition, the platform's
    /// <c>Harborline.Foundation.MultiTenancy</c>, and the api-only catalogue, resolver and
    /// lifecycle contracts (ADR 0096 ruling 7, proved unused) stay retired.
    /// </summary>
    [Fact]
    [Trait("Holds", "kernel-core-ck-4")]
    public void Tenancy_context_has_one_definition_and_it_is_the_platform_contract()
    {
        var types = Directory.EnumerateFiles(AppContext.BaseDirectory, "Harborline*.dll")
            .Select(Assembly.LoadFrom).SelectMany(assembly => assembly.GetTypes()).ToArray();
        foreach (var platform in new[]
                 {
                     typeof(Harborline.Foundation.MultiTenancy.ITenantContext),
                     typeof(Harborline.Foundation.MultiTenancy.TenantMetadata),
                     typeof(Harborline.Foundation.MultiTenancy.TenantStatus),
                     typeof(Harborline.Foundation.MultiTenancy.ITenantScoped),
                     typeof(Harborline.Foundation.MultiTenancy.IMustHaveTenant),
                 })
        {
            Assert.Equal(platform, Assert.Single(types, type => type.Name == platform.Name &&
                type.Namespace?.EndsWith(".MultiTenancy", StringComparison.Ordinal) == true));
        }
        Assert.Equal("Harborline.Foundation.MultiTenancy",
            typeof(Harborline.Foundation.MultiTenancy.ITenantContext).Assembly.GetName().Name);

        string[] retired = ["ITenantCatalog", "ITenantResolver", "ITenantLifecycleService",
            "InMemoryTenantCatalog", "IMayHaveTenant", "TenantQueryFilterExtensions"];
        Assert.Empty(types.Where(type => retired.Contains(type.Name) &&
            type.Assembly.GetName().Name!.StartsWith("Harborline.Api.", StringComparison.Ordinal))
            .Select(type => type.FullName));
        Assert.DoesNotContain(types, type =>
            type.Assembly.GetName().Name == "Harborline.Api.Foundation.MultiTenancy");

        foreach (var name in new[] { "ActiveTeamTenantContext", "SelectedSessionTenantContext",
                     "NodeAuthorizationTenantContext" })
        {
            var context = Assert.Single(types, type => type.Name == name);
            Assert.True(typeof(Harborline.Foundation.MultiTenancy.ITenantContext).IsAssignableFrom(context),
                context.FullName);
        }

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Harborline.Api.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var projectEdges = Sources(root.FullName, "*.*proj")
            .Concat([Path.Combine(root.FullName, "Harborline.Api.slnx")])
            .Where(file => File.ReadAllText(file).Contains("foundation-multitenancy", StringComparison.OrdinalIgnoreCase))
            .Select(file => Path.GetRelativePath(root.FullName, file));
        Assert.Empty(projectEdges);
        Assert.False(Directory.Exists(Path.Combine(root.FullName, "packages", "foundation-multitenancy")));

        // TenantSelection (packages/foundation/MultiTenancy) keeps the Harborline.Api namespace;
        // every other name from the retired package must be spelled from the platform namespace.
        string[] moved = ["ITenantContext", "TenantMetadata", "TenantStatus", "ITenantScoped", "IMustHaveTenant"];
        var oldSpellings = Sources(root.FullName).SelectMany(file =>
            CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot().DescendantNodes()
                .OfType<QualifiedNameSyntax>()
                .Where(name => name.Left.ToString() == "Harborline.Api.Foundation.MultiTenancy" &&
                               moved.Contains(name.Right.Identifier.ValueText))
                .Select(name => $"{file}:{name.GetLocation().GetLineSpan().StartLinePosition.Line + 1}"));
        Assert.Empty(oldSpellings);
    }

    private static IEnumerable<string> Sources(string directory, string pattern = "*.cs")
    {
        foreach (var file in Directory.EnumerateFiles(directory, pattern)) yield return file;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith('.') || name is "bin" or "obj" or "node_modules" or "dist") continue;
            foreach (var file in Sources(child, pattern)) yield return file;
        }
    }
}
