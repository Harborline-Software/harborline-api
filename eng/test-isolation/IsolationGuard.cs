using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class IsolationGuard
{
    internal sealed record Site(string Path, string Type, string Operation, bool Isolated)
    {
        public string Key => $"{Path}\t{Type}\t{Operation}";
    }

    // Deliberately conservative syntax fence: these member names are forbidden regardless of
    // receiver spelling (including aliases/static imports). This is not a call graph, restoration
    // proof, or arbitrary concurrency-safety claim. New mutating helpers require explicit review.
    internal static Site[] Scan(IReadOnlyDictionary<string, string> files)
    {
        var roots = files.ToDictionary(p => p.Key, p => CSharpSyntaxTree.ParseText(p.Value).GetRoot());
        var constants = roots.Values.SelectMany(r => r.DescendantNodes().OfType<FieldDeclarationSyntax>())
            .Where(f => f.Modifiers.Any(SyntaxKind.ConstKeyword))
            .SelectMany(f => f.Declaration.Variables.Select(v => new
            {
                Key = f.Ancestors().OfType<TypeDeclarationSyntax>().First().Identifier.ValueText + "." + v.Identifier.ValueText,
                Value = Literal(v.Initializer?.Value),
            }))
            .Where(p => p.Value is not null).GroupBy(p => p.Key)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Value);
        string? CollectionName(ExpressionSyntax? expression) => Literal(expression)
            ?? (expression is null ? null : constants.GetValueOrDefault(expression.ToString()));
        var nonparallel = roots.Values.SelectMany(r => r.DescendantNodes().OfType<AttributeSyntax>())
            .Where(a => Name(a.Name) == "CollectionDefinition")
            .Where(a => a.ArgumentList?.Arguments.Any(x => x.NameEquals?.Name.Identifier.ValueText == "DisableParallelization"
                && x.Expression.IsKind(SyntaxKind.TrueLiteralExpression)) == true)
            .Select(a => CollectionName(a.ArgumentList?.Arguments.FirstOrDefault()?.Expression))
            .Where(n => n is not null).ToHashSet(StringComparer.Ordinal);
        var sites = new List<Site>();
        foreach (var (path, root) in roots)
        {
            foreach (var node in root.DescendantNodes())
            {
                string? operation = node switch
                {
                    InvocationExpressionSyntax call when Name(call.Expression) is "ClearAllPools" or "SetEnvironmentVariable"
                        or "SetCurrentDirectory" => Name(call.Expression),
                    AssignmentExpressionSyntax assignment when Name(assignment.Left) is "DefaultThreadCurrentCulture"
                        or "DefaultThreadCurrentUICulture" or "CurrentDirectory" => Name(assignment.Left),
                    _ => null,
                };
                if (operation is null) continue;
                var owner = node.Ancestors().OfType<TypeDeclarationSyntax>().LastOrDefault();
                var type = string.Join(".", node.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.ValueText));
                var collection = owner?.AttributeLists.SelectMany(a => a.Attributes)
                    .FirstOrDefault(a => Name(a.Name) == "Collection");
                var collectionName = CollectionName(collection?.ArgumentList?.Arguments.FirstOrDefault()?.Expression);
                // An annotation on an arbitrary helper does not schedule its callers in xUnit.
                var isTest = owner?.Members.OfType<MethodDeclarationSyntax>()
                    .Any(m => m.AttributeLists.SelectMany(a => a.Attributes)
                        .Any(a => Name(a.Name) is "Fact" or "Theory" or "SkippableFact" or "SkippableTheory")) == true;
                var privateHelpers = node.Ancestors().OfType<TypeDeclarationSyntax>().TakeWhile(t => t != owner)
                    .All(t => t.Modifiers.Any(SyntaxKind.PrivateKeyword));
                sites.Add(new(path, type, operation, privateHelpers && isTest && collectionName is not null && nonparallel.Contains(collectionName)));
            }
        }
        return sites.ToArray();
    }

    internal static string[] Check(Site[] sites, IReadOnlyDictionary<string, int> debt)
    {
        var actual = sites.Where(s => !s.Isolated).GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.Count());
        return actual.Keys.Concat(debt.Keys).Distinct().Order(StringComparer.Ordinal)
            .Where(k => actual.GetValueOrDefault(k) != debt.GetValueOrDefault(k))
            .Select(k => $"{k}: unisolated sites {actual.GetValueOrDefault(k)}; reviewed legacy debt {debt.GetValueOrDefault(k)}. "
                + "Use a literal xUnit collection with DisableParallelization=true; helpers require caller review.")
            .ToArray();
    }

    private static string Name(SyntaxNode node) => node switch
    {
        MemberAccessExpressionSyntax m => Name(m.Name),
        QualifiedNameSyntax q => Name(q.Right),
        AliasQualifiedNameSyntax a => Name(a.Name),
        IdentifierNameSyntax i => i.Identifier.ValueText.Replace("Attribute", "", StringComparison.Ordinal),
        _ => "",
    };
    private static string? Literal(ExpressionSyntax? expression) => expression is LiteralExpressionSyntax literal
        && literal.IsKind(SyntaxKind.StringLiteralExpression) ? literal.Token.ValueText : null;
}
