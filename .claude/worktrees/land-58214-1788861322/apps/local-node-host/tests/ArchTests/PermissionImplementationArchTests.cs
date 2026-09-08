using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>Ticket 159 / L1438: catalog and seed entries cannot stand in for permission consumers.</summary>
public sealed class PermissionImplementationArchTests
{
    // Declaration locations below are in packages/foundation-identity-atlas/Permissions/Permission.cs.
    // Ticket 332 owns the decision to implement or retire each pre-existing orphan. Remove a row
    // as soon as its atom gains a consumer or is retired; this list must only shrink.
    private static readonly string[] Ticket332OrphanAllowList =
    [
        "calendar:archive", // Ticket 332: pre-existing orphan; Permission.CalendarArchive, line 65.
        "calendar:create", // Ticket 332: pre-existing orphan; Permission.CalendarCreate, line 61.
        "calendar:read", // Ticket 332: pre-existing orphan; Permission.CalendarRead, line 59.
        "calendar:write", // Ticket 332: pre-existing orphan; Permission.CalendarWrite, line 63.
        "comms:append", // Ticket 332: pre-existing orphan; Permission.CommsAppend, line 70.
        "comms:read", // Ticket 332: pre-existing orphan; Permission.CommsRead, line 68.
        "gl:post", // Ticket 332: pre-existing orphan; Permission.GlPost, line 79.
        "gl:read", // Ticket 332: pre-existing orphan; Permission.GlRead, line 73.
        "members:set-role", // Ticket 332: pre-existing orphan; Permission.MembersSetRole, line 107.
        "packages:install", // Ticket 332: pre-existing orphan; Permission.PackagesInstall, line 196.
        "packages:publish", // Ticket 332: pre-existing orphan; Permission.PackagesPublish, line 190.
        "provider:configure-bank-feed", // Ticket 332: pre-existing orphan; Permission.ProviderConfigureBankFeed, line 133.
        "provider:configure-email", // Ticket 332: pre-existing orphan; Permission.ProviderConfigureEmail, line 127.
        "provider:configure-identity", // Ticket 332: pre-existing orphan; Permission.ProviderConfigureIdentity, line 125.
        "provider:configure-payments", // Ticket 332: pre-existing orphan; Permission.ProviderConfigurePayments, line 131.
        "provider:configure-storage", // Ticket 332: pre-existing orphan; Permission.ProviderConfigureStorage, line 129.
        "provider:configure-telemetry", // Ticket 332: pre-existing orphan; Permission.ProviderConfigureTelemetry, line 135.
        "provider:read-config", // Ticket 332: pre-existing orphan; Permission.ProviderReadConfig, line 137.
        "telemetry:export", // Ticket 332: pre-existing orphan; Permission.TelemetryExport, line 159.
    ];

    [Fact(DisplayName = "Permission orphans exactly match the shrinking ticket 332 allow-list outside catalog and seed")]
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

        var orphans = atoms.Keys.Except(implemented, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var newOrphans = orphans.Except(Ticket332OrphanAllowList, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.True(newOrphans.Length == 0, "Permission atoms without implementing readers and absent from the ticket 332 allow-list: "
            + string.Join(", ", newOrphans.Select(atom => $"{atom} ({atoms[atom]})")));
        var staleRows = Ticket332OrphanAllowList.Except(orphans, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.True(staleRows.Length == 0, "Ticket 332 allow-list rows must be removed because their atoms now have a consumer or were retired: "
            + string.Join(", ", staleRows));
        Assert.Equal(Ticket332OrphanAllowList.Length, Ticket332OrphanAllowList.Distinct(StringComparer.Ordinal).Count());
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
