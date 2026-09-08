using System.Reflection;

using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Navigation;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Exact symbol inventory for every production entry into navigation parsing/admission. The fence keeps
/// install, role-gate, collision, and read composition on the one reviewed declaration contract.
/// </summary>
public sealed class PackNavigationAdmissionCallSiteFenceTests
{
    private sealed record AllowRow(
        string Path,
        string Symbol,
        int Line,
        string Target,
        string Classification,
        string Reason);

    private static readonly AllowRow[] Allowed =
    [
        new("apps/local-node-host/Health/PackNavigationRoutes.cs", "Harborline.Api.LocalNodeHost.Health.PackNavigationRoutes+PackNavigationContent.TryParse(System.String,Harborline.Api.Foundation.Packs.Navigation.PackNavigationDeclaration&,Harborline.Api.LocalNodeHost.Health.PackNavigationRoutes+ComposeError&): System.Boolean", 233, "Harborline.Api.Foundation.Packs.Navigation.PackNavigationDeclarationParser.Parse(System.String): Harborline.Api.Foundation.Packs.Navigation.PackNavigationParseResult", "read projection", "active immutable seeds are interpreted by the same parser that admitted them"),
        new("apps/local-node-host/Health/PackWorkflowAdmissionAdapter.cs", "Harborline.Api.LocalNodeHost.Health.PackWorkflowAdmissionAdapter.Admit(System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Packs.Install.Admission.PackComposedItem],Harborline.Api.Foundation.Assets.Common.TenantId): Harborline.Api.Foundation.Packs.Install.Admission.PackAdmissionResult", 53, "Harborline.Api.Foundation.Packs.Install.Admission.PackNavigationContentAdmission.Validate(System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Packs.Install.Admission.PackComposedItem],Harborline.Api.Foundation.Assets.Common.TenantId,Harborline.Api.Foundation.Authorization.IRoleGateAdmission): System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Packs.Install.Admission.PackAdmissionRefusal]", "host admission", "the real host composes navigation admission beside workflow admission"),
        new("packages/foundation-packs/Install/Admission/IPackContentAdmission.cs", "Harborline.Api.Foundation.Packs.Install.Admission.WorkflowRefusingPackContentAdmission.Admit(System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Packs.Install.Admission.PackComposedItem],Harborline.Api.Foundation.Assets.Common.TenantId): Harborline.Api.Foundation.Packs.Install.Admission.PackAdmissionResult", 86, "Harborline.Api.Foundation.Packs.Install.Admission.PackNavigationContentAdmission.Validate(System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Packs.Install.Admission.PackComposedItem],Harborline.Api.Foundation.Assets.Common.TenantId,Harborline.Api.Foundation.Authorization.IRoleGateAdmission): System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Packs.Install.Admission.PackAdmissionRefusal]", "fail-closed default", "foundation installs still structurally admit navigation and refuse unwired role gates"),
        new("packages/foundation-packs/Install/Admission/PackNavigationContentAdmission.cs", "Harborline.Api.Foundation.Packs.Install.Admission.PackNavigationContentAdmission.Validate(System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Packs.Install.Admission.PackComposedItem],Harborline.Api.Foundation.Assets.Common.TenantId,Harborline.Api.Foundation.Authorization.IRoleGateAdmission): System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Packs.Install.Admission.PackAdmissionRefusal]", 26, "Harborline.Api.Foundation.Packs.Navigation.PackNavigationDeclarationParser.Parse(System.String): Harborline.Api.Foundation.Packs.Navigation.PackNavigationParseResult", "install admission", "the ordinary installer parses every navigation content item before commit"),
        new("packages/foundation-packs/Install/Admission/PackNavigationContentAdmission.cs", "Harborline.Api.Foundation.Packs.Install.Admission.PackNavigationContentAdmission.Validate(System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Packs.Install.Admission.PackComposedItem],Harborline.Api.Foundation.Assets.Common.TenantId,Harborline.Api.Foundation.Authorization.IRoleGateAdmission): System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Packs.Install.Admission.PackAdmissionRefusal]", 36, "Harborline.Api.Foundation.Packs.Navigation.PackNavigationDeclarationParser.RoleGates(Harborline.Api.Foundation.Packs.Navigation.PackNavigationDeclaration): System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Authorization.DeclarativeGateReference]", "role extraction", "actions become declarative gates from the admitted object"),
        new("packages/foundation-packs/Install/Admission/PackNavigationContentAdmission.cs", "Harborline.Api.Foundation.Packs.Install.Admission.PackNavigationContentAdmission.Validate(System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Packs.Install.Admission.PackComposedItem],Harborline.Api.Foundation.Assets.Common.TenantId,Harborline.Api.Foundation.Authorization.IRoleGateAdmission): System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Packs.Install.Admission.PackAdmissionRefusal]", 56, "Harborline.Api.Foundation.Authorization.IRoleGateAdmission.AdmitAsync(Harborline.Api.Foundation.Authorization.RoleGatedDefinition,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask", "shared role gate", "vendor action roles traverse the one role-gate admission"),
        new("packages/foundation-packs/Install/PackInstaller.cs", "Harborline.Api.Foundation.Packs.Install.PackInstaller.FindNavigationCompositionRefusal(System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Packs.Install.InstalledPack],System.String,System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Packs.Install.Admission.PackComposedItem]): Harborline.Api.Foundation.Packs.Navigation.PackNavigationRefusal", 1126, "Harborline.Api.Foundation.Packs.Navigation.PackNavigationDeclarationParser.Parse(System.String): Harborline.Api.Foundation.Packs.Navigation.PackNavigationParseResult", "installed inventory", "the latest installed declaration participates in semantic collision admission"),
        new("packages/foundation-packs/Install/PackInstaller.cs", "Harborline.Api.Foundation.Packs.Install.PackInstaller.FindNavigationCompositionRefusal(System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Packs.Install.InstalledPack],System.String,System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Packs.Install.Admission.PackComposedItem]): Harborline.Api.Foundation.Packs.Navigation.PackNavigationRefusal", 1136, "Harborline.Api.Foundation.Packs.Navigation.PackNavigationDeclarationParser.Parse(System.String): Harborline.Api.Foundation.Packs.Navigation.PackNavigationParseResult", "candidate inventory", "the composed candidate participates in semantic collision admission"),
        new("packages/foundation-packs/Install/PackInstaller.cs", "Harborline.Api.Foundation.Packs.Install.PackInstaller.FindNavigationCompositionRefusal(System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Packs.Install.InstalledPack],System.String,System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Packs.Install.Admission.PackComposedItem]): Harborline.Api.Foundation.Packs.Navigation.PackNavigationRefusal", 1141, "Harborline.Api.Foundation.Packs.Navigation.PackNavigationDeclarationParser.FindCompositionRefusal(System.Collections.Generic.IEnumerable`1[Harborline.Api.Foundation.Packs.Navigation.PackNavigationDeclaration]): Harborline.Api.Foundation.Packs.Navigation.PackNavigationRefusal", "collision gate", "install refuses duplicate workspace, item, panel, and mode identities before commit"),
    ];

    [Fact]
    public void Production_call_sites_equal_the_exact_reviewed_inventory()
    {
        Assert.All(Allowed, row =>
        {
            Assert.DoesNotContain('*', row.Path);
            Assert.DoesNotContain('*', row.Symbol);
            Assert.DoesNotContain('*', row.Target);
            Assert.True(row.Line > 0);
            Assert.False(string.IsNullOrWhiteSpace(row.Classification));
            Assert.False(string.IsNullOrWhiteSpace(row.Reason));
        });
        var actual = Discover(
            [typeof(PackInstaller).Assembly, typeof(PackNavigationRoutes).Assembly]);
        var expected = Allowed
            .Select(row => Describe(row.Path, row.Symbol, row.Line, row.Target))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(expected.SequenceEqual(actual, StringComparer.Ordinal),
            "Navigation admission call-site inventory mismatch:\n" + string.Join("\n", actual));
    }

    [Fact]
    public void Fence_detects_a_planted_unreviewed_parser_call_site()
    {
        var actual = Discover(
            [typeof(PlantedNavigationParserOffender).Assembly],
            type => type == typeof(PlantedNavigationParserOffender));

        Assert.Single(actual);
        Assert.Contains(nameof(PlantedNavigationParserOffender), actual[0], StringComparison.Ordinal);
        Assert.Contains("PackNavigationDeclarationParser.Parse", actual[0], StringComparison.Ordinal);
    }

    private static string[] Discover(IEnumerable<Assembly> assemblies, Func<Type, bool>? typeFilter = null)
        => RawMutationPortSymbolInventoryTests.DiscoverCalls(
                assemblies,
                target => TargetNames.Contains(TargetName(target)),
                typeFilter)
            .Select(site => Describe(site.Path, site.Symbol, site.Line, site.Target))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string Describe(string path, string symbol, int line, string target)
        => $"{path}:{symbol}:{line} -> {target}";

    private static string TargetName(MethodBase method)
        => $"{method.DeclaringType?.FullName}.{method.Name}";

    private static readonly HashSet<string> TargetNames = new(StringComparer.Ordinal)
    {
        "Harborline.Api.Foundation.Packs.Install.Admission.PackNavigationContentAdmission.Validate",
        "Harborline.Api.Foundation.Packs.Navigation.PackNavigationDeclarationParser.Parse",
        "Harborline.Api.Foundation.Packs.Navigation.PackNavigationDeclarationParser.RoleGates",
        "Harborline.Api.Foundation.Packs.Navigation.PackNavigationDeclarationParser.FindCompositionRefusal",
        "Harborline.Api.Foundation.Authorization.IRoleGateAdmission.AdmitAsync",
    };

    private static class PlantedNavigationParserOffender
    {
        internal static PackNavigationParseResult ParseWithoutAdmission(string json)
            => PackNavigationDeclarationParser.Parse(json);
    }
}
