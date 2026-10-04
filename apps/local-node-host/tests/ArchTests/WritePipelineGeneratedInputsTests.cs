using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Assets.Hierarchy;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.LocalNodeHost.Data.Entities;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

public sealed partial class WritePipelineExecutorFenceTests
{
    // This closes substitutions reached from the finite reviewed source closure.
    // It does not certify unrelated generator output or module initialization.
    private static bool HasReviewedCompiledHostCalls(Type owner, SemanticModel model, string imagePath)
    {
        // Bind host callees against genuine domain types in the default context.
        // The separate construction proof loads domain images too, which creates
        // duplicate type identities through domain contracts' dependency cycles.
        var context = new AssemblyLoadContext("reviewed-host-calls", isCollectible: true);
        try
        {
            using var image = File.OpenRead(imagePath);
            var assembly = context.LoadFromStream(image);
            return HasReviewedCompiledHostCallsCore(assembly.GetType(owner.FullName!, throwOnError: true)!, model);
        }
        finally { context.Unload(); }
    }

    private static bool HasReviewedCompiledHostCallsCore(Type owner, SemanticModel model)
    {
        var root = model.SyntaxTree.GetRoot();
        var selected = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(type => type.Identifier.ValueText is "Merge" or "CompositeAuthorization")
            .Cast<SyntaxNode>().Concat(root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(method => method.Parent is ClassDeclarationSyntax type
                    && type.Identifier.ValueText == "NodeHierarchyCompositeCoordinator"
                    && method.Identifier.ValueText is "MergeAsync" or "DecideAllAsync" or "ReadChildrenNotEndedAsync"))
            .Concat(root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
                .Where(property => property.Parent is ClassDeclarationSyntax type
                    && type.Identifier.ValueText == "NodeHierarchyCompositeCoordinator"))
            .ToArray();
        var callers = new HashSet<string>(StringComparer.Ordinal);
        var callees = new HashSet<string>(StringComparer.Ordinal);
        var loweredNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selectedNode in selected)
        {
            if (selectedNode is ClassDeclarationSyntax type && model.GetDeclaredSymbol(type) is INamedTypeSymbol declared)
                foreach (var constructor in declared.InstanceConstructors)
                    callers.Add(SourceMethodKey(constructor, model.Compilation.Assembly, owner.Assembly));
            foreach (var node in selectedNode.DescendantNodesAndSelf())
            {
                if (node is MethodDeclarationSyntax method && model.GetDeclaredSymbol(method) is IMethodSymbol declaredMethod)
                {
                    callers.Add(SourceMethodKey(declaredMethod, model.Compilation.Assembly, owner.Assembly));
                    loweredNames.Add(declaredMethod.Name);
                }
                if (node is PropertyDeclarationSyntax property && model.GetDeclaredSymbol(property) is IPropertySymbol declaredProperty
                    && declaredProperty.GetMethod is { } getter)
                    callers.Add(SourceMethodKey(getter, model.Compilation.Assembly, owner.Assembly));
                var symbol = model.GetSymbolInfo(node).Symbol;
                var called = symbol is IMethodSymbol methodSymbol ? methodSymbol.ReducedFrom ?? methodSymbol
                    : symbol is IPropertySymbol propertySymbol ? propertySymbol.GetMethod : null;
                if (called is not null && SymbolEqualityComparer.Default.Equals(called.ContainingAssembly, model.Compilation.Assembly))
                    callees.Add(SourceMethodKey(called, model.Compilation.Assembly, owner.Assembly));
            }
        }
        var methods = ConstructionOwnerTypes(owner)
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>().Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)))
            .Where(method => callers.Contains(RuntimeMethodKey(method))).ToArray();
        if (methods.Length != callers.Count) return false;
        var pending = new Queue<MethodBase>(methods);
        var seen = new HashSet<MethodBase>();
        while (pending.TryDequeue(out var method))
        {
            if (!seen.Add(method)) continue;
            if (method.GetCustomAttribute<AsyncStateMachineAttribute>() is { } state)
                foreach (var lowered in state.StateMachineType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                    pending.Enqueue(lowered);
            foreach (var call in RawMutationPortSymbolInventoryTests.CalledMethods(method))
            {
                var target = call.Target;
                if (target.DeclaringType?.Assembly != owner.Assembly) continue;
                if (callees.Contains(RuntimeMethodKey(target))) continue;
                var loweredType = target.DeclaringType;
                var inOwner = loweredType.FullName!.StartsWith(owner.FullName + "+", StringComparison.Ordinal);
                var compilerGenerated = loweredType.IsDefined(typeof(CompilerGeneratedAttribute), false);
                var namedLowering = loweredNames.Any(name => loweredType.Name.Contains("<" + name + ">", StringComparison.Ordinal)
                    || target.Name.Contains("<" + name + ">", StringComparison.Ordinal));
                var closureConstructor = target.IsConstructor && loweredType.Name.StartsWith("<>c", StringComparison.Ordinal);
                var anonymousValue = loweredType.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal);
                var collectionValue = target.IsConstructor && loweredType.Name == "<>z__ReadOnlyArray`1";
                if (!compilerGenerated || !(inOwner && (namedLowering || closureConstructor) || anonymousValue || collectionValue)) return false;
                pending.Enqueue(target);
                if (target.IsConstructor && namedLowering)
                    foreach (var lowered in loweredType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                        pending.Enqueue(lowered);
            }
        }
        return true;
    }

    private static IEnumerable<Type> ConstructionOwnerTypes(Type owner)
    {
        yield return owner;
        foreach (var child in owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            foreach (var nested in ConstructionOwnerTypes(child)) yield return nested;
    }

    private static string SourceMethodKey(IMethodSymbol method, IAssemblySymbol localAssembly, Assembly host)
    {
        method = method.OriginalDefinition;
        return SourceTypeKey(method.ContainingType, localAssembly, host) + "::" + method.MetadataName + "`" + method.Arity
            + "|" + method.IsStatic + "|" + SourceTypeKey(method.ReturnType, localAssembly, host) + (method.ReturnsByRef || method.ReturnsByRefReadonly ? "&" : "")
            + "|" + string.Join(",", method.Parameters.Select(parameter => parameter.RefKind + ":" + SourceTypeKey(parameter.Type, localAssembly, host)));
    }

    private static string SourceTypeKey(ITypeSymbol type, IAssemblySymbol localAssembly, Assembly host)
    {
        if (type is IArrayTypeSymbol array) return SourceTypeKey(array.ElementType, localAssembly, host) + "[" + new string(',', array.Rank - 1) + "]";
        if (type is ITypeParameterSymbol parameter) return (parameter.TypeParameterKind == TypeParameterKind.Method ? "!!" : "!") + parameter.Ordinal;
        if (type is IDynamicTypeSymbol) return RuntimeTypeKey(typeof(object));
        if (type is not INamedTypeSymbol named) return type.ToDisplayString();
        if (named.IsTupleType) named = named.TupleUnderlyingType!;
        var identity = SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, localAssembly) ? host.FullName : named.ContainingAssembly.Identity.ToString();
        var name = named.ContainingType is { } containing
            ? SourceTypeKey(containing, localAssembly, host).Split('|')[0] + "+" + named.MetadataName
            : named.ContainingNamespace.ToDisplayString() + "." + named.MetadataName;
        return name + "|" + identity + (named.TypeArguments.Length == 0 ? "" : "<" + string.Join(",", named.TypeArguments.Select(argument => SourceTypeKey(argument, localAssembly, host))) + ">");
    }

    private static string RuntimeMethodKey(MethodBase method)
    {
        if (method is MethodInfo { IsGenericMethod: true } generic) method = generic.GetGenericMethodDefinition();
        return RuntimeTypeKey(method.DeclaringType!) + "::" + method.Name + "`" + (method.IsGenericMethod ? method.GetGenericArguments().Length : 0)
            + "|" + method.IsStatic + "|" + RuntimeTypeKey(method is MethodInfo info ? info.ReturnType : typeof(void))
            + "|" + string.Join(",", method.GetParameters().Select(parameter => (parameter.IsOut ? "Out" : parameter.IsIn && parameter.ParameterType.IsByRef ? "In" : parameter.ParameterType.IsByRef ? "Ref" : "None")
                + ":" + RuntimeTypeKey(parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType)));
    }

    private static string RuntimeTypeKey(Type type)
    {
        if (type.IsByRef) return RuntimeTypeKey(type.GetElementType()!) + "&";
        if (type.IsArray) return RuntimeTypeKey(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        if (type.IsGenericParameter) return (type.DeclaringMethod is null ? "!" : "!!") + type.GenericParameterPosition;
        return (type.IsGenericType ? type.GetGenericTypeDefinition().FullName : type.FullName) + "|" + type.Assembly.FullName
            + (type.IsGenericType ? "<" + string.Join(",", type.GetGenericArguments().Select(RuntimeTypeKey)) + ">" : "");
    }

    [Fact]
    public async Task CompilerImageProofRejectsGeneratedPreludeInterception()
    {
        var physicalPath = Path.Combine(RepositoryRoot(), HostHierarchySource);
        var source = File.ReadAllText(physicalPath);
        var options = CSharpParseOptions.Default.WithFeatures([new("InterceptorsNamespaces", "GeneratedBoundaryWitness")]);
        var tree = CSharpSyntaxTree.ParseText(Microsoft.CodeAnalysis.Text.SourceText.From(source, new System.Text.UTF8Encoding(false),
            Microsoft.CodeAnalysis.Text.SourceHashAlgorithm.Sha256), path: physicalPath);
        var baseCompilation = BoundaryModel(tree).Compilation
            .WithAssemblyName(typeof(NodeHierarchyCompositeCoordinator).Assembly.GetName().Name!);
        baseCompilation = baseCompilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            Microsoft.CodeAnalysis.Text.SourceText.From("[assembly: System.Reflection.AssemblyVersion(\""
                + typeof(NodeHierarchyCompositeCoordinator).Assembly.GetName().Version + "\")]", System.Text.Encoding.UTF8),
            path: "GeneratedWitness.AssemblyInfo.cs"));
        var compilation = baseCompilation.RemoveAllSyntaxTrees().AddSyntaxTrees(baseCompilation.SyntaxTrees.Select(item =>
            CSharpSyntaxTree.ParseText(Microsoft.CodeAnalysis.Text.SourceText.From(item.GetText().ToString(), new System.Text.UTF8Encoding(false),
                Microsoft.CodeAnalysis.Text.SourceHashAlgorithm.Sha256), options, item.FilePath)));
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new DeferredPreludeGenerator()], parseOptions: options);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var generated, out var generatorDiagnostics);
        Assert.DoesNotContain(generatorDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        using var image = new MemoryStream();
        using var symbols = new MemoryStream();
        var emitted = generated.Emit(image, symbols, options: new Microsoft.CodeAnalysis.Emit.EmitOptions(debugInformationFormat: Microsoft.CodeAnalysis.Emit.DebugInformationFormat.PortablePdb));
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var assembly = Assembly.Load(image.ToArray());
        Assert.Equal(typeof(NodeHierarchyCompositeCoordinator).Assembly.FullName, assembly.FullName);
        var owner = assembly.GetType(typeof(NodeHierarchyCompositeCoordinator).FullName!)!;
        var generatedOwner = assembly.GetType("GeneratedBoundaryWitness.Interceptor")!;
        var originalTree = generated.SyntaxTrees.Single(item => item.FilePath == physicalPath);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(physicalPath))),
            Convert.ToHexString(originalTree.GetText().GetChecksum().ToArray()));
        var originalModel = generated.GetSemanticModel(originalTree);
        var declaration = originalTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == "MergeAsync");
        Assert.True(RunsExecutorInsideAtomicCallback(declaration, originalModel));
        var writer = RuntimeHelpers.GetUninitializedObject(owner.GetNestedType("Merge", BindingFlags.NonPublic)!);
        var observer = new GeneratedWitnessObserver();
        generatedOwner.GetField("OnReleased")!.SetValue(null, (Func<Task>)(async () =>
        {
            var method = typeof(WritePipeline).GetMethods().Single(candidate => candidate.Name == "RunAsync" && candidate.IsGenericMethodDefinition)
                .MakeGenericMethod(writer.GetType().BaseType!.GetGenericArguments());
            var value = method.Invoke(null, [writer, observer, CancellationToken.None])!;
            var task = (Task)value.GetType().GetMethod("AsTask")!.Invoke(value, null)!;
            await Assert.ThrowsAsync<NullReferenceException>(() => task);
        }));
        var instance = RuntimeHelpers.GetUninitializedObject(owner);
        var fields = owner.GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        fields.Single(field => field.FieldType == typeof(TimeProvider)).SetValue(instance, TimeProvider.System);
        fields.Single(field => field.FieldType == typeof(IWritePipelineObserver)).SetValue(instance, observer);
        var unit = DispatchProxy.Create<IHierarchyCompositeUnitOfWork, GeneratedWitnessUnit>();
        ((GeneratedWitnessUnit)(object)unit).Observer = observer;
        fields.Single(field => field.FieldType == typeof(IHierarchyCompositeUnitOfWork)).SetValue(instance, unit);
        var actor = new ActorId("generated-boundary-witness");
        var tenant = new TenantId("generated-boundary-witness");
        using var body = System.Text.Json.JsonDocument.Parse("{}");
        var run = (Task<MergeResult>)owner.GetMethod("MergeAsync")!.Invoke(instance, [Array.Empty<EntityId>(), new SchemaId("boundary.witness"), body,
            new CreateOptions("entity", "test", "boundary-witness", actor, tenant, ExplicitLocalPart: "boundary-witness"), "boundary-witness", actor, tenant,
            DateTimeOffset.UnixEpoch, CancellationToken.None])!;
        try
        {
            await Assert.ThrowsAsync<NullReferenceException>(() => run);
            Assert.Equal(1, observer.Inside);
            Assert.False(observer.InAtomicCallback);
        }
        finally
        {
            ((TaskCompletionSource)generatedOwner.GetField("Release")!.GetValue(null)!).TrySetResult();
            await ((TaskCompletionSource)generatedOwner.GetField("Finished")!.GetValue(null)!).Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(1, observer.Outside);
        var directory = Path.Combine(Path.GetTempPath(), "generator-host-proof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var output = Path.GetDirectoryName(typeof(InMemoryEntityStore).Assembly.Location)!;
            foreach (var name in new[] { "Harborline.Api.Foundation", "Harborline.Api.Kernel.Runtime" })
                foreach (var extension in new[] { ".compile-image", ".compile-symbols", ".compile-inputs.txt" })
                    File.Copy(Path.Combine(output, name + extension), Path.Combine(directory, name + extension));
            var hostPath = Path.Combine(directory, assembly.GetName().Name + ".compile-image");
            File.WriteAllBytes(hostPath, image.ToArray());
            File.WriteAllBytes(Path.ChangeExtension(hostPath, ".compile-symbols"), symbols.ToArray());
            File.WriteAllLines(Path.ChangeExtension(hostPath, ".compile-inputs.txt"), ["assembly|" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image.ToArray())),
                 "source|" + physicalPath + "|" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(physicalPath)))]);
            Assert.True(HasMatchingCompilerImage(assembly, hostPath), "Generated witness image identity must be accepted before testing the fence.");
            var hostMethod = owner.GetMethod("MergeAsync")!;
            var hostBody = hostMethod.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.True(HasBoundCompiledSource(hostBody, HostHierarchySource, hostPath), "Generated witness must retain the authenticated physical caller source.");
            Assert.False(HasReviewedConstructionArtifacts(directory, assembly));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    internal sealed class GeneratedWitnessObserver : IWritePipelineObserver
    {
        internal bool InAtomicCallback;
        internal int Inside;
        internal int Outside;
        public void OnStage(WritePipelineStage stage)
        {
            Assert.Equal(WritePipelineStage.Authorize, stage);
            if (InAtomicCallback) Interlocked.Increment(ref Inside);
            else Interlocked.Increment(ref Outside);
        }
    }

    public class GeneratedWitnessUnit : DispatchProxy
    {
        private GeneratedWitnessObserver? observer;
        internal GeneratedWitnessObserver Observer { set => observer = value; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.Equal(nameof(IHierarchyCompositeUnitOfWork.ExecuteAtomicAsync), targetMethod!.Name);
            return typeof(GeneratedWitnessUnit).GetMethod(nameof(RunAtomic), BindingFlags.Instance | BindingFlags.NonPublic)!
                .MakeGenericMethod(targetMethod.GetGenericArguments()).Invoke(this, args);
        }

        private async Task<T> RunAtomic<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
        {
            observer!.InAtomicCallback = true;
            try { return await action(ct); }
            finally { observer.InAtomicCallback = false; }
        }
    }

    private sealed class DeferredPreludeGenerator : ISourceGenerator
    {
        public void Initialize(GeneratorInitializationContext context) { }
        public void Execute(GeneratorExecutionContext context)
        {
            var tree = context.Compilation.SyntaxTrees.Single(item => item.FilePath.EndsWith("NodeHierarchyCompositeCoordinator.cs", StringComparison.Ordinal));
            var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(item => item.Identifier.ValueText == "MergeAsync");
            var call = method.DescendantNodes().OfType<InvocationExpressionSyntax>().First(item => item.Expression.ToString() == "ArgumentNullException.ThrowIfNull");
#pragma warning disable RSEXPERIMENTAL002
            var location = context.Compilation.GetSemanticModel(tree).GetInterceptableLocation(call)!;
            var attribute = location.GetInterceptsLocationAttributeSyntax();
#pragma warning restore RSEXPERIMENTAL002
            context.AddSource("DeferredPrelude.g.cs", $$"""
                #nullable enable
                namespace System.Runtime.CompilerServices
                {
                    [System.AttributeUsage(System.AttributeTargets.Method)]
                    public sealed class InterceptsLocationAttribute(int version, string data) : System.Attribute { }
                }
                namespace GeneratedBoundaryWitness
                {
                    public static class Interceptor
                    {
                        public static System.Func<System.Threading.Tasks.Task>? OnReleased;
                        public static readonly System.Threading.Tasks.TaskCompletionSource Release = new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                        public static readonly System.Threading.Tasks.TaskCompletionSource Finished = new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                        {{attribute}}
                        public static void Guard(object? argument, string? paramName)
                        {
                            _ = System.Threading.Tasks.Task.Run(async () =>
                            {
                                await Release.Task;
                                try { await OnReleased!(); Finished.TrySetResult(); }
                                catch (System.Exception failure) { Finished.TrySetException(failure); }
                            });
                            System.ArgumentNullException.ThrowIfNull(argument, paramName);
                        }
                    }
                }
                """);
        }
    }
}
