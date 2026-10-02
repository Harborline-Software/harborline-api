using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Security.Cryptography;
using System.Text;

internal static class IsolationGuard
{
    internal sealed record Site(string Path, string Type, string Operation, bool Isolated, string TypeSha256)
    {
        public string Key => $"{Path}\t{Type}\t{Operation}";
    }
    internal sealed record LegacyDebt(int Count, string TypeSha256, string Owner, string Reason);
    private static readonly MetadataReference XunitReference = MetadataReference.CreateFromFile(typeof(Xunit.CollectionAttribute).Assembly.Location);
    private static readonly MetadataReference SkippableReference = MetadataReference.CreateFromFile(typeof(Xunit.SkippableFactAttribute).Assembly.Location);
    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Where(p => Path.GetFileName(p) is "System.Private.CoreLib.dll" or "System.Runtime.dll" or "netstandard.dll")
        .Select(p => MetadataReference.CreateFromFile(p)).Cast<MetadataReference>()
        .Concat([XunitReference, SkippableReference]).ToArray();

    // Deliberately conservative syntax fence: these member names are forbidden regardless of
    // receiver spelling (including aliases/static imports). This is not a call graph, restoration
    // proof, or arbitrary concurrency-safety claim. New mutating helpers require explicit review.
    internal static Site[] Scan(IReadOnlyDictionary<string, string> files)
    {
        var trees = files.ToDictionary(p => p.Key, p => CSharpSyntaxTree.ParseText(p.Value));
        var roots = trees.ToDictionary(p => p.Key, p => p.Value.GetRoot());
        var compilation = CSharpCompilation.Create("HostTestIsolationBinding", trees.Values, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var models = trees.Values.ToDictionary(t => t, t => compilation.GetSemanticModel(t));
        var xunit = (IAssemblySymbol)compilation.GetAssemblyOrModuleSymbol(XunitReference)!;
        var skippable = (IAssemblySymbol)compilation.GetAssemblyOrModuleSymbol(SkippableReference)!;
        bool AttributeIs(AttributeSyntax attribute, string name, IAssemblySymbol assembly) =>
            models[attribute.SyntaxTree].GetSymbolInfo(attribute).Symbol is IMethodSymbol constructor
            && SymbolEqualityComparer.Default.Equals(constructor.ContainingType, assembly.GetTypeByMetadataName(name));
        string? CollectionName(ExpressionSyntax? expression) => expression is not null
            && models[expression.SyntaxTree].GetConstantValue(expression) is { HasValue: true, Value: string value } ? value : null;
        var nonparallel = roots.Values.SelectMany(r => r.DescendantNodes().OfType<AttributeSyntax>())
            .Where(a => AttributeIs(a, "Xunit.CollectionDefinitionAttribute", xunit))
            .Select(a => new
            {
                Name = CollectionName(a.ArgumentList?.Arguments.FirstOrDefault()?.Expression),
                Serial = a.ArgumentList?.Arguments.Any(x => x.NameEquals?.Name.Identifier.ValueText == "DisableParallelization"
                    && x.Expression.IsKind(SyntaxKind.TrueLiteralExpression)) == true,
            })
            .Where(d => d.Name is not null).GroupBy(d => d.Name)
            .Where(g => g.Count() == 1 && g.Single().Serial).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
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
                    .FirstOrDefault(a => AttributeIs(a, "Xunit.CollectionAttribute", xunit));
                var collectionName = CollectionName(collection?.ArgumentList?.Arguments.FirstOrDefault()?.Expression);
                // An annotation on an arbitrary helper does not schedule its callers in xUnit.
                var isTest = owner?.Members.OfType<MethodDeclarationSyntax>()
                    .Any(m => m.AttributeLists.SelectMany(a => a.Attributes)
                        .Any(a => AttributeIs(a, "Xunit.FactAttribute", xunit) || AttributeIs(a, "Xunit.TheoryAttribute", xunit)
                            || AttributeIs(a, "Xunit.SkippableFactAttribute", skippable) || AttributeIs(a, "Xunit.SkippableTheoryAttribute", skippable))) == true;
                var privateHelpers = node.Ancestors().OfType<TypeDeclarationSyntax>().TakeWhile(t => t != owner)
                    .All(t => t.Modifiers.Any(SyntaxKind.PrivateKeyword));
                var declaration = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                var code = string.Concat((declaration ?? node).DescendantTokens()
                    .Select(t => (t.RawKind, Text: t.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal)))
                    .Select(t => $"{t.RawKind}:{t.Text.Length}:{t.Text}"));
                var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();
                sites.Add(new(path, type, operation, privateHelpers && isTest && collectionName is not null && nonparallel.Contains(collectionName), fingerprint));
            }
        }
        return sites.ToArray();
    }

    internal static string[] CheckHashes(Site[] sites, IReadOnlyDictionary<string, LegacyDebt> debt) =>
        sites.Where(s => !s.Isolated && debt.ContainsKey(s.Key) && s.TypeSha256 != debt[s.Key].TypeSha256)
            .Select(s => $"{s.Key}: legacy type code changed; {debt[s.Key].Owner} review required ({debt[s.Key].Reason}).")
            .Distinct().Order(StringComparer.Ordinal).ToArray();

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
        IdentifierNameSyntax i => i.Identifier.ValueText,
        _ => "",
    };
}
