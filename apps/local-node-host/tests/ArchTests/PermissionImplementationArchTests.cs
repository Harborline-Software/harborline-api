using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>Ticket 159 / L1438: catalog and seed entries cannot stand in for permission consumers.</summary>
public sealed class PermissionImplementationArchTests
{
    [Fact(DisplayName = "Every permission atom has a gate, route or policy reader outside its catalog and seed")]
    public void EveryAtom_HasAnImplementation()
    {
        var root = RepositoryRoot();
        var declarations = new[]
        {
            "packages/foundation-identity-atlas/Permissions/Permission.cs",
            "packages/foundation-identity-atlas/TeamRolePermissions.cs",
            "packages/foundation/AuthorizationOperationNames.cs",
        }.Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, path)), path: path))
            .ToArray();
        var compilation = CSharpCompilation.Create("PermissionImplementationDiscovery", declarations,
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(PermissionSet).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var atoms = declarations.Take(2).SelectMany(tree => tree.GetRoot().DescendantNodes()
                .OfType<VariableDeclaratorSyntax>()
                .Select(node => compilation.GetSemanticModel(tree).GetDeclaredSymbol(node))
                .OfType<IFieldSymbol>()
                .Where(field => field.IsConst && field.ConstantValue is string))
            .ToDictionary(field => (string)field.ConstantValue!, field => field.ToDisplayString(), StringComparer.Ordinal);
        Assert.NotEmpty(atoms);
        var implemented = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in ProductionSources(root))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            // These are catalog/offer declarations, not implementing code. Historical SQL and tests
            // are excluded by the directory walk; navigation projections are presentation only.
            if (relative.StartsWith("packages/foundation-identity-atlas/Permissions/", StringComparison.Ordinal)
                || relative == "packages/foundation-identity-atlas/TeamRolePermissions.cs"
                || relative == "packages/blocks-access-grant/AccessGrantAuthorizationSeed.cs"
                || relative == "apps/local-node-host/Health/WebSession/NavigationPermissionProjection.cs")
                continue;

            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: relative);
            var expressions = tree.GetRoot().DescendantNodes().OfType<ExpressionSyntax>()
                .Where(expression => expression is IdentifierNameSyntax or LiteralExpressionSyntax).ToArray();
            // Most production files never mention an atom. Avoid semantic models for that majority.
            if (!expressions.Any(expression => expression is IdentifierNameSyntax name
                    ? atoms.Values.Any(symbol => symbol.EndsWith("." + name.Identifier.ValueText, StringComparison.Ordinal))
                    : expression is LiteralExpressionSyntax literal && literal.Token.Value is string value && atoms.ContainsKey(value)))
                continue;
            if (expressions.Length == 0) continue;
            var model = compilation.AddSyntaxTrees(tree).GetSemanticModel(tree);
            foreach (var expression in expressions)
            {
                if (!IsDecisionRouteOrPolicyRead(expression, model)) continue;
                // Resolve symbols, including aliases and using-static imports. Comments, documentation
                // and nameof references never count. Literal operation arguments support legacy gates.
                if (expression.Ancestors().OfType<InvocationExpressionSyntax>()
                    .Any(call => call.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" }))
                    continue;
                var symbol = model.GetSymbolInfo(expression).Symbol;
                var constant = symbol is IFieldSymbol { IsConst: true } field
                    ? field.ConstantValue
                    : expression is LiteralExpressionSyntax literal ? literal.Token.Value : null;
                if (constant is string atom && atoms.ContainsKey(atom)) implemented.Add(atom);
            }
        }

        var orphans = atoms.Keys.Except(implemented, StringComparer.Ordinal).Order(StringComparer.Ordinal);
        Assert.True(!orphans.Any(), "Permission atoms without implementing readers: "
            + string.Join(", ", orphans.Select(atom => $"{atom} ({atoms[atom]})")));
    }

    private static bool IsDecisionRouteOrPolicyRead(ExpressionSyntax expression, SemanticModel model)
    {
        if (expression.Ancestors().Any(node => node is ArgumentSyntax or SwitchExpressionArmSyntax)
            && (expression.Ancestors().OfType<TypeDeclarationSyntax>().Any(type =>
                type.Identifier.ValueText.EndsWith("Routes", StringComparison.Ordinal)
                || type.Identifier.ValueText.EndsWith("Policy", StringComparison.Ordinal)
                || type.Identifier.ValueText.EndsWith("Gate", StringComparison.Ordinal))
            || expression.Ancestors().OfType<InvocationExpressionSyntax>().Any(call =>
                call.Expression.ToString().Contains("Authorization", StringComparison.Ordinal)
                || call.Expression.ToString().Contains("HasPermission", StringComparison.Ordinal)
                || call.Expression.ToString().Contains("Decide", StringComparison.Ordinal)
                || model.GetSymbolInfo(call).Symbol is IMethodSymbol
                {
                    Name: "Contains", ContainingType: { } owner,
                } && owner.ToDisplayString() == typeof(PermissionSet).FullName))) return true;

        // A local operation selected before DecideAsync is still that decision's input. Follow the
        // local symbol to its uses, so introducing a temporary cannot make a real gate disappear.
        if (expression.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault() is { } variable
            && variable.Parent?.Parent is LocalDeclarationStatementSyntax
            && model.GetDeclaredSymbol(variable) is ILocalSymbol local)
        {
            return variable.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault()?
                .DescendantNodes().OfType<IdentifierNameSyntax>().Any(use =>
                    SymbolEqualityComparer.Default.Equals(local, model.GetSymbolInfo(use).Symbol)
                    && IsDecisionRouteOrPolicyRead(use, model)) == true;
        }
        // Another const declaration or an arbitrary string table is not an executable reader.
        return false;
    }

    private static IEnumerable<string> ProductionSources(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs")) yield return file;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            // Prune before descending: worktrees and build output must never enter source discovery.
            if (Path.GetFileName(child) is "tests" or "test" or "bin" or "obj" or "Migrations"
                or "node_modules" or "artifacts" || Path.GetFileName(child).StartsWith('.')) continue;
            foreach (var file in ProductionSources(child)) yield return file;
        }
    }

    private static string RepositoryRoot([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../../../.."));
}
