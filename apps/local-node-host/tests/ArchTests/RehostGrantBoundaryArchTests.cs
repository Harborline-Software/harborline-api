using System.Reflection;
using Harborline.Api.LocalNodeHost.BackupRestore;
using Harborline.Api.LocalNodeHost.Tests.Audit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>Ticket 292: one grant redemption owner and no retired opaque-string restore contract.</summary>
public sealed class RehostGrantBoundaryArchTests
{
    [Fact]
    public void OnlyNodeRehostServiceRedeemsGrants()
    {
        // Discover compiled calls (including concrete calls, aliases and async state machines).
        // Exact equality makes both a second caller and deletion of the real caller fail.
        var site = Assert.Single(AuditAppendSymbolInventory.Discover(ClassifyRedemption));
        Assert.Equal("apps/local-node-host/BackupRestore/NodeRehostService.cs", site.File);
        Assert.Equal(MethodSignatureSymbol.Format(
            typeof(NodeRehostService).GetMethod(nameof(NodeRehostService.RestoreAsync))!), site.Symbol);
        Assert.Equal(121, site.Line); // Manual restore: redemption precedes every recovery/write port.
    }

    private static string? ClassifyRedemption(MethodBase method) =>
        method.Name == nameof(IRosterRehostGrantProvider.RedeemAsync)
        && method.DeclaringType is { } owner
        && typeof(IRosterRehostGrantProvider).IsAssignableFrom(owner)
            ? "rehost-redemption" : null;

    [Fact]
    public void RetiredOpaqueGrantContractCannotReturn()
    {
        var root = AuditAppendSymbolInventory.RepositoryRoot();
        var offenders = ProductionSources(root).SelectMany(file =>
            CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot().DescendantTokens()
                .Where(token => token.ValueText is "RosterSignedGrant" or "rosterSignedGrant"
                    or "ReplicaRestoreCoordinator" or "IReplicaRehostSource")
                .Select(token => $"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{token.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {token.ValueText}"))
            .ToArray();
        Assert.True(offenders.Length == 0,
            "Retired restore symbols accept an unverified string grant:\n" + string.Join("\n", offenders));

        // Also reject renamed plain-string grant inputs on re-host types. Resolve the parameter
        // type so System.String and using aliases cannot evade this boundary.
        var envelopeInputs = 0;
        foreach (var file in ProductionSources(root))
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
            var inputs = tree.GetRoot().DescendantNodes().OfType<ParameterSyntax>()
                .Where(parameter => parameter.Identifier.ValueText.Contains("grant", StringComparison.OrdinalIgnoreCase)
                    && parameter.Ancestors().OfType<TypeDeclarationSyntax>().Any(type =>
                        type.Identifier.ValueText.Contains("rehost", StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (inputs.Length == 0) continue;
            var model = CSharpCompilation.Create("RehostStringInputs", [tree],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)])
                .GetSemanticModel(tree);
            foreach (var input in inputs)
            {
                if (input.Type is null || model.GetTypeInfo(input.Type).Type?.SpecialType != SpecialType.System_String)
                    continue;
                // This one transport envelope stores bytes; it neither admits nor redeems a grant.
                Assert.Equal("apps/local-node-host/BackupRestore/NodeRehostService.cs",
                    Path.GetRelativePath(root, file).Replace('\\', '/'));
                Assert.Equal(nameof(RosterSignedRehostGrant),
                    input.Ancestors().OfType<TypeDeclarationSyntax>().First().Identifier.ValueText);
                Assert.Equal(nameof(RosterSignedRehostGrant.SerializedGrant), input.Identifier.ValueText);
                envelopeInputs++;
            }
        }
        Assert.Equal(1, envelopeInputs);
    }

    private static IEnumerable<string> ProductionSources(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs")) yield return file;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            // Prune before descending so nested worktrees, dependencies and build output are never read.
            if (Path.GetFileName(child) is "tests" or "test" or "bin" or "obj"
                or "node_modules" or "artifacts" || Path.GetFileName(child).StartsWith('.')) continue;
            foreach (var file in ProductionSources(child)) yield return file;
        }
    }
}
