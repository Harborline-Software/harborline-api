using Harborline.Api.Foundation.Authorization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Assets.Hierarchy;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.LocalNodeHost.Data.Entities;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// ck-10 (DES-0029): <see cref="WritePipeline.RunAsync"/> is the one ADR-0038 executor. The compiled IL of
/// every production Harborline assembly is scanned for three things. No code iterates the stage order to run
/// its own loop. No code calls a <see cref="KernelWrite{TBound, TMutation, TSealed, TResult}"/> stage except the
/// executor. Every admitted-write commit site is either a KernelWrite commit stage or a row on the reviewed
/// inventory of paths not yet on the executor, each naming the plan slice that moves it. The commit sinks
/// are the two merged fences' (<see cref="RawMutationPortSymbolInventoryTests"/> and
/// <see cref="AuthorizationDefinitionWriteFenceTests"/>). The inventory is the measurable gap: each slice
/// deletes its rows, and a new write path off the executor reds this fence until it is reviewed.
/// </summary>
public sealed partial class WritePipelineExecutorFenceTests
{
    private static readonly string[] StageNames =
        ["AuthorizeAsync", "BindAsync", "MutateAsync", "ValidateAsync", "CommitAsync", "ReactAsync"];

    /// <summary>Admitted write paths that commit outside the executor: path | caller, and the slice that moves it.</summary>
    private static readonly (string Key, string Slice)[] NotYetOnTheExecutor =
    [
        ("packages/blocks-workflow/src/durable/AuthorizedWorkflowDefinitionLifecycle.cs|Harborline.Api.Blocks.Workflow.Durable.AuthorizedWorkflowDefinitionLifecycle+EntityWriterBackend.RegisterAsync",
            "S3 workflow definition lifecycle"),
        ("packages/foundation-forms/AuthorizedFormDefinitionLifecycle.cs|Harborline.Api.Foundation.Forms.AuthorizedFormDefinitionLifecycle+EntityWriterBackend.RegisterAsync",
            "S3 form definition lifecycle"),
        ("packages/foundation/Definitions/EntityStoreDefinitionLifecycle.cs|Harborline.Api.Foundation.Definitions.EntityStoreDefinitionLifecycle`1[!0].TransitionAsync",
            "S3 definition lifecycle"),
    ];

    internal static string[] NotYetOnTheExecutorRows() => [.. NotYetOnTheExecutor.Select(row => row.Key)];
    /// <summary>
    /// ck-10 S6: admitted-write sites that stay off the executor for good, each with its reason. An exempt site must itself
    /// run <see cref="WritePipeline.RunAsync"/> inside the unit it opens, so its writes are still a KernelWrite's commit
    /// stage; the fence checks that, not only the row.
    /// </summary>
    private static readonly (string Key, string Reason)[] ExemptFromTheExecutor =
    [
        ("apps/local-node-host/Data/Entities/NodeHierarchyCompositeCoordinator.cs|Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator.MergeAsync",
            "Unit opener: merge reads the displaced children it decides inside its atomic unit (ticket 216, review round 7), so the whole pipeline runs inside the unit it opens; its writes are Merge.CommitAsync"),
    ];

    internal static string[] ExemptFromTheExecutorRows() => [.. ExemptFromTheExecutor.Select(row => row.Key)];


    /// <summary>Admitted-write commit sites in production that are not a KernelWrite commit stage.</summary>
    internal static string[] DiscoveredOffExecutorKeys() => Classify(ProductionAssemblies()).Off;

    [Fact(DisplayName = "ck-10 fence: no production code runs its own loop over the ADR 0038 stage order")]
    public void NoProductionCodeIteratesTheStageOrder()
    {
        var getter = typeof(WritePipeline).GetProperty(nameof(WritePipeline.Order))!.GetMethod!;
        var sites = RawMutationPortSymbolInventoryTests.DiscoverCalls(ProductionAssemblies(), target => target == getter);

        Assert.True(sites.Length == 0,
            "Run the write through WritePipeline.RunAsync instead:\n" + string.Join("\n", sites.Select(Describe)));
    }

    [Fact(DisplayName = "ck-10 fence: only the executor calls a KernelWrite stage")]
    public void OnlyTheExecutorCallsAKernelWriteStage()
    {
        var offenders = StageCalls(ProductionAssemblies()).Where(site => !IsExecutor(site)).ToArray();

        Assert.True(offenders.Length == 0,
            "A KernelWrite stage is called outside WritePipeline.RunAsync:\n" + string.Join("\n", offenders.Select(Describe)));
        Assert.Contains(StageCalls([typeof(WritePipeline).Assembly]), IsExecutor);
    }

    [Fact(DisplayName = "ck-10 fence: every admitted-write commit is a KernelWrite commit stage or a reviewed not-yet-moved path")]
    public void EveryAdmittedWriteCommitIsOnTheExecutorOrReviewed()
    {
        var assemblies = ProductionAssemblies();
        var (onExecutor, off) = Classify(assemblies);

        var exempt = ExemptFromTheExecutor.Select(row => row.Key).ToArray();
        Assert.Empty(exempt.Intersect(NotYetOnTheExecutor.Select(row => row.Key), StringComparer.Ordinal));
        var reviewed = NotYetOnTheExecutor.Select(row => row.Key).Concat(exempt).Order(StringComparer.Ordinal).ToArray();
        Assert.True(reviewed.SequenceEqual(off, StringComparer.Ordinal),
            "ck-10 not-yet-on-the-executor inventory mismatch.\nUnreviewed (move onto WritePipeline.RunAsync, or review with a slice):\n"
            + string.Join("\n", off.Except(reviewed))
            + "\nStale (on the executor now; delete the row):\n" + string.Join("\n", reviewed.Except(off)));
        Assert.All(NotYetOnTheExecutor, row => Assert.StartsWith("S", row.Slice, StringComparison.Ordinal));
        Assert.All(ExemptFromTheExecutor, row => Assert.False(string.IsNullOrWhiteSpace(row.Reason)));

        // The authorization configuration writer and the admission conferral commit from a KernelWrite.
        Assert.Contains(onExecutor, key => key.Contains("AuthorizationDefinitionWriter+ConfigurationWrite.CommitAsync", StringComparison.Ordinal));
        Assert.Contains(onExecutor, key => key.Contains("AuthorizationDefinitionWriter+ConferralWrite.CommitAsync", StringComparison.Ordinal));
        // ck-10 S2: the generic record create, update and delete commit from a KernelWrite.
        foreach (var write in new[] { "RecordCreate", "RecordUpdate", "RecordDelete" })
            Assert.Contains(onExecutor, key => key.Contains($"NodeEntityWriter+{write}.CommitAsync", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ck-10 S6 fence: every exempt site runs the executor inside the unit it opens")]
    public void EveryExemptSiteRunsTheExecutorInsideItsUnit()
    {
        var assemblies = ProductionAssemblies();
        var mutationSites = RawMutationPortSymbolInventoryTests.DiscoverCalls(assemblies, IsCommitSink);
        var runCallers = RawMutationPortSymbolInventoryTests.DiscoverCalls(assemblies,
                target => target.DeclaringType == typeof(WritePipeline) && target.Name == nameof(WritePipeline.RunAsync))
            .Select(site => site.Symbol)
            .ToArray();

        var repositoryRoot = RepositoryRoot();
        Assert.All(ExemptFromTheExecutor, row =>
        {
            var caller = row.Key[(row.Key.IndexOf('|') + 1)..];
            var method = caller[(caller.LastIndexOf('.') + 1)..];
            var type = caller[..caller.LastIndexOf('.')];
            // The exemption reviews ONE opener, not every sink sharing the same path|method key.
            // Keep ordinals and targets: two calls to the same opener must not collapse into one row.
            var sites = mutationSites.Where(site => site.Path == row.Key[..row.Key.IndexOf('|')]
                && (site.Symbol.StartsWith(caller + "(", StringComparison.Ordinal)
                    || (site.Symbol.StartsWith(type + "+", StringComparison.Ordinal)
                            || site.Symbol.StartsWith(type + ".", StringComparison.Ordinal))
                        && site.Symbol.Contains($"<{method}>", StringComparison.Ordinal))).ToArray();
            Assert.True(HasOnlyReviewedExemptionSite(sites),
                $"Exempt site {caller} has unreviewed mutation sites:\n" + string.Join("\n", sites.Select(site =>
                    $"{Describe(site)} -> {site.Target} #{site.Ordinal}")));
            // Retain the IL check so the source check cannot accept a different RunAsync symbol.
            Assert.True(
                runCallers.Any(symbol => symbol.StartsWith(type, StringComparison.Ordinal)
                    && (symbol.Contains($".{method}(", StringComparison.Ordinal) || symbol.Contains($"<{method}>", StringComparison.Ordinal))),
                $"Exempt site {caller} does not run WritePipeline.RunAsync inside its unit; move it onto the executor or remove the exemption.");
            var source = File.ReadAllText(Path.Combine(repositoryRoot, row.Key[..row.Key.IndexOf('|')]));
            var tree = CSharpSyntaxTree.ParseText(source);
            var declaration = tree.GetRoot().DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Single(node => node.Identifier.ValueText == method);
            Assert.True(RunsExecutorInsideAtomicCallback(declaration, BoundaryModel(tree)),
                $"Exempt site {caller} must run the executor in the callback passed to ExecuteAtomicAsync.");
        });
    }

    private static bool HasOnlyReviewedExemptionSite(IReadOnlyList<RawMutationPortSymbolInventoryTests.CallSite> sites)
    {
        var reviewed = typeof(IHierarchyCompositeUnitOfWork).GetMethod(nameof(IHierarchyCompositeUnitOfWork.ExecuteAtomicAsync))!
            .MakeGenericMethod(typeof(MergeResult));
        return sites.Count == 1 && sites[0].Ordinal == 0
            && sites[0].Target == MethodSignatureSymbol.Format(reviewed);
    }

    [Fact]
    public void ExemptionCountsEveryMutationSiteWithoutCollapsingTargetsOrOrdinals()
    {
        var target = MethodSignatureSymbol.Format(typeof(IHierarchyCompositeUnitOfWork)
            .GetMethod(nameof(IHierarchyCompositeUnitOfWork.ExecuteAtomicAsync))!.MakeGenericMethod(typeof(MergeResult)));
        var opener = new RawMutationPortSymbolInventoryTests.CallSite("fixture.cs", "Fixture.MergeAsync()", 1, target, 0);
        Assert.True(HasOnlyReviewedExemptionSite([opener]));
        Assert.False(HasOnlyReviewedExemptionSite([]));
        Assert.False(HasOnlyReviewedExemptionSite([opener, opener with { Ordinal = 1 }]));
        Assert.False(HasOnlyReviewedExemptionSite([opener, opener with { Target = "OtherMutation", Ordinal = 0 }]));
    }

    [Theory]
    // Oracle: the exemption requires execution in the atomic callback, not merely in the same method.
    [InlineData("return unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct));", true)]
    [InlineData("return unit.ExecuteAtomicAsync(async ct => { return await WritePipeline.RunAsync(write, observer, ct); });", true)]
    [InlineData("return unit.ExecuteAtomicAsync(ct => WritePipeline.RunAsync(write, observer, ct).AsTask());", true)]
    [InlineData("return unit.ExecuteAtomicAsync(ct => { return WritePipeline.RunAsync(write, observer, ct).AsTask(); });", true)]
    [InlineData("return unit.ExecuteAtomicAsync(async ct => { await WritePipeline.RunAsync(write, observer, ct).ConfigureAwait(false); return await Done(); });", false)]
    [InlineData("return unit.ExecuteAtomicAsync(async ct => await RealPipeline.RunAsync(write, observer, ct));", true)]
    [InlineData("return unit.ExecuteAtomicAsync(async ct => await Harborline.Api.Kernel.Runtime.WritePipeline.RunAsync(write, observer, ct));", true)]
    [InlineData("return unit.ExecuteAtomicAsync(ct => { _ = WritePipeline.RunAsync(write, observer, ct); return Done(); });", false)]
    [InlineData("return unit.ExecuteAtomicAsync(async ct => { WritePipeline.RunAsync(write, observer, ct); return await Done(); });", false)]
    [InlineData("return unit.ExecuteAtomicAsync(ct => { var pending = WritePipeline.RunAsync(write, observer, ct); return Done(); });", false)]
    [InlineData("return unit.ExecuteAtomicAsync(async ct => { return WritePipeline.RunAsync(write, observer, ct); });", false)]
    [InlineData("return unit.ExecuteAtomicAsync(async ct => WritePipeline.RunAsync(write, observer, ct));", false)]
    [InlineData("await WritePipeline.RunAsync(write, observer, ct); return unit.ExecuteAtomicAsync(ct => Done());", false)]
    [InlineData("Func<Task> other = async () => await WritePipeline.RunAsync(write, observer, ct); return unit.ExecuteAtomicAsync(ct => Done());", false)]
    [InlineData("return unit.ExecuteAtomicAsync(ct => { Func<Task> other = async () => await WritePipeline.RunAsync(write, observer, ct); return Done(); });", false)]
    [InlineData("await WritePipeline.RunAsync(write, observer, ct); return unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct));", false)]
    [InlineData("return unit.ExecuteAtomicAsync(ct => Done());", false)]
    [InlineData("await unit.ExecuteAtomicAsync(ct => Done()); return unrelated.ExecuteAtomicAsync(ct => WritePipeline.RunAsync(write, observer, ct).AsTask());", false)]
    public void ExemptionRejectsExecutorOutsideAtomicCallback(string body, bool expected)
    {
        // The unit parameter binds to the shipping interface; the unrelated receiver deliberately has the
        // same method name and callback shape, but opens no reviewed hierarchy transaction.
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Harborline.Api.Foundation.Assets.Hierarchy;
            using Harborline.Api.Kernel.Runtime;
            using RealPipeline = Harborline.Api.Kernel.Runtime.WritePipeline;
            class UnrelatedAtomic {
                public Task<int> ExecuteAtomicAsync(Func<CancellationToken, Task<int>> action) => action(default);
            }
            class Fixture {
                Task<int> Done() => Task.FromResult(0);
                async Task<int> MergeAsync(IHierarchyCompositeUnitOfWork unit, UnrelatedAtomic unrelated,
                    KernelWrite<object, int, int, int> write, IWritePipelineObserver observer, CancellationToken ct) {
            """ + body.Replace("return unit.ExecuteAtomicAsync", "return await unit.ExecuteAtomicAsync",
                StringComparison.Ordinal) + " } }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var declaration = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(node => node.Identifier.ValueText == "MergeAsync");
        var model = BoundaryModel(tree);
        if (expected)
            Assert.DoesNotContain(model.Compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Equal(expected, RunsExecutorInsideAtomicCallback(declaration, model));
    }

    [Fact]
    public void ExemptionRejectsAFakePipelineAliasEvenWhenTheRealExecutorIsPresentElsewhere()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            using System.Threading;
            using System.Threading.Tasks;
            using Harborline.Api.Foundation.Assets.Hierarchy;
            using Harborline.Api.Kernel.Runtime;
            using WritePipeline = FakePipeline;
            static class FakePipeline {
                public static Task<int> RunAsync(object write, object observer, CancellationToken ct) => Task.FromResult(1);
            }
            class Fixture {
                Task<int> MergeAsync(IHierarchyCompositeUnitOfWork unit,
                    KernelWrite<object, int, int, int> write, IWritePipelineObserver observer, CancellationToken ct) {
                    async Task Deferred() {
                        await Harborline.Api.Kernel.Runtime.WritePipeline.RunAsync(write, observer, ct);
                    }
                    return unit.ExecuteAtomicAsync(ct => WritePipeline.RunAsync(write, observer, ct));
                }
            }
            """);
        var declaration = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(node => node.Identifier.ValueText == "MergeAsync");
        var model = BoundaryModel(tree);
        Assert.DoesNotContain(model.Compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.False(RunsExecutorInsideAtomicCallback(declaration, model));
    }

    [Theory]
    [InlineData("async Task<int> Deferred() { return await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct)); } return await Done();")]
    [InlineData("Func<Task<int>> deferred = async () => await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct)); return await Done();")]
    [InlineData("Func<Task<int>> deferred = async delegate { return await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct)); }; return await Done();")]
    [InlineData("if (false) { return await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct)); } return await Done();")]
    [InlineData("return await Done(); return await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct));")]
    [InlineData("return false ? await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct)) : await Done();")]
    [InlineData("_ = unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct)); return await Done();")]
    [InlineData("return await unit.ExecuteAtomicAsync(async ct => { if (false) { await WritePipeline.RunAsync(write, observer, ct); } return await Done(); });")]
    [InlineData("return await unit.ExecuteAtomicAsync(async ct => { return await Done(); await WritePipeline.RunAsync(write, observer, ct); });")]
    public void ExemptionRejectsDeferredDiscardedOrUnreachableAtomicExecution(string body)
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Harborline.Api.Foundation.Assets.Hierarchy;
            using Harborline.Api.Kernel.Runtime;
            class Fixture {
                Task<int> Done() => Task.FromResult(0);
                async Task<int> MergeAsync(IHierarchyCompositeUnitOfWork unit,
                    KernelWrite<object, int, int, int> write, IWritePipelineObserver observer, CancellationToken ct) {
            """ + body + " } }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var declaration = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(node => node.Identifier.ValueText == "MergeAsync");
        var model = BoundaryModel(tree);
        Assert.DoesNotContain(model.Compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.False(RunsExecutorInsideAtomicCallback(declaration, model));
    }

    [Theory]
    [InlineData("await unit.InvalidateEdgeAsync(1, DateTimeOffset.UnixEpoch); return await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct));")]
    [InlineData("await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct)); await unit.InvalidateEdgeAsync(1, DateTimeOffset.UnixEpoch); return await Done();")]
    [InlineData("return await unit.ExecuteAtomicAsync(async ct => { await unit.InvalidateEdgeAsync(1, DateTimeOffset.UnixEpoch); return await WritePipeline.RunAsync(write, observer, ct); });")]
    [InlineData("async Task Deferred() { await unit.InvalidateEdgeAsync(1, DateTimeOffset.UnixEpoch); } return await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct));")]
    [InlineData("if (false) { await unit.InvalidateEdgeAsync(1, DateTimeOffset.UnixEpoch); } return await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct));")]
    [InlineData("await unit.ExecuteAtomicAsync(ct => Done()); return await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct));")]
    [InlineData("async Task Deferred() { await unit.ExecuteAtomicAsync(ct => Done()); } return await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct));")]
    [InlineData("return await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct), Token(unit.InvalidateEdgeAsync(1, DateTimeOffset.UnixEpoch)));")]
    [InlineData("return await unit.ExecuteAtomicAsync(async ct => { await WritePipeline.RunAsync(write, observer, ct); return await WritePipeline.RunAsync(write, observer, ct); });")]
    public void ExemptionRejectsEveryAdditionalMutationOrOpenerEvenWithAValidExecutor(string body)
    {
        var tree = CSharpSyntaxTree.ParseText("""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Harborline.Api.Foundation.Assets.Hierarchy;
            using Harborline.Api.Kernel.Runtime;
            class Fixture {
                Task<int> Done() => Task.FromResult(0);
                CancellationToken Token(Task sideEffect) => default;
                async Task<int> MergeAsync(IHierarchyCompositeUnitOfWork unit,
                    KernelWrite<object, int, int, int> write, IWritePipelineObserver observer) {
            """ + body + " } }");
        var declaration = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(node => node.Identifier.ValueText == "MergeAsync");
        var model = BoundaryModel(tree);
        Assert.DoesNotContain(model.Compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.False(RunsExecutorInsideAtomicCallback(declaration, model));
    }

    [Theory]
    [InlineData("if (Opaque(unit)) return await Done(); return await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct));")]
    [InlineData("return await unit.ExecuteAtomicAsync(async ct => { if (Opaque(unit)) return await Done(); return await WritePipeline.RunAsync(write, observer, ct); });")]
    [InlineData("Func<KernelWrite<object, int, int, int>, IWritePipelineObserver, CancellationToken, ValueTask<int>> escaped = WritePipeline.RunAsync<object, int, int, int>; await unit.ExecuteAtomicAsync(async ct => { if (Opaque(unit)) return await Done(); return await WritePipeline.RunAsync(write, observer, ct); }); return await escaped.Invoke(write, observer, default);")]
    [InlineData("Func<KernelWrite<object, int, int, int>, IWritePipelineObserver, CancellationToken, ValueTask<int>> escaped = WritePipeline.RunAsync<object, int, int, int>; await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct)); return await escaped(write, observer, default);")]
    [InlineData("Task<int> Escape() => Done(); await Escape(); return await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct));")]
    public void ExemptionRejectsEarlyExitAndIndirectExecution(string body)
    {
        var tree = CSharpSyntaxTree.ParseText(BoundaryFixture(body));
        var declaration = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(node => node.Identifier.ValueText == "MergeAsync");
        var model = BoundaryModel(tree);
        Assert.DoesNotContain(model.Compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.False(RunsExecutorInsideAtomicCallback(declaration, model));
    }

    [Fact]
    public void ExemptionRejectsOrdinaryHelperEscapingTheAtomicCallback()
    {
        var tree = CSharpSyntaxTree.ParseText(BoundaryFixture("""
            await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct));
            return await Escape.Run(write, observer);
            """) + """
            class Escape {
                public static async Task<int> Run(
                    Harborline.Api.Kernel.Runtime.KernelWrite<object, int, int, int> write,
                    Harborline.Api.Kernel.Runtime.IWritePipelineObserver observer) =>
                    await Harborline.Api.Kernel.Runtime.WritePipeline.RunAsync(write, observer, default);
            }
            """);
        var declaration = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(node => node.Identifier.ValueText == "MergeAsync");
        var model = BoundaryModel(tree);
        Assert.DoesNotContain(model.Compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.False(RunsExecutorInsideAtomicCallback(declaration, model));
    }

    [Theory]
    [InlineData("var escape = new Escape(write, observer); await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct)); return await escape.Pending;")]
    [InlineData("await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct)); var escape = new Escape(write, observer); return await escape.PendingField;")]
    [InlineData("await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct)); return await Escape.PendingStatic;")]
    [InlineData("await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct)); return await Escape.Shared[0];")]
    [InlineData("await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct)); return await +Escape.Shared;")]
    [InlineData("await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct)); Task<int> pending = Escape.Shared; return await pending;")]
    public void ExemptionRejectsNonInvocationEffectsOutsideAtomicCallback(string body)
    {
        var tree = CSharpSyntaxTree.ParseText(BoundaryFixture(body) + """
            class Escape {
                static KernelWrite<object, int, int, int> write = null;
                static IWritePipelineObserver observer = null;
                public static Escape Shared;
                public readonly Task<int> PendingField;
                public Escape(KernelWrite<object, int, int, int> supplied, IWritePipelineObserver observed) {
                    write = supplied; observer = observed;
                    PendingField = WritePipeline.RunAsync(write, observer, default).AsTask();
                }
                public Task<int> Pending => WritePipeline.RunAsync(write, observer, default).AsTask();
                public static Task<int> PendingStatic => WritePipeline.RunAsync(write, observer, default).AsTask();
                public Task<int> this[int index] => WritePipeline.RunAsync(write, observer, default).AsTask();
                public static Task<int> operator +(Escape value) => WritePipeline.RunAsync(write, observer, default).AsTask();
                public static implicit operator Task<int>(Escape value) => WritePipeline.RunAsync(write, observer, default).AsTask();
            }
            """);
        var declaration = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(node => node.Identifier.ValueText == "MergeAsync");
        var model = BoundaryModel(tree);
        Assert.DoesNotContain(model.Compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.False(RunsExecutorInsideAtomicCallback(declaration, model));
    }

    [Fact]
    public void ExemptionRejectsStaticFieldInitializerExecutionOutsideAtomicCallback()
    {
        var tree = CSharpSyntaxTree.ParseText(BoundaryFixture("""
            await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct));
            return await Escape.Pending;
            """) + """
            class Escape {
                static KernelWrite<object, int, int, int> write = null;
                static IWritePipelineObserver observer = null;
                public static readonly Task<int> Pending = WritePipeline.RunAsync(write, observer, default).AsTask();
                // An explicit type initializer makes the first static field read the initialization trigger.
                static Escape() { }
            }
            """);
        var declaration = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(node => node.Identifier.ValueText == "MergeAsync");
        var model = BoundaryModel(tree);
        Assert.DoesNotContain(model.Compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.False(RunsExecutorInsideAtomicCallback(declaration, model));
    }

    [Theory]
    [InlineData("await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct)); return await escaped.Pending;")]
    [InlineData("await using var scope = lease; return await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct));")]
    [InlineData("using var scope = lease; return await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct));")]
    public void ExemptionRejectsDynamicAccessAndImplicitDisposal(string body)
    {
        var source = BoundaryFixture(body).Replace("IWritePipelineObserver observer)",
            "IWritePipelineObserver observer, dynamic escaped, Escape lease)", StringComparison.Ordinal) + """
            class Escape : IAsyncDisposable, IDisposable {
                KernelWrite<object, int, int, int> write;
                IWritePipelineObserver observer;
                public Task<int> Pending => WritePipeline.RunAsync(write, observer, default).AsTask();
                public async ValueTask DisposeAsync() { await WritePipeline.RunAsync(write, observer, default); }
                public void Dispose() { WritePipeline.RunAsync(write, observer, default).AsTask().GetAwaiter().GetResult(); }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(source);
        var declaration = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(node => node.Identifier.ValueText == "MergeAsync");
        var model = BoundaryModel(tree);
        Assert.DoesNotContain(model.Compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.False(RunsExecutorInsideAtomicCallback(declaration, model));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExemptionRejectsConditionalShippingAndDecoyBranches(bool productionSymbols)
    {
        var source = BoundaryFixture("""
            #if NET11_0
            await unit.ExecuteAtomicAsync(ct => Done());
            return await WritePipeline.RunAsync(write, observer, default);
            #else
            return await unit.ExecuteAtomicAsync(async ct => await WritePipeline.RunAsync(write, observer, ct));
            #endif
            """);
        var options = productionSymbols ? new CSharpParseOptions(preprocessorSymbols: ["NET11_0"]) : CSharpParseOptions.Default;
        var tree = CSharpSyntaxTree.ParseText(source, options);
        var declaration = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(node => node.Identifier.ValueText == "MergeAsync");
        var model = BoundaryModel(tree);
        Assert.DoesNotContain(model.Compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.False(RunsExecutorInsideAtomicCallback(declaration, model));
    }

    [Theory]
    // A callback that completes one executor must not start another through an opaque operation.
    [InlineData("Fire(write, observer, ct); return await WritePipeline.RunAsync(write, observer, ct);")]
    [InlineData("return await WritePipeline.RunAsync(FireAndReturn(write, observer, ct), observer, ct);")]
    [InlineData("return await WritePipeline.RunAsync(Leaked, observer, ct);")]
    [InlineData("return await WritePipeline.RunAsync(new OpaqueWriter(write, observer).Write, observer, ct);")]
    [InlineData("using var scope = new OpaqueWriter(write, observer); return await WritePipeline.RunAsync(write, observer, ct);")]
    [InlineData("return await WritePipeline.RunAsync<object, int, int, int>(new ConvertedWriter(write, observer), observer, ct);")]
    public void ExemptionRejectsOpaqueEffectsInsideAtomicCallback(string body)
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Harborline.Api.Foundation.Assets.Hierarchy;
            using Harborline.Api.Kernel.Runtime;
            class Fixture {
                KernelWrite<object, int, int, int> saved;
                IWritePipelineObserver observed;
                void Fire(KernelWrite<object, int, int, int> write, IWritePipelineObserver observer, CancellationToken ct) {
                    _ = WritePipeline.RunAsync(write, observer, ct);
                }
                KernelWrite<object, int, int, int> FireAndReturn(KernelWrite<object, int, int, int> write,
                    IWritePipelineObserver observer, CancellationToken ct) {
                    Fire(write, observer, ct); return write;
                }
                KernelWrite<object, int, int, int> Leaked { get { Fire(saved, observed, default); return saved; } }
                sealed class OpaqueWriter : IDisposable {
                    public KernelWrite<object, int, int, int> Write;
                    IWritePipelineObserver observer;
                    public OpaqueWriter(KernelWrite<object, int, int, int> write, IWritePipelineObserver observer) {
                        Write = write; this.observer = observer; _ = WritePipeline.RunAsync(write, observer, default);
                    }
                    public void Dispose() { _ = WritePipeline.RunAsync(Write, observer, default); }
                }
                sealed class ConvertedWriter {
                    KernelWrite<object, int, int, int> write;
                    IWritePipelineObserver observer;
                    public ConvertedWriter(KernelWrite<object, int, int, int> write, IWritePipelineObserver observer) {
                        this.write = write; this.observer = observer;
                    }
                    public static implicit operator KernelWrite<object, int, int, int>(ConvertedWriter value) {
                        _ = WritePipeline.RunAsync(value.write, value.observer, default); return value.write;
                    }
                }
                async Task<int> MergeAsync(IHierarchyCompositeUnitOfWork unit,
                    KernelWrite<object, int, int, int> write, IWritePipelineObserver observer) {
                    return await unit.ExecuteAtomicAsync(async ct => {
            """ + body + " }); } }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var declaration = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(node => node.Identifier.ValueText == "MergeAsync");
        var model = BoundaryModel(tree);
        Assert.DoesNotContain(model.Compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.False(RunsExecutorInsideAtomicCallback(declaration, model));
    }

    [Theory]
    [InlineData("/_/apps/local-node-host/tests/ArchTests/WritePipelineExecutorFenceTests.cs")]
    [InlineData("\\_\\apps\\local-node-host\\tests\\ArchTests\\WritePipelineExecutorFenceTests.cs")]
    public void RepositoryLookupIgnoresMappedCompileTimePaths(string mappedCallerPath)
    {
        var root = RepositoryRoot(mappedCallerPath);
        Assert.True(File.Exists(Path.Combine(root, "Harborline.Api.slnx")));
        Assert.True(File.Exists(Path.Combine(root, "apps/local-node-host/Data/Entities/NodeHierarchyCompositeCoordinator.cs")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExemptionRequiresTheCanonicalOverloadOnTheGenuineExecutorType(bool alternateOverload)
    {
        // Compile the actual kernel source with its real assembly identity and one extra overload.
        // The negative is on the genuine class, not a lookalike class or helper outside the callback.
        var kernelSource = File.ReadAllText(Path.Combine(RepositoryRoot(), "packages/kernel-runtime/WritePipelineStage.cs"))
            .Replace("    private static void Enter(",
                "    public static Task<TResult> RunAsync<TBound, TMutation, TSealed, TResult>(KernelWrite<TBound, TMutation, TSealed, TResult> write, IWritePipelineObserver observer, CancellationToken ct, bool decoy) where TBound : class => Task.FromResult(default(TResult)!);\n"
                + "    private static void Enter(", StringComparison.Ordinal);
        var kernelTree = CSharpSyntaxTree.ParseText(kernelSource);
        var references = BoundaryModel(kernelTree).Compilation.References
            .Where(reference => reference.Display != typeof(WritePipeline).Assembly.Location);
        var globals = CSharpSyntaxTree.ParseText("global using System; global using System.Collections.Generic; "
            + "global using System.Linq; global using System.Threading; global using System.Threading.Tasks;");
        var identity = typeof(WritePipeline).Assembly.GetName();
        var version = CSharpSyntaxTree.ParseText($"[assembly: System.Reflection.AssemblyVersion(\"{identity.Version}\")]");
        var compilation = CSharpCompilation.Create(identity.Name!, [kernelTree, globals, version], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var kernelReference = MetadataReference.CreateFromImage(image.ToArray());
        var call = alternateOverload ? "WritePipeline.RunAsync(write, observer, ct, false)" : "WritePipeline.RunAsync(write, observer, ct)";
        var tree = CSharpSyntaxTree.ParseText(BoundaryFixture(
            "return await unit.ExecuteAtomicAsync(async ct => await " + call + ");"));
        var model = BoundaryModel(tree, kernelReference);
        Assert.DoesNotContain(model.Compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var declaration = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(node => node.Identifier.ValueText == "MergeAsync");
        Assert.Equal(!alternateOverload, RunsExecutorInsideAtomicCallback(declaration, model));
    }

    [Theory]
    [InlineData("constructor")]
    [InlineData("instance initializer")]
    [InlineData("static initializer")]
    public void ExemptionRejectsDeferredExecutorEffectsInTheReviewedWriterConstruction(string effect)
    {
        const string parameters = "NodeHierarchyCompositeCoordinator coordinator, IReadOnlyList<EntityId> oldEntities, "
            + "SchemaId newSchema, JsonDocument newBody, CreateOptions newOptions, string justification, "
            + "ActorId actor, TenantId tenant, DateTimeOffset at";
        const string bases = "KernelWrite<IReadOnlyList<EntityEdge>, CreateOptions, ValidatedRecordBody, MergeResult>";
        const string stages = """
            protected override ValueTask AuthorizeAsync(CancellationToken ct) => ValueTask.CompletedTask;
            protected override ValueTask<IReadOnlyList<EntityEdge>?> BindAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<EntityEdge>?>([]);
            protected override ValueTask<CreateOptions> MutateAsync(IReadOnlyList<EntityEdge> bound, CancellationToken ct) => ValueTask.FromResult<CreateOptions>(default!);
            protected override ValueTask<ValidatedRecordBody> ValidateAsync(IReadOnlyList<EntityEdge> bound, CreateOptions mutation, CancellationToken ct) => ValueTask.FromResult<ValidatedRecordBody>(default!);
            protected override ValueTask CommitAsync(ValidatedRecordBody validated, CancellationToken ct) => ValueTask.CompletedTask;
            protected override ValueTask<MergeResult> ReactAsync(ValidatedRecordBody validated, CancellationToken ct) => ValueTask.FromResult<MergeResult>(default!);
            """;
        var construction = effect == "constructor"
            ? "private sealed class Merge : " + bases + " { public Merge(" + parameters + ") { _ = Leak(); }"
            : "private sealed class Merge(" + parameters + ") : " + bases + " { "
                + "private readonly EntityId expectedNewId = InMemoryEntityStore.DeriveEntityId(newSchema, newOptions); "
                + "private CompositeAuthorization authorization = null!; private IReadOnlyList<EntityEdge> displaced = []; "
                + "private MergeResult result = null!; private "
                + (effect == "static initializer" ? "static readonly" : "readonly") + " Task leaked = Leak();";
        var source = """
            using System;
            using System.Collections.Generic;
            using System.Text.Json;
            using System.Threading;
            using System.Threading.Tasks;
            using Harborline.Api.Foundation.Assets.Common;
            using Harborline.Api.Foundation.Assets.Entities;
            using Harborline.Api.Foundation.Assets.Hierarchy;
            using Harborline.Api.Kernel.Runtime;
            namespace Harborline.Api.LocalNodeHost.Data.Entities;
            class NodeHierarchyCompositeCoordinator {
                private sealed class CompositeAuthorization { }
                private static Task Leak() => Task.Run(async () => {
                    await Task.Delay(10);
                    await WritePipeline.RunAsync(new DetachedWrite(), null, default);
                });
                async Task<MergeResult> MergeAsync(IHierarchyCompositeUnitOfWork unit, IReadOnlyList<EntityId> oldEntities,
                    SchemaId newSchema, JsonDocument newBody, CreateOptions newOptions, string justification,
                    ActorId actor, TenantId tenant, DateTimeOffset at, IWritePipelineObserver observer) {
                    return (await unit.ExecuteAtomicAsync(async ct => (await WritePipeline.RunAsync(
                        new Merge(this, oldEntities, newSchema, newBody, newOptions, justification, actor, tenant, at), observer, ct))!))!;
                }
            """ + construction + stages + " } private sealed class DetachedWrite : " + bases + " { " + stages + " } }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var model = BoundaryModel(tree);
        Assert.DoesNotContain(model.Compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var declaration = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(node => node.Identifier.ValueText == "MergeAsync");
        Assert.False(RunsExecutorInsideAtomicCallback(declaration, model));
    }

    private static string BoundaryFixture(string body) => """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Harborline.Api.Foundation.Assets.Hierarchy;
        using Harborline.Api.Kernel.Runtime;
        class Fixture {
            Task<int> Done() => Task.FromResult(0);
            bool Opaque(object value) => value.GetHashCode() == 0;
            async Task<int> MergeAsync(IHierarchyCompositeUnitOfWork unit,
                KernelWrite<object, int, int, int> write, IWritePipelineObserver observer) {
        """ + "\n" + body + "\n} }";

    [Theory]
    [InlineData("conditional constructor")]
    [InlineData("second partial declaration")]
    public void ReviewedKernelConstructionRejectsConditionalOrPartialBase(string effect)
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "packages/kernel-runtime/WritePipelineStage.cs"));
        var parsed = CSharpSyntaxTree.ParseText(source);
        var declaration = parsed.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(type => type.Identifier.ValueText == "KernelWrite");
        const string constructor = "protected KernelWrite() { _ = Task.Run(async () => { await Task.Delay(1); await WritePipeline.RunAsync(this, null, default); }); }";
        var options = new CSharpParseOptions(preprocessorSymbols: ["NET11_0"]);
        SyntaxTree[] trees;
        if (effect == "conditional constructor")
        {
            source = source.Insert(declaration.OpenBraceToken.Span.End, "\n#if NET11_0\n" + constructor + "\n#endif\n");
            trees = [CSharpSyntaxTree.ParseText(source, options, path: "WritePipelineStage.cs")];
        }
        else
        {
            source = source.Replace("public abstract class KernelWrite<", "public abstract partial class KernelWrite<", StringComparison.Ordinal);
            trees = [CSharpSyntaxTree.ParseText(source, options, path: "WritePipelineStage.cs"),
                CSharpSyntaxTree.ParseText("namespace Harborline.Api.Kernel.Runtime; public abstract partial class KernelWrite<TBound, TMutation, TSealed, TResult> where TBound : class { "
                    + constructor + " }", options, path: "HiddenConstruction.cs")];
        }
        var references = BoundaryModel(trees[0]).Compilation.References
            .Where(reference => reference.Display != typeof(WritePipeline).Assembly.Location);
        var globals = CSharpSyntaxTree.ParseText("global using System; global using System.Collections.Generic; global using System.Linq; global using System.Threading; global using System.Threading.Tasks;");
        var identity = typeof(WritePipeline).Assembly.GetName();
        var version = CSharpSyntaxTree.ParseText($"[assembly: System.Reflection.AssemblyVersion(\"{identity.Version}\")]");
        var compilation = CSharpCompilation.Create(identity.Name!, trees.Concat([globals, version]), references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        Assert.False(HasReviewedKernelBaseConstruction(trees));
    }

    [Fact]
    public void ReviewedCalculationRejectsConditionalConstructionDependencyCode()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "packages/foundation/Assets/Entities/InMemoryEntityStore.cs"));
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var calculation = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "DeriveEntityId");
        var method = calculation.ToFullString();
        method = method.Insert(method.IndexOf('{') + 1, "\n#if NET11_0\n_ = System.Threading.Tasks.Task.Run(() => System.Threading.Tasks.Task.Delay(1));\n#endif\n");
        source = "using System; using System.Security.Cryptography; using System.Text; using Harborline.Api.Foundation.Assets.Common; using Harborline.Api.Foundation.Assets.Entities; using Harborline.Api.Foundation.Definitions; "
            + "namespace Harborline.Api.Foundation.Assets.Entities { public sealed class InMemoryEntityStore { " + method + " } "
            + root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(type => type.Identifier.ValueText == "Base32Lower").ToFullString() + " }";
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(preprocessorSymbols: ["NET11_0"]));
        var compilation = BoundaryModel(tree).Compilation;
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        // The old default-symbol reparse hides the conditional side effect and accepts the literal body.
        Assert.False(HasReviewedCalculationSource(CSharpSyntaxTree.ParseText(source)));
    }

    [Theory]
    [InlineData("encoder effect")]
    [InlineData("partial calculation owner")]
    public void ReviewedCalculationRejectsUnreviewedHelperOrOwnerConstruction(string effect)
    {
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(RepositoryRoot(),
            "packages/foundation/Assets/Entities/InMemoryEntityStore.cs"))).GetRoot();
        var calculation = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "DeriveEntityId");
        var encoder = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(type => type.Identifier.ValueText == "Base32Lower").ToFullString();
        if (effect == "encoder effect")
        {
            var encode = CSharpSyntaxTree.ParseText(encoder).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
            encoder = encoder.Insert(encode.Body!.OpenBraceToken.Span.End,
                " _ = System.Threading.Tasks.Task.Run(() => System.Threading.Tasks.Task.Delay(1)); ");
        }
        var source = "using System; using System.Security.Cryptography; using System.Text; using Harborline.Api.Foundation.Assets.Common; using Harborline.Api.Foundation.Assets.Entities; using Harborline.Api.Foundation.Definitions; "
            + "namespace Harborline.Api.Foundation.Assets.Entities { public sealed "
            + (effect == "partial calculation owner" ? "partial " : "")
            + "class InMemoryEntityStore { " + calculation.ToFullString() + " } " + encoder + " }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = BoundaryModel(tree).Compilation;
        if (effect == "partial calculation owner")
            compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
                "namespace Harborline.Api.Foundation.Assets.Entities; public sealed partial class InMemoryEntityStore { static InMemoryEntityStore() { _ = System.Threading.Tasks.Task.Run(() => System.Threading.Tasks.Task.Delay(1)); } }"));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        Assert.False(HasReviewedCalculationSource(tree));
    }

    [Fact]
    public void ReviewedBaseRejectsAnEffectfulLinkedCompiledDeclarationBehindSafePhysicalSource()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "packages/kernel-runtime/WritePipelineStage.cs"));
        var safeTree = CSharpSyntaxTree.ParseText(source);
        var declaration = safeTree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(type => type.Identifier.ValueText == "KernelWrite");
        source = source.Insert(declaration.OpenBraceToken.Span.End,
            " protected KernelWrite() { _ = Task.Run(async () => { await Task.Delay(1); await WritePipeline.RunAsync(this, null, default); }); } ");
        var linkedTree = CSharpSyntaxTree.ParseText(source, path: "external/LinkedWritePipeline.cs");
        var references = BoundaryModel(linkedTree).Compilation.References
            .Where(reference => reference.Display != typeof(WritePipeline).Assembly.Location);
        var globals = CSharpSyntaxTree.ParseText("global using System; global using System.Collections.Generic; global using System.Linq; global using System.Threading; global using System.Threading.Tasks;");
        var identity = typeof(WritePipeline).Assembly.GetName();
        var compilation = CSharpCompilation.Create(identity.Name!, [linkedTree, globals], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var linked = Assembly.Load(image.ToArray()).GetType(typeof(KernelWrite<,,,>).FullName!)!;
        Assert.True(HasReviewedKernelBaseConstruction([safeTree]));
        Assert.False(HasReviewedCompiledBase(linked));
    }

    [Fact]
    public void CompiledSourceBindingRejectsAReplacementDocumentWithTheSamePhysicalPath()
    {
        const string file = "packages/foundation/Assets/Entities/InMemoryEntityStore.cs";
        var source = Microsoft.CodeAnalysis.Text.SourceText.From("public static class Replacement { public static int Value() => 42; }",
            System.Text.Encoding.UTF8, Microsoft.CodeAnalysis.Text.SourceHashAlgorithm.Sha256);
        var tree = CSharpSyntaxTree.ParseText(source, path: Path.Combine(RepositoryRoot(), file));
        var compilation = BoundaryModel(tree).Compilation;
        using var image = new MemoryStream();
        using var symbols = new MemoryStream();
        var emitted = compilation.Emit(image, symbols, options: new Microsoft.CodeAnalysis.Emit.EmitOptions(
            debugInformationFormat: Microsoft.CodeAnalysis.Emit.DebugInformationFormat.PortablePdb));
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        image.Position = 0;
        symbols.Position = 0;
        using var pe = new PEReader(image);
        var metadata = pe.GetMetadataReader();
        var method = metadata.MethodDefinitions.Single(handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name) == "Value");
        using var provider = MetadataReaderProvider.FromPortablePdbStream(symbols);
        var reader = provider.GetMetadataReader();
        var document = reader.GetDocument(reader.GetMethodDebugInformation(method).Document);
        Assert.Equal(file, Audit.AuditAppendSymbolInventory.NormalizeFile(reader.GetString(document.Name)));
        Assert.False(HasMatchingSourceDocument(reader, method, file));
    }

    [Theory]
    [InlineData("SHA256")]
    [InlineData("Encoding property")]
    [InlineData("Encoding field")]
    [InlineData("ArgumentException")]
    [InlineData("StringComparison field")]
    [InlineData("LINQ extension")]
    public void CalculationBindingsRejectSameNamespaceFrameworkShadows(string shadow)
    {
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(RepositoryRoot(),
            "packages/foundation/Assets/Entities/InMemoryEntityStore.cs"))).GetRoot();
        var calculation = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == "DeriveEntityId");
        var encoder = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(type => type.Identifier.ValueText == "Base32Lower");
        var source = "using System; using System.Security.Cryptography; using System.Text; using Harborline.Api.Foundation.Assets.Common; using Harborline.Api.Foundation.Definitions; namespace Harborline.Api.Foundation.Assets.Entities { public sealed class InMemoryEntityStore { "
            + calculation.ToFullString() + " } " + encoder.ToFullString() + " }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var shadowSource = shadow switch
        {
            "SHA256" => "public static class SHA256 { public static int HashData(ReadOnlySpan<byte> source, Span<byte> destination) { Queue(); return System.Security.Cryptography.SHA256.HashData(source, destination); } private static void Queue() { _ = System.Threading.Tasks.Task.Run(() => System.Threading.Tasks.Task.Delay(1)); } }",
            "Encoding property" => "public static class Encoding { public static System.Text.Encoding UTF8 { get { _ = System.Threading.Tasks.Task.Run(() => System.Threading.Tasks.Task.Delay(1)); return System.Text.Encoding.UTF8; } } }",
            "Encoding field" => "public static class Encoding { public static readonly System.Text.Encoding UTF8 = Queue(); private static System.Text.Encoding Queue() { _ = System.Threading.Tasks.Task.Run(() => System.Threading.Tasks.Task.Delay(1)); return System.Text.Encoding.UTF8; } }",
            "ArgumentException" => "public sealed class ArgumentException : System.ArgumentException { public ArgumentException(string message, string parameter) : base(message, parameter) { _ = System.Threading.Tasks.Task.Run(() => System.Threading.Tasks.Task.Delay(1)); } }",
            "StringComparison field" => "public static class StringComparison { public static readonly System.StringComparison Ordinal = Queue(); private static System.StringComparison Queue() { _ = System.Threading.Tasks.Task.Run(() => System.Threading.Tasks.Task.Delay(1)); return System.StringComparison.Ordinal; } }",
            "LINQ extension" => "public static class ShadowExtensions { public static bool All(this string value, Func<char, bool> predicate) { _ = System.Threading.Tasks.Task.Run(() => System.Threading.Tasks.Task.Delay(1)); return System.Linq.Enumerable.All(value, predicate); } }",
            _ => throw new ArgumentOutOfRangeException(nameof(shadow)),
        };
        var compilation = BoundaryModel(tree).Compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            "using System; namespace Harborline.Api.Foundation.Assets.Entities; " + shadowSource));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var assembly = Assembly.Load(image.ToArray());
        var owner = assembly.GetType(typeof(InMemoryEntityStore).FullName!)!;
        var encoded = assembly.GetType("Harborline.Api.Foundation.Assets.Entities.Base32Lower")!;
        Assert.True(HasReviewedCalculationSource(tree));
        Assert.Null(owner.TypeInitializer);
        Assert.Null(encoded.TypeInitializer);
        Assert.False(HasReviewedCalculationBindings(owner.GetMethod("DeriveEntityId")!, encoded.GetMethod("Encode")!),
            string.Join("\n", RawMutationPortSymbolInventoryTests.CalledMethods(typeof(InMemoryEntityStore).GetMethod("DeriveEntityId")!)
                .Concat(RawMutationPortSymbolInventoryTests.CalledMethods(typeof(InMemoryEntityStore).Assembly.GetType("Harborline.Api.Foundation.Assets.Entities.Base32Lower")!.GetMethod("Encode")!))
                .Select(call => call.Target.DeclaringType + ": " + call.Target)));
    }

    [Fact]
    public void CompileInputBindingRejectsForgedPdbDocumentClaimsForAnExcludedDecoy()
    {
        const string expectedFile = "packages/foundation/Assets/Entities/InMemoryEntityStore.cs";
        var expectedPath = Path.Combine(RepositoryRoot(), expectedFile);
        var expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(expectedPath)));
        var source = $"#pragma checksum \"{expectedPath.Replace('\\', '/')}\" \"{{8829d00f-11b8-4213-878b-770e8597ac16}}\" \"{expectedHash}\"\n#line 1 \"{expectedPath.Replace('\\', '/')}\"\n"
            + "public static class LinkedReplacement { public static int Value() { _ = System.Threading.Tasks.Task.Run(() => System.Threading.Tasks.Task.Delay(1)); return 42; } }";
        var linkedPath = Path.Combine(RepositoryRoot(), "external/LinkedReplacement.cs");
        var tree = CSharpSyntaxTree.ParseText(Microsoft.CodeAnalysis.Text.SourceText.From(source,
            System.Text.Encoding.UTF8, Microsoft.CodeAnalysis.Text.SourceHashAlgorithm.Sha256), path: linkedPath);
        var compilation = BoundaryModel(tree).Compilation;
        using var image = new MemoryStream();
        using var symbols = new MemoryStream();
        var emitted = compilation.Emit(image, symbols, options: new Microsoft.CodeAnalysis.Emit.EmitOptions(
            debugInformationFormat: Microsoft.CodeAnalysis.Emit.DebugInformationFormat.PortablePdb));
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var assemblyHash = Convert.ToHexString(SHA256.HashData(image.ToArray()));
        image.Position = 0;
        symbols.Position = 0;
        using var pe = new PEReader(image);
        var metadata = pe.GetMetadataReader();
        var method = metadata.MethodDefinitions.Single(handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name) == "Value");
        using var provider = MetadataReaderProvider.FromPortablePdbStream(symbols);
        // Compiler-supported document directives fool the former PDB-only source association.
        Assert.True(HasMatchingSourceDocument(provider.GetMetadataReader(), method, expectedFile));
        string[] recordedInputs = [$"assembly|{assemblyHash}",
            $"source|{linkedPath}|{Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)))}"];
        Assert.False(HasRecordedCompileInput(recordedInputs, assemblyHash, expectedFile));
    }

    [Fact]
    public async Task ReviewedMergeRejectsADeferredRealExecutorFromReact()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), HostHierarchySource));
        source = source.Replace("protected override ValueTask<MergeResult> ReactAsync(ValidatedRecordBody validated, CancellationToken ct) =>\r\n            ValueTask.FromResult(result);",
            DeferredReactSource).Replace("protected override ValueTask<MergeResult> ReactAsync(ValidatedRecordBody validated, CancellationToken ct) =>\n            ValueTask.FromResult(result);", DeferredReactSource);
        var tree = CSharpSyntaxTree.ParseText(source + DeferredWitnessSource);
        var model = BoundaryModel(tree);
        using var image = new MemoryStream();
        var emitted = model.Compilation.Emit(image);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var assembly = Assembly.Load(image.ToArray());
        var writer = assembly.GetType(typeof(NodeHierarchyCompositeCoordinator).FullName + "+Merge")!;
        var boundary = assembly.GetType("Harborline.Api.LocalNodeHost.Data.Entities.DeferredBoundaryWitness")!;
        var instance = RuntimeHelpers.GetUninitializedObject(writer);
        writer.GetMethod("ReactAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, [null, CancellationToken.None]);
        boundary.GetField("Inside")!.SetValue(null, false);
        ((TaskCompletionSource)boundary.GetField("Release")!.GetValue(null)!).SetResult();
        await ((TaskCompletionSource)boundary.GetField("Finished")!.GetValue(null)!).Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, (int)boundary.GetField("Outside")!.GetValue(null)!);
        var declaration = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == "MergeAsync");
        Assert.False(RunsExecutorInsideAtomicCallback(declaration, model));
    }

    [Fact]
    public async Task HostCompilerProofRejectsALinkedReplacementBehindAReviewedPhysicalDecoy()
    {
        var physicalPath = Path.Combine(RepositoryRoot(), HostHierarchySource);
        var physical = File.ReadAllText(physicalPath);
        var safe = CSharpSyntaxTree.ParseText(physical);
        var merge = safe.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == "MergeAsync");
        Assert.True(RunsExecutorInsideAtomicCallback(merge, BoundaryModel(safe)));
        var replacementBody = merge.Body!.Statements.Take(merge.Body.Statements.Count - 1)
            .Append(SyntaxFactory.ParseStatement("if (oldEntities.Count < 0) { " + merge.Body.Statements[^1] + " }"))
            .Append(SyntaxFactory.ParseStatement("return (await WritePipeline.RunAsync(new Merge(this, oldEntities, newSchema, newBody, newOptions, justification, actor, tenant, at), pipelineObserver, ct).ConfigureAwait(false))!;"));
        var replacement = safe.GetRoot().ReplaceNode(merge, merge.WithBody(SyntaxFactory.Block(replacementBody))).ToFullString();
        var mappedPath = physicalPath.Replace('\\', '/');
        var physicalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(physicalPath)));
        var source = $"#pragma checksum \"{mappedPath}\" \"{{8829d00f-11b8-4213-878b-770e8597ac16}}\" \"{physicalHash}\"\n#line 1 \"{mappedPath}\"\n" + replacement;
        var linkedPath = Path.Combine(RepositoryRoot(), "external/LinkedHostHierarchy.cs");
        var tree = CSharpSyntaxTree.ParseText(Microsoft.CodeAnalysis.Text.SourceText.From(source,
            System.Text.Encoding.UTF8, Microsoft.CodeAnalysis.Text.SourceHashAlgorithm.Sha256), path: linkedPath);
        var compilation = BoundaryModel(tree).Compilation.WithAssemblyName(typeof(NodeHierarchyCompositeCoordinator).Assembly.GetName().Name!);
        using var image = new MemoryStream();
        using var symbols = new MemoryStream();
        var emitted = compilation.Emit(image, symbols, options: new Microsoft.CodeAnalysis.Emit.EmitOptions(
            debugInformationFormat: Microsoft.CodeAnalysis.Emit.DebugInformationFormat.PortablePdb));
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var assembly = Assembly.Load(image.ToArray());
        var method = assembly.GetType(typeof(NodeHierarchyCompositeCoordinator).FullName!)!.GetMethod("MergeAsync")!;
        // The linked caller enters the real executor while no atomic callback is open.
        // Its null unit proves ExecuteAtomicAsync was not entered on this path; an observer
        // records the first real stage before the deliberately uninitialized service seam refuses.
        var instance = RuntimeHelpers.GetUninitializedObject(method.DeclaringType!);
        var fields = method.DeclaringType!.GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        fields.Single(field => field.FieldType == typeof(TimeProvider)).SetValue(instance, TimeProvider.System);
        var observer = new OutsideUnitObserver();
        fields.Single(field => field.FieldType == typeof(IWritePipelineObserver)).SetValue(instance, observer);
        var actor = new Harborline.Api.Foundation.Assets.Common.ActorId("boundary-witness");
        var tenant = new TenantId("boundary-witness");
        using var body = System.Text.Json.JsonDocument.Parse("{}");
        var run = (Task<MergeResult>)method.Invoke(instance, [Array.Empty<EntityId>(),
            new Harborline.Api.Foundation.Assets.Common.SchemaId("boundary.witness"), body,
            new CreateOptions("entity", "test", "boundary-witness", actor, tenant, ExplicitLocalPart: "boundary-witness"),
            "boundary-witness", actor, tenant, DateTimeOffset.UnixEpoch, CancellationToken.None])!;
        await Assert.ThrowsAsync<NullReferenceException>(() => run);
        Assert.Equal(1, observer.EnteredStages);
        var moveNext = method.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var calls = RawMutationPortSymbolInventoryTests.CalledMethods(moveNext).ToArray();
        Assert.Contains(calls, call => call.Target.Name == "ExecuteAtomicAsync" && call.Target.DeclaringType == typeof(IHierarchyCompositeUnitOfWork));
        Assert.Contains(calls, call => call.Target.Name == "RunAsync" && call.Target.DeclaringType == typeof(WritePipeline));
        symbols.Position = 0;
        using var provider = MetadataReaderProvider.FromPortablePdbStream(symbols, MetadataStreamOptions.LeaveOpen);
        Assert.True(HasMatchingSourceDocument(provider.GetMetadataReader(), (MethodDefinitionHandle)MetadataTokens.Handle(moveNext.MetadataToken), HostHierarchySource));
        var directory = Path.Combine(Path.GetTempPath(), "host-proof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var output = Path.GetDirectoryName(typeof(InMemoryEntityStore).Assembly.Location)!;
            foreach (var name in new[] { "Harborline.Api.Foundation", "Harborline.Api.Kernel.Runtime", "Harborline.Api.LocalNodeHost" })
                foreach (var extension in new[] { ".compile-image", ".compile-symbols", ".compile-inputs.txt" })
                    File.Copy(Path.Combine(output, name + extension), Path.Combine(directory, name + extension));
            var hostPath = Path.Combine(directory, assembly.GetName().Name + ".compile-image");
            File.WriteAllBytes(hostPath, image.ToArray());
            File.WriteAllBytes(Path.ChangeExtension(hostPath, ".compile-symbols"), symbols.ToArray());
            File.WriteAllLines(Path.ChangeExtension(hostPath, ".compile-inputs.txt"),
                ["assembly|" + Convert.ToHexString(SHA256.HashData(image.ToArray())),
                 "source|" + linkedPath + "|" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)))]);
            Assert.False(HasReviewedConstructionArtifacts(directory, assembly));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class OutsideUnitObserver : IWritePipelineObserver
    {
        internal int EnteredStages { get; private set; }
        public void OnStage(WritePipelineStage stage)
        {
            Assert.Equal(WritePipelineStage.Authorize, stage);
            EnteredStages++;
        }
    }

    [Theory]
    [InlineData("getter")]
    [InlineData("helper")]
    public void ReviewedMergeRejectsDeferredEffectsInItsLocalHelperClosure(string change)
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), HostHierarchySource)).Replace("\r\n", "\n");
        source = change == "getter"
            ? source.Replace("private IHierarchyCompositeUnitOfWork Store => unitOfWork;",
                "private IHierarchyCompositeUnitOfWork Store { get { _ = Task.Run(() => Task.Delay(1)); return unitOfWork; } }")
            : source.Replace("var edges = new List<EntityEdge>();\n        foreach (var parent in parents.Distinct())\n        await foreach (var edge in unitOfWork.GetChildrenNotEndedAsync",
                "_ = Task.Run(() => Task.Delay(1));\n        var edges = new List<EntityEdge>();\n        foreach (var parent in parents.Distinct())\n        await foreach (var edge in unitOfWork.GetChildrenNotEndedAsync");
        var tree = CSharpSyntaxTree.ParseText(source);
        var model = BoundaryModel(tree);
        using var image = new MemoryStream();
        var emitted = model.Compilation.Emit(image);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var merge = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Single(type => type.Identifier.ValueText == "Merge");
        Assert.False(HasReviewedMergeOwnerClosure((INamedTypeSymbol)model.GetDeclaredSymbol(merge)!));
    }

    [Fact]
    public void ReviewedMergeRejectsLinkedDependencyTypesWithUnchangedStageTokens()
    {
        var physical = File.ReadAllText(Path.Combine(RepositoryRoot(), HostHierarchySource));
        var tree = CSharpSyntaxTree.ParseText(physical);
        var linked = CSharpSyntaxTree.ParseText("""
            using System;
            using System.Collections.Generic;
            using System.Text.Json;
            using Harborline.Api.Foundation.Assets.Entities;
            using System.Threading;
            using System.Threading.Tasks;
            using Harborline.Api.Foundation.Assets.Common;
            using Harborline.Api.Foundation.Assets.Hierarchy;
            namespace Harborline.Api.LocalNodeHost.Data.Entities;
            public sealed record MergeResult(EntityId NewId, IReadOnlyList<EntityId> OldIds, IReadOnlyList<EntityId> Reassigned)
            {
                private readonly Task deferred = Task.Run(() => Task.Delay(1));
            }
            public interface IHierarchyCompositeCoordinator
            {
                Task<SplitResult> SplitAsync(
                    EntityId oldEntity,
                    IReadOnlyList<SplitTarget> newEntities,
                    IReadOnlyDictionary<EntityId, EntityId> childReassignments,
                    string justification,
                    ActorId actor,
                    TenantId tenant,
                    DateTimeOffset effectiveAt,
                    CancellationToken ct = default);

                Task<MergeResult> MergeAsync(
                    IReadOnlyList<EntityId> oldEntities,
                    SchemaId newSchema,
                    JsonDocument newBody,
                    CreateOptions newOptions,
                    string justification,
                    ActorId actor,
                    TenantId tenant,
                    DateTimeOffset effectiveAt,
                    CancellationToken ct = default);

                Task ReparentAsync(
                    EntityId child,
                    EntityId oldParent,
                    EntityId newParent,
                    string justification,
                    ActorId actor,
                    TenantId tenant,
                    DateTimeOffset effectiveAt,
                    CancellationToken ct = default);
            }

            """);
        var compilation = BoundaryModel(tree).Compilation.AddSyntaxTrees(linked);
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == "MergeAsync");
        Assert.False(RunsExecutorInsideAtomicCallback(method, compilation.GetSemanticModel(tree)));
    }

    private const string HostHierarchySource = "apps/local-node-host/Data/Entities/NodeHierarchyCompositeCoordinator.cs";
    private const string DeferredReactSource = """
        protected override ValueTask<MergeResult> ReactAsync(ValidatedRecordBody validated, CancellationToken ct)
        {
            _ = Task.Run(async () => {
                await DeferredBoundaryWitness.Release.Task;
                try { await WritePipeline.RunAsync(this, new DeferredBoundaryWitness.Observer(), default); }
                catch (NullReferenceException) { /* Uninitialized fixture reaches the real executor before the service seam. */ }
                finally { DeferredBoundaryWitness.Finished.TrySetResult(); }
            });
            return ValueTask.FromResult(result);
        }
        """;
    private const string DeferredWitnessSource = """
        public static class DeferredBoundaryWitness
        {
            public static bool Inside = true;
            public static int Outside;
            public static readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public static readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public sealed class Observer : IWritePipelineObserver
            {
                public void OnStage(WritePipelineStage stage)
                {
                    if (!Inside) Interlocked.Increment(ref Outside);
                }
            }
        }
        """;

    private static SemanticModel BoundaryModel(SyntaxTree tree, MetadataReference? kernelReference = null)
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            .Select(assembly => assembly.Location)
            .Concat(new[] { typeof(DynamicAttribute).Assembly.Location,
                typeof(Microsoft.CSharp.RuntimeBinder.Binder).Assembly.Location,
                typeof(System.Linq.Expressions.Expression).Assembly.Location })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(location => kernelReference is null || location != typeof(WritePipeline).Assembly.Location)
            .Select(location => (MetadataReference)MetadataReference.CreateFromFile(location))
            .Concat(kernelReference is null ? Array.Empty<MetadataReference>() : new[] { kernelReference });
        // The host relies on implicit global usings; include those when binding its standalone source file.
        var globals = CSharpSyntaxTree.ParseText("global using System; global using System.Collections.Generic; "
            + "global using System.Linq; global using System.Threading; global using System.Threading.Tasks; "
            + "global using TenantId = Harborline.Foundation.Assets.Common.TenantId;");
        return CSharpCompilation.Create("HierarchyAtomicBoundaryProbe", [tree, globals], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).GetSemanticModel(tree);
    }

    private static bool IsReviewedAtomicCall(InvocationExpressionSyntax call, SemanticModel model) =>
        model.GetSymbolInfo(call).Symbol is IMethodSymbol symbol
        && symbol.Name == nameof(IHierarchyCompositeUnitOfWork.ExecuteAtomicAsync)
        && symbol.ContainingType.ToDisplayString() == typeof(IHierarchyCompositeUnitOfWork).FullName
        && symbol.ContainingAssembly.Identity.ToString() == typeof(IHierarchyCompositeUnitOfWork).Assembly.FullName;

    private static bool IsExecutorCall(InvocationExpressionSyntax call, SemanticModel model)
    {
        var executor = model.Compilation.GetTypeByMetadataName(typeof(WritePipeline).FullName!);
        if (executor is null || executor.ContainingAssembly.Identity.ToString() != typeof(WritePipeline).Assembly.FullName
            || model.GetSymbolInfo(call).Symbol is not IMethodSymbol actual) return false;
        // Bind the original generic definition with the ADR-0038 API contract, not every future
        // method named RunAsync on the same genuine type. Constructed generic calls share this symbol.
        var expected = executor.GetMembers(nameof(WritePipeline.RunAsync)).OfType<IMethodSymbol>()
            .SingleOrDefault(candidate => HasCanonicalExecutorContract(candidate, model));
        return expected is not null && SymbolEqualityComparer.Default.Equals(actual.OriginalDefinition, expected);
    }

    private static bool HasCanonicalExecutorContract(IMethodSymbol method, SemanticModel model)
    {
        if (method is not { IsStatic: true, MethodKind: MethodKind.Ordinary, Arity: 4, Parameters.Length: 3 }
            || method.ReturnsByRef || method.ReturnsByRefReadonly || method.IsExtensionMethod
            || method.Parameters.Any(parameter => parameter.RefKind != RefKind.None || parameter.IsParams)
            || !method.TypeParameters[0].HasReferenceTypeConstraint) return false;
        return method.Parameters[0].Type is INamedTypeSymbol write
            && SymbolEqualityComparer.Default.Equals(write.OriginalDefinition,
                model.Compilation.GetTypeByMetadataName(typeof(KernelWrite<,,,>).FullName!))
            && write.TypeArguments.Length == 4
            && write.TypeArguments.Select((argument, index) =>
                SymbolEqualityComparer.Default.Equals(argument, method.TypeParameters[index])).All(equal => equal)
            && SymbolEqualityComparer.Default.Equals(method.Parameters[1].Type,
                model.Compilation.GetTypeByMetadataName(typeof(IWritePipelineObserver).FullName!))
            && SymbolEqualityComparer.Default.Equals(method.Parameters[2].Type,
                model.Compilation.GetTypeByMetadataName(typeof(CancellationToken).FullName!))
            && method.ReturnType is INamedTypeSymbol result
            && SymbolEqualityComparer.Default.Equals(result.OriginalDefinition,
                model.Compilation.GetTypeByMetadataName(typeof(ValueTask<>).FullName!))
            && SymbolEqualityComparer.Default.Equals(result.TypeArguments.Single(), method.TypeParameters[3]);
    }

    private static bool IsReviewedPreludeCall(InvocationExpressionSyntax call, SemanticModel model) =>
        model.GetSymbolInfo(call).Symbol is IMethodSymbol symbol
        && (symbol.Name == nameof(ArgumentNullException.ThrowIfNull)
            && symbol.ContainingType.ToDisplayString() == typeof(ArgumentNullException).FullName
            && symbol.ContainingAssembly.Identity.ToString() == typeof(ArgumentNullException).Assembly.FullName
            || symbol.Name == nameof(TimeProvider.GetUtcNow)
            && symbol.ContainingType.ToDisplayString() == typeof(TimeProvider).FullName
            && symbol.ContainingAssembly.Identity.ToString() == typeof(TimeProvider).Assembly.FullName
            || IsAdmittedClockRead(symbol));

    // T-1015: the act's one clock read, typed as the admitted decision instant.
    private static bool IsAdmittedClockRead(IMethodSymbol symbol) =>
        symbol.Name == nameof(AdmittedInstant.Read)
        && symbol.ContainingType.ToDisplayString() == typeof(AdmittedInstant).FullName
        && symbol.ContainingAssembly.Identity.ToString() == typeof(AdmittedInstant).Assembly.FullName;

    private static bool IsTaskWrapperCall(InvocationExpressionSyntax call, SemanticModel model) =>
        model.GetSymbolInfo(call).Symbol is IMethodSymbol symbol
        && symbol.Name is "ConfigureAwait" or "AsTask"
        && symbol.ContainingType.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks"
        && symbol.ContainingType.MetadataName is "Task`1" or "ValueTask`1"
        && symbol.ContainingAssembly.Identity.ToString() == typeof(Task).Assembly.FullName;

    private static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        // Caller paths can be mapped to /_/ or embedded on another build machine. The runtime
        // checkout, located above the test output, is the authority; ignore the compile-time path.
        return Audit.AuditAppendSymbolInventory.RepositoryRoot();
    }

    private static bool RunsExecutorInsideAtomicCallback(MethodDeclarationSyntax method, SemanticModel model)
    {
        // The standalone probe does not reconstruct MSBuild's preprocessor symbols. Refuse conditional
        // source rather than proving a different branch from the one compiled into the shipping assembly.
        if (method.SyntaxTree.GetRoot().DescendantTrivia(descendIntoTrivia: true).Any(trivia =>
            trivia.GetStructure() is IfDirectiveTriviaSyntax or ElifDirectiveTriviaSyntax
                or ElseDirectiveTriviaSyntax or EndIfDirectiveTriviaSyntax)) return false;
        // Indirect calls require interprocedural proof, which this narrow exemption does not provide.
        // A method-group alias to RunAsync is a delegate Invoke, not an executor invocation in the scan.
        if (method.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(call =>
            model.GetSymbolInfo(call).Symbol is not IMethodSymbol
            || model.GetSymbolInfo(call).Symbol is IMethodSymbol
                { MethodKind: MethodKind.DelegateInvoke or MethodKind.LocalFunction })) return false;
        var mutationCalls = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => IsMutationCall(call, model)).ToArray();
        if (mutationCalls.Length != 1 || !IsReviewedAtomicCall(mutationCalls[0], model)) return false;
        var atomicCall = mutationCalls[0];
        if (atomicCall.ArgumentList.Arguments.FirstOrDefault()?.Expression is not AnonymousFunctionExpressionSyntax callback)
            return false;
        // This exemption supports the shipping method's closed outer shape, not arbitrary C# purity.
        // Every statement and expression outside the atomic callback must match an allowed case;
        // unknown syntax, dynamic/unresolved binding and implicit effects fail closed.
        if (!HasReviewedOuterBody(method, atomicCall, callback, model)
            || !HasReviewedCallbackBody(callback, atomicCall, model)) return false;
        var calls = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => IsExecutorCall(call, model))
            .ToArray();
        return calls.Length == 1 && calls.All(call =>
        {
            // The nearest function must be the callback itself, not a deferred nested lambda/local function.
            var function = call.Ancestors().FirstOrDefault(node =>
                node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or MethodDeclarationSyntax);
            return function is AnonymousFunctionExpressionSyntax { Parent: ArgumentSyntax argument }
                && argument.Parent is ArgumentListSyntax { Parent: InvocationExpressionSyntax atomic }
                && atomic == mutationCalls[0]
                && IsReviewedAtomicCall(atomic, model)
                && atomic.ArgumentList.Arguments[0] == argument
                && atomic.Ancestors().First(node =>
                    node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or MethodDeclarationSyntax) == method
                && CompletesTaskInDirectReachableBody(atomic, method, model)
                && CompletesExecutorBeforeCallbackReturns(call, function, model);
        });
    }

    private static bool HasReviewedCallbackBody(AnonymousFunctionExpressionSyntax callback,
        InvocationExpressionSyntax atomic, SemanticModel model)
    {
        // This is a closed template for the reviewed shipping callback, not interprocedural purity.
        // The callback may only return/await the executor expression, including its reviewed writer
        // construction and framework completion wrappers. No opaque setup or trailing work is admitted.
        var body = callback switch
        {
            LambdaExpressionSyntax lambda => lambda.Body,
            AnonymousMethodExpressionSyntax anonymous => anonymous.Block,
            _ => null,
        };
        var expression = body switch
        {
            ExpressionSyntax value => value,
            BlockSyntax { Statements.Count: 1 } block
                when block.Statements[0] is ReturnStatementSyntax { Expression: { } value } => value,
            _ => null,
        };
        return expression is not null && IsReviewedBoundaryExpression(expression, atomic, callback, model, true);
    }

    private static bool IsReviewedMergeConstruction(ObjectCreationExpressionSyntax creation,
        AnonymousFunctionExpressionSyntax callback, SemanticModel model)
    {
        var outer = callback.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        var owner = outer is null ? null : model.GetDeclaredSymbol(outer)?.ContainingType;
        return creation.Initializer is null
            && model.GetSymbolInfo(creation).Symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor
            && constructor.ContainingType.Name == "Merge"
            && owner?.ToDisplayString() == typeof(NodeHierarchyCompositeCoordinator).FullName
            && SymbolEqualityComparer.Default.Equals(constructor.ContainingType.ContainingType, owner)
            && constructor.Parameters.Select(parameter => parameter.Name).SequenceEqual(
                new[] { "coordinator", "oldEntities", "newSchema", "newBody", "newOptions", "justification", "actor", "tenant", "at" },
                StringComparer.Ordinal)
            && HasReviewedWriterConstructionSource(constructor.ContainingType, model);
    }

    private static bool HasReviewedKernelBaseConstruction(IReadOnlyList<SyntaxTree> trees)
    {
        // The closed exemption must not prove a different preprocessor branch from the shipping build.
        if (trees.Any(tree => tree.GetRoot().ContainsDirectives)) return false;
        var baseTrees = trees.Where(tree => tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Any(type => type.Identifier.ValueText == "KernelWrite")).ToArray();
        if (baseTrees.Length != 1) return false;
        var references = BoundaryModel(baseTrees[0]).Compilation.References
            .Where(reference => reference.Display != typeof(WritePipeline).Assembly.Location);
        var globals = CSharpSyntaxTree.ParseText("global using System; global using System.Collections.Generic; global using System.Linq; global using System.Threading; global using System.Threading.Tasks;");
        var identity = typeof(WritePipeline).Assembly.GetName();
        var compilation = CSharpCompilation.Create(identity.Name!, baseTrees.Concat([globals]), references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var basis = compilation.GetTypeByMetadataName(typeof(KernelWrite<,,,>).FullName!);
        if (basis is null || basis.DeclaringSyntaxReferences.Length != 1
            || basis.DeclaringSyntaxReferences[0].GetSyntax() is not ClassDeclarationSyntax baseDeclaration
            || baseDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword)
            || compilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)) return false;
        // No base primary constructor, explicit/static constructor, or instance/static initializers.
        if (baseDeclaration.ParameterList is not null || baseDeclaration.BaseList is not null
            || baseDeclaration.AttributeLists.Count != 0
            || baseDeclaration.Members.Count != 6
            || baseDeclaration.Members.Any(member => member is not MethodDeclarationSyntax
                { Body: null, ExpressionBody: null } method
                || !method.Modifiers.Any(SyntaxKind.AbstractKeyword)
                || method.Modifiers.Any(SyntaxKind.StaticKeyword))) return false;
        return true;
    }

    private static bool HasReviewedCompiledBase(Type basis)
    {
        // Inspect the actual artifact: a linked Compile item cannot substitute an effectful constructor.
        const BindingFlags declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        if (!basis.IsAbstract || basis.BaseType != typeof(object) || basis.TypeInitializer is not null
            || basis.GetFields(declared).Length != 0
            || basis.GetMethods(declared).Length != 6
            || basis.GetMethods(declared).Any(method => !method.IsAbstract || method.IsStatic)) return false;
        var constructors = basis.GetConstructors(declared);
        if (constructors.Length != 1 || constructors[0].GetParameters().Length != 0) return false;
        var constructor = constructors[0];
        var body = constructor.GetMethodBody();
        if (body is null || body.LocalVariables.Count != 0 || body.ExceptionHandlingClauses.Count != 0
            || body.GetILAsByteArray() is not { } il) return false;
        var position = 0;
        while (position < il.Length && il[position] == 0x00) position++;
        if (position + 6 > il.Length || il[position++] != 0x02 || il[position++] != 0x28) return false;
        var target = constructor.Module.ResolveMethod(BitConverter.ToInt32(il, position));
        position += 4;
        if (target != typeof(object).GetConstructor(Type.EmptyTypes)) return false;
        while (position < il.Length && il[position] == 0x00) position++;
        return position == il.Length - 1 && il[position] == 0x2a;
    }

    private static bool HasReviewedCalculationBindings(MethodInfo calculation, MethodInfo encoder, Assembly? domainAssembly = null)
    {
        // This finite contract binds the reviewed template's calls, not arbitrary System.* helpers.
        Type Domain(Type type) => domainAssembly?.GetType(type.FullName!, throwOnError: true)! ?? type;
        var allowed = new Dictionary<Type, string[]>
        {
            [Domain(typeof(CreateOptions))] = ["get_ExplicitLocalPart", "get_Scheme", "get_Authority", "get_Nonce", "get_Issuer"],
            [Domain(typeof(Harborline.Api.Foundation.Assets.Common.SchemaId))] = ["get_Value"],
            [Domain(typeof(Harborline.Api.Foundation.Assets.Common.ActorId))] = ["get_Value"],
            [Domain(typeof(Harborline.Api.Foundation.Assets.Common.EntityId))] = [".ctor"],
            [typeof(string)] = ["get_Length", "get_Chars", "Contains", ".ctor"],
            [typeof(ArgumentException)] = [".ctor"],
            [typeof(System.Text.Encoding)] = ["get_UTF8", "GetBytes"],
            [typeof(System.Security.Cryptography.SHA256)] = ["HashData"],
            [typeof(Enumerable)] = ["All"],
            [typeof(Func<char, bool>)] = [".ctor"],
            [typeof(DefaultInterpolatedStringHandler)] = [".ctor", "AppendLiteral", "AppendFormatted", "ToStringAndClear"],
            [typeof(Span<byte>)] = [".ctor", "Slice", "op_Implicit"],
            [typeof(ReadOnlySpan<byte>)] = ["get_IsEmpty", "get_Length", "get_Item", "GetEnumerator", "op_Implicit"],
            [typeof(ReadOnlySpan<byte>.Enumerator)] = ["get_Current", "MoveNext"],
        };
        var calls = RawMutationPortSymbolInventoryTests.CalledMethods(calculation).Select(call => call.Target).ToArray();
        var hash = typeof(System.Security.Cryptography.SHA256).GetMethod("HashData", [typeof(ReadOnlySpan<byte>), typeof(Span<byte>)])!;
        var utf8 = typeof(System.Text.Encoding).GetProperty("UTF8")!.GetMethod!;
        if (!calls.Contains(hash) || !calls.Contains(utf8) || !calls.Contains(encoder)) return false;
        bool ReviewedCall(MethodBase method) => method.Equals(encoder)
            || method.DeclaringType is { } type && allowed.TryGetValue(type, out var names) && names.Contains(method.Name, StringComparer.Ordinal)
            || method is MethodInfo { ReturnType: { } result, IsStatic: false } lambda
                && result == typeof(bool) && lambda.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual([typeof(char)])
                && lambda.Name.StartsWith("<DeriveEntityId>b__", StringComparison.Ordinal)
                && lambda.DeclaringType is { } closure && closure.DeclaringType == calculation.DeclaringType
                && closure.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
                && !RawMutationPortSymbolInventoryTests.CalledMethods(lambda).Any();
        if (!calls.All(ReviewedCall) || !RawMutationPortSymbolInventoryTests.CalledMethods(encoder).All(call => ReviewedCall(call.Target))) return false;
        return ReferencedCalculationFields(calculation).Concat(ReferencedCalculationFields(encoder)).All(field =>
            field.Equals(typeof(string).GetField(nameof(string.Empty)))
            || field.DeclaringType is { } closure && closure.DeclaringType == calculation.DeclaringType
                && closure.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
                && field.Name.StartsWith("<>9", StringComparison.Ordinal)
                && (field.FieldType == typeof(Func<char, bool>) || field.FieldType == field.DeclaringType));
    }

    private static IEnumerable<FieldInfo> ReferencedCalculationFields(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null) yield break;
        var opcodes = typeof(System.Reflection.Emit.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(System.Reflection.Emit.OpCode))
            .Select(field => (System.Reflection.Emit.OpCode)field.GetValue(null)!)
            .GroupBy(opcode => opcode.Value).ToDictionary(group => group.Key, group => group.First());
        var position = 0;
        while (position < il.Length)
        {
            var first = il[position++];
            var code = opcodes[first == 0xfe ? unchecked((short)(0xfe00 | il[position++])) : (short)first];
            if (code.OperandType == System.Reflection.Emit.OperandType.InlineField)
                yield return method.Module.ResolveField(BitConverter.ToInt32(il, position), method.DeclaringType?.GetGenericArguments(),
                    method is MethodInfo info ? info.GetGenericArguments() : null)!;
            position += code.OperandType switch
            {
                System.Reflection.Emit.OperandType.InlineNone => 0,
                System.Reflection.Emit.OperandType.ShortInlineBrTarget or System.Reflection.Emit.OperandType.ShortInlineI or System.Reflection.Emit.OperandType.ShortInlineVar => 1,
                System.Reflection.Emit.OperandType.InlineVar => 2,
                System.Reflection.Emit.OperandType.InlineI or System.Reflection.Emit.OperandType.InlineBrTarget or System.Reflection.Emit.OperandType.InlineField
                    or System.Reflection.Emit.OperandType.InlineMethod or System.Reflection.Emit.OperandType.InlineSig or System.Reflection.Emit.OperandType.InlineString
                    or System.Reflection.Emit.OperandType.InlineTok or System.Reflection.Emit.OperandType.InlineType or System.Reflection.Emit.OperandType.ShortInlineR => 4,
                System.Reflection.Emit.OperandType.InlineI8 or System.Reflection.Emit.OperandType.InlineR => 8,
                System.Reflection.Emit.OperandType.InlineSwitch => 4 + BitConverter.ToInt32(il, position) * 4,
                _ => throw new InvalidOperationException($"Unsupported construction operand {code.OperandType}."),
            };
        }
    }

    private static bool HasRecordedCompileInput(IReadOnlyList<string> lines, string assemblyHash, string expectedFile)
    {
        // The trusted MSBuild target records evaluated Compile paths/hashes and the emitted binary hash.
        // Unlike PDB documents, these records cannot be manufactured with source-level #line/checksum directives.
        if (lines.Count == 0 || lines[0] != $"assembly|{assemblyHash}") return false;
        var expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(RepositoryRoot(), expectedFile))));
        var matches = lines.Skip(1).Select(line => line.Split('|'))
            .Where(parts => parts.Length == 3 && parts[0] == "source"
                && Path.GetRelativePath(RepositoryRoot(), Path.GetFullPath(parts[1])).Replace('\\', '/') == expectedFile).ToArray();
        return matches.Length == 1 && matches[0][2] == expectedHash;
    }

    private static bool HasBoundCompiledSource(MethodBase method, string expectedFile, string? imagePath = null)
    {
        var assemblyPath = imagePath ?? method.Module.Assembly.Location;
        var pdbPath = Path.ChangeExtension(assemblyPath, imagePath is null ? ".pdb" : ".compile-symbols");
        var inputsPath = Path.ChangeExtension(assemblyPath, ".compile-inputs.txt");
        if (!File.Exists(assemblyPath) || !File.Exists(pdbPath) || !File.Exists(inputsPath)
            || !HasRecordedCompileInput(File.ReadAllLines(inputsPath), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))), expectedFile)) return false;
        using var assemblyStream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(assemblyStream);
        var metadata = pe.GetMetadataReader();
        if (metadata.GetGuid(metadata.GetModuleDefinition().Mvid) != method.Module.ModuleVersionId) return false;
        var entries = pe.ReadDebugDirectory().Where(entry => entry.Type == DebugDirectoryEntryType.CodeView).ToArray();
        if (entries.Length != 1) return false;
        using var pdbStream = File.OpenRead(pdbPath);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
        var reader = provider.GetMetadataReader();
        var id = reader.DebugMetadataHeader!.Id.ToArray();
        if (id.Length != 20 || new Guid(id.AsSpan(0, 16)) != pe.ReadCodeViewDebugDirectoryData(entries[0]).Guid
            || BitConverter.ToUInt32(id, 16) != entries[0].Stamp) return false;
        return HasMatchingSourceDocument(reader, (MethodDefinitionHandle)MetadataTokens.Handle(method.MetadataToken), expectedFile);
    }

    private static bool HasMatchingSourceDocument(MetadataReader reader, MethodDefinitionHandle method, string expectedFile)
    {
        var debug = reader.GetMethodDebugInformation(method);
        var documents = debug.GetSequencePoints().Where(point => !point.IsHidden)
            .Select(point => point.Document.IsNil ? debug.Document : point.Document).Distinct().ToArray();
        if (documents.Length == 0) return false;
        var expectedHash = SHA256.HashData(File.ReadAllBytes(Path.Combine(RepositoryRoot(), expectedFile)));
        return documents.All(handle => !handle.IsNil
            && Audit.AuditAppendSymbolInventory.NormalizeFile(reader.GetString(reader.GetDocument(handle).Name)) == expectedFile
            && reader.GetGuid(reader.GetDocument(handle).HashAlgorithm) == new Guid("8829d00f-11b8-4213-878b-770e8597ac16")
            && reader.GetBlobBytes(reader.GetDocument(handle).Hash).SequenceEqual(expectedHash));
    }

    private static bool HasReviewedCalculationSource(SyntaxTree tree)
    {
        if (tree.GetRoot().ContainsDirectives) return false;
        var actualCalculation = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "DeriveEntityId");
        var reviewedCalculation = SyntaxFactory.ParseMemberDeclaration("""
            public static EntityId DeriveEntityId(SchemaId schema, CreateOptions options)
            {
                if (options.ExplicitLocalPart is { Length: > 0 } explicitLocal)
                {
                    if (explicitLocal.Length == 26 &&
                        explicitLocal.All(c => c is >= 'a' and <= 'z' or >= '2' and <= '7') &&
                        "aeimquy4".Contains(explicitLocal[25], StringComparison.Ordinal))
                        throw new ArgumentException("Explicit local part is reserved for schema-derived IDs.", nameof(options));
                    return new EntityId(options.Scheme, options.Authority, explicitLocal);
                }
                var input = Encoding.UTF8.GetBytes($"{schema.Value}|{options.Authority}|{options.Nonce}|{options.Issuer.Value}");
                Span<byte> digest = stackalloc byte[32];
                SHA256.HashData(input, digest);
                var local = Base32Lower.Encode(digest[..16]);
                return new EntityId(options.Scheme, options.Authority, local);
            }
            """);
        var owners = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(type => type.Identifier.ValueText == "InMemoryEntityStore").ToArray();
        if (owners.Length != 1 || owners[0].Modifiers.Any(SyntaxKind.PartialKeyword)
            || owners[0].Members.OfType<ConstructorDeclarationSyntax>().Any(ctor => ctor.Modifiers.Any(SyntaxKind.StaticKeyword))
            || owners[0].Members.OfType<FieldDeclarationSyntax>().Any(field => field.Modifiers.Any(SyntaxKind.StaticKeyword)
                && !field.Modifiers.Any(SyntaxKind.ConstKeyword))) return false;
        var encoders = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(type => type.Identifier.ValueText == "Base32Lower").ToArray();
        var reviewedEncoder = SyntaxFactory.ParseMemberDeclaration("""
            internal static class Base32Lower
            {
                private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

                public static string Encode(ReadOnlySpan<byte> bytes)
                {
                    if (bytes.IsEmpty) return string.Empty;

                    var outputLength = (bytes.Length * 8 + 4) / 5;
                    var output = new char[outputLength];
                    int buffer = 0, bitsLeft = 0, outputIndex = 0;

                    foreach (var b in bytes)
                    {
                        buffer = (buffer << 8) | b;
                        bitsLeft += 8;
                        while (bitsLeft >= 5)
                        {
                            output[outputIndex++] = Alphabet[(buffer >> (bitsLeft - 5)) & 0x1F];
                            bitsLeft -= 5;
                        }
                    }

                    if (bitsLeft > 0)
                        output[outputIndex] = Alphabet[(buffer << (5 - bitsLeft)) & 0x1F];

                    return new string(output);
                }
            }
            """);
        return reviewedCalculation is not null && HasSameReviewedTokens(actualCalculation, reviewedCalculation)
            && encoders.Length == 1 && reviewedEncoder is not null && HasSameReviewedTokens(encoders[0], reviewedEncoder);
    }

    private static bool HasReviewedWriterConstructionSource(INamedTypeSymbol writer, SemanticModel model)
    {
        // Closed writer contract: primary captures, initializers, six stages, and the finite
        // local helper closure. Injected services and existing identifier value contracts,
        // framework hashing/span/string APIs, and compiler-generated lambdas remain trusted.
        // Changes to this reviewed domain require a new literal template and boundary review.
        if (writer.DeclaringSyntaxReferences.Length != 1
            || writer.DeclaringSyntaxReferences[0].GetSyntax() is not ClassDeclarationSyntax declaration
            || declaration.SyntaxTree.GetRoot().ContainsDirectives
            || declaration.Members.Any(member => member is not FieldDeclarationSyntax and not MethodDeclarationSyntax)) return false;
        // ADR-0038's reviewed Merge stages are a closed source contract, not arbitrary callbacks.
        // The literal snapshot deliberately includes React and ValidateTargetTenant.
        var expected = CSharpSyntaxTree.ParseText("""
            private sealed class Merge(
                NodeHierarchyCompositeCoordinator coordinator,
                IReadOnlyList<EntityId> oldEntities,
                SchemaId newSchema,
                JsonDocument newBody,
                CreateOptions newOptions,
                string justification,
                ActorId actor,
                TenantId tenant,
                AdmittedInstant at)
                : KernelWrite<IReadOnlyList<EntityEdge>, CreateOptions, ValidatedRecordBody, MergeResult>
            {
                private readonly EntityId expectedNewId = InMemoryEntityStore.DeriveEntityId(newSchema, newOptions);
                private CompositeAuthorization authorization = null!;
                private IReadOnlyList<EntityEdge> displaced = [];
                private MergeResult result = null!;

                protected override async ValueTask AuthorizeAsync(CancellationToken ct) =>
                    authorization = await coordinator.DecideAllAsync(
                        [expectedNewId, .. oldEntities], actor, tenant, at, ct).ConfigureAwait(false);

                /// <summary>Binds the children the merge displaces. A child that is itself one of the merged records is
                /// superseded and deleted with them, so it is not moved under the merged record.</summary>
                protected override async ValueTask<IReadOnlyList<EntityEdge>?> BindAsync(CancellationToken ct) =>
                    displaced = await coordinator.ReadChildrenNotEndedAsync(oldEntities, at.Value, ct).ConfigureAwait(false);

                protected override ValueTask<CreateOptions> MutateAsync(IReadOnlyList<EntityEdge> bound, CancellationToken ct) =>
                    ValueTask.FromResult(newOptions with { ValidFrom = at.Value });

                protected override async ValueTask<ValidatedRecordBody> ValidateAsync(
                    IReadOnlyList<EntityEdge> bound, CreateOptions mutation, CancellationToken ct)
                {
                    ValidateTargetTenant(mutation, tenant);
                    authorization = await coordinator.DecideAllAsync(
                        bound.Select(edge => edge.From), actor, tenant, at, ct, authorization).ConfigureAwait(false);
                    // Ticket 366: the merge target is a record, admitted from the decision that admitted it.
                    return await ValidatedRecordBody.AdmitAsync(
                        coordinator.Validator, authorization.Require(expectedNewId), newSchema, newBody, tenant,
                        mutation.Binding, ct).ConfigureAwait(false);
                }

                private static void ValidateTargetTenant(CreateOptions newOptions, TenantId tenant)
                {
                    if (newOptions.Tenant != tenant)
                        throw new ArgumentException("The merge target tenant does not match the admitted composite.", nameof(newOptions));
                }

                protected override async ValueTask CommitAsync(ValidatedRecordBody validated, CancellationToken ct)
                {
                    var store = coordinator.Store;
                    var newId = await coordinator.Entities.CreateAsync(
                        validated, newOptions with { ValidFrom = at.Value }, ct).ConfigureAwait(false);
                    if (newId != expectedNewId)
                        throw new InvalidOperationException("The entity store minted an id different from the pre-authorized merge target.");
                    var reassigned = new List<EntityId>();
                    foreach (var oldId in oldEntities)
                    {
                        authorization.Require(oldId);
                        foreach (var edge in displaced.Where(edge => edge.To == oldId))
                        {
                            authorization.Require(edge.From);
                            authorization.Require(newId);
                            // An edge committed by a later-admitted act may start after this merge's admitted clock.
                            // Close it at its start (an empty half-open interval), never before it, and preserve that
                            // scheduled start on the replacement. Entity and audit admission remain at the merge clock.
                            var start = edge.Validity.ValidFrom > at.Value ? edge.Validity.ValidFrom : at.Value;
                            await store.InvalidateEdgeAsync(edge.Id, start, ct).ConfigureAwait(false);
                            if (oldEntities.Contains(edge.From))
                                continue;
                            var replacementEdge = await store.AddEdgeAsync(
                                edge.From, newId, EdgeKind.ChildOf, start, null, ct).ConfigureAwait(false);
                            if (edge.Validity.ValidTo is { } validTo)
                                await store.InvalidateEdgeAsync(replacementEdge.Id, validTo, ct).ConfigureAwait(false);
                            reassigned.Add(edge.From);
                        }
                        authorization.Require(oldId);
                        authorization.Require(newId);
                        await store.AddEdgeAsync(oldId, newId, EdgeKind.SupersededBy, at.Value, null, ct).ConfigureAwait(false);
                        await coordinator.Entities.DeleteAsync(
                            oldId, new DeleteOptions(actor, at.Value, justification), ct).ConfigureAwait(false);
                    }
                    using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
                    {
                        op = "merge",
                        newId = newId.ToString(),
                        oldIds = oldEntities.Select(id => id.ToString()).ToArray(),
                        reassigned = reassigned.Select(id => id.ToString()).ToArray(),
                    }));
                    await coordinator.AuditWriter.AppendAsync(new AuditAppend(
                        newId, null, Op.Merge, actor, tenant, at.Value, payload, justification),
                        authorization.Require(newId), ct)
                        .ConfigureAwait(false);
                    result = new MergeResult(newId, oldEntities, reassigned);
                }

                protected override ValueTask<MergeResult> ReactAsync(ValidatedRecordBody validated, CancellationToken ct) =>
                    ValueTask.FromResult(result);
            }
            """).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Single();
        if (!HasSameReviewedTokens(declaration, expected) || !HasReviewedMergeOwnerClosure(writer)
            || !HasReviewedMergeDependencyBindings(writer, model)) return false;
        var derive = declaration.Members.OfType<FieldDeclarationSyntax>()
            .SelectMany(field => field.DescendantNodes().OfType<InvocationExpressionSyntax>()).Single();
        if (model.GetSymbolInfo(derive).Symbol is not IMethodSymbol { IsStatic: true, Arity: 0, Parameters.Length: 2 } calculation
            || calculation.Name != nameof(InMemoryEntityStore.DeriveEntityId)
            || calculation.ContainingType.ToDisplayString() != typeof(InMemoryEntityStore).FullName
            || calculation.ContainingAssembly.Identity.ToString() != typeof(InMemoryEntityStore).Assembly.FullName
            || writer.BaseType is not { } basis
            || !SymbolEqualityComparer.Default.Equals(basis.OriginalDefinition,
                model.Compilation.GetTypeByMetadataName(typeof(KernelWrite<,,,>).FullName!))) return false;
        return HasReviewedConstructionArtifacts();
    }

    private static bool HasReviewedMergeOwnerClosure(INamedTypeSymbol writer)
    {
        var owner = writer.ContainingType;
        if (owner.DeclaringSyntaxReferences.Length != 1
            || owner.DeclaringSyntaxReferences[0].GetSyntax() is not ClassDeclarationSyntax declaration
            || declaration.Members.OfType<ConstructorDeclarationSyntax>().Any()) return false;
        // Only the local helpers reached by Merge are pinned. Injected services keep their
        // existing owner contracts; unrelated Split/Reparent stages are not a purity proof.
        var members = declaration.Members.Where(member => member is FieldDeclarationSyntax or PropertyDeclarationSyntax
            || member is MethodDeclarationSyntax method && method.Identifier.ValueText is "DecideAllAsync" or "ReadChildrenNotEndedAsync"
            || member is ClassDeclarationSyntax type && type.Identifier.ValueText == "CompositeAuthorization");
        var actual = declaration.WithMembers(SyntaxFactory.List(members));
        var expected = CSharpSyntaxTree.ParseText("""
            public sealed class NodeHierarchyCompositeCoordinator(
                IEntityMutationStore entities,
                IHierarchyCompositeUnitOfWork unitOfWork,
                IHierarchyAuthorizedAuditWriter audit,
                AuthorizationGate gate,
                TimeProvider timeProvider,
                [FromKeyedServices(CompiledSchemaEntityValidator.RecordWriteKey)] IEntityValidator validator,
                IWritePipelineObserver? pipelineObserver = null)
                : IHierarchyCompositeCoordinator
            {
                private static readonly AuthorizationOperation RecordsWrite =
                    AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite);

                private IHierarchyCompositeUnitOfWork Store => unitOfWork;
                private IHierarchyAuthorizedAuditWriter AuditWriter => audit;
                private IEntityMutationStore Entities => entities;
                private IEntityValidator Validator => validator;
                private async Task<CompositeAuthorization> DecideAllAsync(
                    IEnumerable<EntityId> targets,
                    ActorId actor,
                    TenantId tenant,
                    AdmittedInstant at,
                    CancellationToken ct,
                    CompositeAuthorization? decided = null)
                {
                    var decisions = new Dictionary<string, AuthorizationDecision>(
                        decided?.Decisions ?? new Dictionary<string, AuthorizationDecision>(), StringComparer.Ordinal);
                    foreach (var target in targets.Distinct())
                    {
                        if (decisions.ContainsKey(target.LocalPart))
                            continue;
                        var scope = ScopeExpression.Parse($"/records/{target.LocalPart}");
                        var decision = await gate.DecideAsync(new AuthorizationGateRequest(
                            new PermissionAtom(RecordsWrite, scope),
                            actor,
                            tenant,
                            new AuthorizationTarget("record", target.LocalPart, scope),
                            at), ct).ConfigureAwait(false);
                        decision.RequireAllowed();
                        decisions.Add(target.LocalPart, decision);
                    }
                    return new CompositeAuthorization(decisions);
                }
                private async Task<IReadOnlyList<EntityEdge>> ReadChildrenNotEndedAsync(
                    IEnumerable<EntityId> parents,
                    DateTimeOffset asOf,
                    CancellationToken ct,
                    Func<EntityEdge, bool>? include = null)
                {
                    var edges = new List<EntityEdge>();
                    foreach (var parent in parents.Distinct())
                    await foreach (var edge in unitOfWork.GetChildrenNotEndedAsync(parent, asOf, ct).ConfigureAwait(false))
                        if (include is null || include(edge))
                            edges.Add(edge);
                    return edges;
                }
                private sealed class CompositeAuthorization(IReadOnlyDictionary<string, AuthorizationDecision> decisions)
                {
                    internal IReadOnlyDictionary<string, AuthorizationDecision> Decisions => decisions;

                    internal AuthorizationDecision Require(EntityId target)
                    {
                        if (!decisions.TryGetValue(target.LocalPart, out var decision))
                            throw new InvalidOperationException($"The hierarchy target '{target}' was not authorized before the unit of work.");
                        return decision.RequireAllowedReaction(
                            RecordsWrite, decision.Request.Tenant, "record", target.LocalPart);
                    }

                }
            }
            """).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().First();
        return HasSameReviewedTokens(actual, expected);
    }

    private static bool HasReviewedMergeDependencyBindings(INamedTypeSymbol writer, SemanticModel model)
    {
        var owner = writer.ContainingType;
        var ownerSource = (ClassDeclarationSyntax)owner.DeclaringSyntaxReferences.Single().GetSyntax();
        var auditContract = model.Compilation.GetTypeByMetadataName(typeof(IHierarchyAuthorizedAuditWriter).FullName!);
        if (auditContract?.DeclaringSyntaxReferences is not { Length: 1 }
            || auditContract.DeclaringSyntaxReferences[0].GetSyntax() is not InterfaceDeclarationSyntax auditSource
            || auditSource.SyntaxTree != ownerSource.SyntaxTree) return false;
        var expectedAudit = SyntaxFactory.ParseMemberDeclaration("""
            public interface IHierarchyAuthorizedAuditWriter
            {
                Task<AuditId> AppendAsync(AuditAppend append, AuthorizationDecision decision, CancellationToken ct = default);
            }
            """);
        if (expectedAudit is null || !HasSameReviewedTokens(auditSource, expectedAudit)) return false;
        INamedTypeSymbol[] owned = [owner, writer, owner.GetTypeMembers("CompositeAuthorization").Single(), auditContract];
        // These existing service/value/framework assemblies are the explicit trusted contract
        // boundary. No new source-declared dependency, same-namespace shadow, or host helper
        // may acquire that trust just by using an approved simple name.
        var trustedAssemblies = new[] {
            typeof(object).Assembly, typeof(Enumerable).Assembly, typeof(System.Text.Json.JsonDocument).Assembly,
            typeof(EntityId).Assembly, typeof(TenantId).Assembly, typeof(WritePipeline).Assembly,
            typeof(Harborline.Api.Foundation.Authorization.AuthorizationGate).Assembly,
            typeof(Harborline.Api.Foundation.Authorization.AuthorizationDecision).Assembly,
            typeof(Harborline.Api.Foundation.IdentityAtlas.TeamRolePermissions).Assembly,
            typeof(Harborline.Api.Kernel.Schema.CompiledSchemaEntityValidator).Assembly,
            typeof(Microsoft.Extensions.DependencyInjection.FromKeyedServicesAttribute).Assembly,
        }.Select(assembly => assembly.FullName).ToHashSet(StringComparer.Ordinal);
        bool Trusted(ITypeSymbol type)
        {
            if (type is IArrayTypeSymbol array) return Trusted(array.ElementType);
            if (type is not INamedTypeSymbol named) return type is ITypeParameterSymbol;
            if (named.IsAnonymousType) return named.GetMembers().OfType<IPropertySymbol>().All(property => Trusted(property.Type));
            return (owned.Any(local => SymbolEqualityComparer.Default.Equals(local, named.OriginalDefinition))
                    || trustedAssemblies.Contains(named.ContainingAssembly.Identity.ToString()))
                && named.TypeArguments.All(Trusted);
        }
        IEnumerable<SyntaxNode> roots = [ownerSource.ParameterList!, ownerSource.BaseList!, auditSource,
            writer.DeclaringSyntaxReferences.Single().GetSyntax()];
        roots = roots.Concat(ownerSource.Members.Where(member => member is FieldDeclarationSyntax or PropertyDeclarationSyntax
            || member is MethodDeclarationSyntax method && method.Identifier.ValueText is "DecideAllAsync" or "ReadChildrenNotEndedAsync"
            || member is ClassDeclarationSyntax type && type.Identifier.ValueText == "CompositeAuthorization"));
        foreach (var node in roots.SelectMany(root => root.DescendantNodesAndSelf()))
        {
            // nameof is a compiler form: its identifier has an error pseudo-type, while
            // the complete expression is a bound constant string and its argument is checked below.
            if (node is IdentifierNameSyntax { Identifier.ValueText: "nameof", Parent: InvocationExpressionSyntax nameOf }
                && model.GetSymbolInfo(nameOf).Symbol is null
                && model.GetTypeInfo(nameOf).Type?.SpecialType == SpecialType.System_String) continue;
            if (node is ExpressionSyntax expression && model.GetTypeInfo(expression).Type is { } type && !Trusted(type)) return false;
            if (node is ExpressionSyntax or AttributeSyntax)
            {
                var symbol = model.GetSymbolInfo(node).Symbol;
                if (symbol is INamedTypeSymbol named && !Trusted(named)
                    || symbol?.ContainingType is { } declaring && !Trusted(declaring)) return false;
            }
        }
        return true;
    }

    // Authenticate compiler output rather than coverage-rewritten IL. The approved
    // build target/SDK and coverage collector are trusted tools; this does not
    // attest arbitrary malicious post-build rewrites of the live coverage binary.
    [Fact]
    public void CompilerProofImagesAuthenticateUnderTheActiveTestCollector()
    {
        var directory = Path.GetDirectoryName(typeof(InMemoryEntityStore).Assembly.Location)!;
        var foundationPath = Path.Combine(directory, "Harborline.Api.Foundation.compile-image");
        var kernelPath = Path.Combine(directory, "Harborline.Api.Kernel.Runtime.compile-image");
        var hostPath = Path.Combine(directory, "Harborline.Api.LocalNodeHost.compile-image");
        foreach (var (assembly, image) in new[] { (typeof(InMemoryEntityStore).Assembly, foundationPath), (typeof(WritePipeline).Assembly, kernelPath), (typeof(NodeHierarchyCompositeCoordinator).Assembly, hostPath) })
        {
            Assert.True(File.Exists(image), image);
            using var stream = File.OpenRead(image);
            using var pe = new PEReader(stream);
            Assert.Equal(assembly.ManifestModule.ModuleVersionId, pe.GetMetadataReader().GetGuid(pe.GetMetadataReader().GetModuleDefinition().Mvid));
            Assert.Equal(assembly.FullName, AssemblyName.GetAssemblyName(image).FullName);
            Assert.True(HasMatchingCompilerImage(assembly, image), image);
        }
        using var context = new ConstructionProofContext(foundationPath, kernelPath, hostPath);
        var hostMethod = context.Host!.GetType(typeof(NodeHierarchyCompositeCoordinator).FullName!)!.GetMethod("MergeAsync")!;
        Assert.True(HasSameMethodIdentity(hostMethod, typeof(NodeHierarchyCompositeCoordinator).GetMethod("MergeAsync")!), "host method identity");
        Assert.True(HasBoundCompiledSource(hostMethod.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic)!, HostHierarchySource, hostPath), "host source binding");
        var owner = context.Foundation.GetType(typeof(InMemoryEntityStore).FullName!)!;
        var encoder = context.Foundation.GetType("Harborline.Api.Foundation.Assets.Entities.Base32Lower")!;
        var calculation = owner.GetMethod(nameof(InMemoryEntityStore.DeriveEntityId))!;
        var encode = encoder.GetMethod("Encode")!;
        var pipeline = context.Kernel.GetType(typeof(WritePipeline).FullName!)!;
        Assert.Null(owner.TypeInitializer);
        Assert.Null(encoder.TypeInitializer);
        Assert.True(HasReviewedCompiledBase(context.Kernel.GetType(typeof(KernelWrite<,,,>).FullName!)!), "compiled base");
        Assert.True(HasSameMethodIdentity(calculation, typeof(InMemoryEntityStore).GetMethod(nameof(InMemoryEntityStore.DeriveEntityId))!), "calculation method identity");
        Assert.True(HasSameMethodIdentity(encode, typeof(InMemoryEntityStore).Assembly.GetType(encoder.FullName!)!.GetMethod("Encode")!), "encoder method identity");
        Assert.True(HasSameMethodIdentity(pipeline.GetMethod(nameof(WritePipeline.NameOf))!, typeof(WritePipeline).GetMethod(nameof(WritePipeline.NameOf))!), "pipeline method identity");
        Assert.True(HasBoundCompiledSource(pipeline.GetMethod(nameof(WritePipeline.NameOf))!, "packages/kernel-runtime/WritePipelineStage.cs", kernelPath), "kernel source binding");
        Assert.True(HasBoundCompiledSource(calculation, "packages/foundation/Assets/Entities/InMemoryEntityStore.cs", foundationPath), "calculation source binding");
        Assert.True(HasBoundCompiledSource(encode, "packages/foundation/Assets/Entities/InMemoryEntityStore.cs", foundationPath), "encoder source binding");
        Assert.True(HasReviewedCalculationBindings(calculation, encode, context.Foundation), "calculation bindings");
    }

    private static bool HasReviewedConstructionArtifacts(string? proofDirectory = null, Assembly? actualHost = null)
    {
        const string baseFile = "packages/kernel-runtime/WritePipelineStage.cs";
        const string calculationFile = "packages/foundation/Assets/Entities/InMemoryEntityStore.cs";
        var actualOwner = typeof(InMemoryEntityStore);
        var actualKernel = typeof(KernelWrite<,,,>);
        actualHost ??= typeof(NodeHierarchyCompositeCoordinator).Assembly;
        proofDirectory ??= Path.GetDirectoryName(actualOwner.Assembly.Location)!;
        var foundationPath = Path.Combine(proofDirectory, actualOwner.Assembly.GetName().Name + ".compile-image");
        var kernelPath = Path.Combine(proofDirectory, actualKernel.Assembly.GetName().Name + ".compile-image");
        var hostPath = Path.Combine(proofDirectory, actualHost.GetName().Name + ".compile-image");
        if (!HasMatchingCompilerImage(actualHost, hostPath)
            || !HasMatchingCompilerImage(actualOwner.Assembly, foundationPath)
            || !HasMatchingCompilerImage(actualKernel.Assembly, kernelPath)) return false;
        using var context = new ConstructionProofContext(foundationPath, kernelPath, hostPath);
        var hostMethod = context.Host!.GetType(typeof(NodeHierarchyCompositeCoordinator).FullName!, throwOnError: true)!.GetMethod("MergeAsync")!;
        var actualHostMethod = actualHost.GetType(typeof(NodeHierarchyCompositeCoordinator).FullName!, throwOnError: true)!.GetMethod("MergeAsync")!;
        if (!HasSameMethodIdentity(hostMethod, actualHostMethod)
            || hostMethod.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic) is not { } hostBody
            || !HasBoundCompiledSource(hostBody, HostHierarchySource, hostPath)) return false;
        var owner = context.Foundation.GetType(actualOwner.FullName!, throwOnError: true)!;
        var encoder = context.Foundation.GetType("Harborline.Api.Foundation.Assets.Entities.Base32Lower", throwOnError: true)!;
        var kernel = context.Kernel.GetType(actualKernel.FullName!, throwOnError: true)!;
        var pipeline = context.Kernel.GetType(typeof(WritePipeline).FullName!, throwOnError: true)!;
        var calculation = owner.GetMethod(nameof(InMemoryEntityStore.DeriveEntityId))!;
        var encode = encoder.GetMethod("Encode")!;
        var nameOf = pipeline.GetMethod(nameof(WritePipeline.NameOf))!;
        if (owner.TypeInitializer is not null || encoder.TypeInitializer is not null
            || !HasSameMethodIdentity(calculation, actualOwner.GetMethod(nameof(InMemoryEntityStore.DeriveEntityId))!)
            || !HasSameMethodIdentity(encode, actualOwner.Assembly.GetType(encoder.FullName!)!.GetMethod("Encode")!)
            || !HasSameMethodIdentity(nameOf, typeof(WritePipeline).GetMethod(nameof(WritePipeline.NameOf))!)
            || !HasReviewedCompiledBase(kernel)
            || !HasBoundCompiledSource(nameOf, baseFile, kernelPath)
            || !HasBoundCompiledSource(calculation, calculationFile, foundationPath)
            || !HasBoundCompiledSource(encode, calculationFile, foundationPath)
            || !HasReviewedCalculationBindings(calculation, encode, context.Foundation)) return false;
        var root = RepositoryRoot();
        var hostTree = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, HostHierarchySource)));
        return HasReviewedCompiledHostCalls(hostMethod.DeclaringType!, BoundaryModel(hostTree), hostPath)
            && HasReviewedKernelBaseConstruction([CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, baseFile)))])
            && HasReviewedCalculationSource(CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, calculationFile))));
    }

    [Theory]
    [InlineData("missing-image")]
    [InlineData("modified-image")]
    [InlineData("stale-module")]
    [InlineData("missing-symbols")]
    [InlineData("replaced-symbols")]
    [InlineData("wrong-manifest")]
    [InlineData("host-missing-image")]
    [InlineData("host-modified-image")]
    [InlineData("host-stale-module")]
    [InlineData("host-missing-symbols")]
    [InlineData("host-replaced-symbols")]
    [InlineData("host-wrong-manifest")]
    public void CompilerConstructionProofRejectsMissingTamperedOrStaleEvidence(string change)
    {
        var output = Path.GetDirectoryName(typeof(InMemoryEntityStore).Assembly.Location)!;
        var directory = Path.Combine(Path.GetTempPath(), "construction-proof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var name in new[] { "Harborline.Api.Foundation", "Harborline.Api.Kernel.Runtime", "Harborline.Api.LocalNodeHost" })
                foreach (var extension in new[] { ".compile-image", ".compile-symbols", ".compile-inputs.txt" })
                    File.Copy(Path.Combine(output, name + extension), Path.Combine(directory, name + extension));
            Assert.True(HasReviewedConstructionArtifacts(directory));
            var target = change.StartsWith("host-", StringComparison.Ordinal) ? typeof(NodeHierarchyCompositeCoordinator).Assembly : typeof(InMemoryEntityStore).Assembly;
            if (change.StartsWith("host-", StringComparison.Ordinal)) change = change[5..];
            var imagePath = Path.Combine(directory, target.GetName().Name + ".compile-image");
            var symbolsPath = Path.ChangeExtension(imagePath, ".compile-symbols");
            var manifestPath = Path.ChangeExtension(imagePath, ".compile-inputs.txt");
            switch (change)
            {
                case "missing-image": File.Delete(imagePath); break;
                case "modified-image":
                    using (var stream = new FileStream(imagePath, FileMode.Append)) stream.WriteByte(0x42);
                    break;
                case "stale-module":
                    var bytes = File.ReadAllBytes(imagePath);
                    var id = target.ManifestModule.ModuleVersionId.ToByteArray();
                    var index = bytes.AsSpan().IndexOf(id);
                    Assert.True(index >= 0);
                    bytes[index] ^= 0x01;
                    File.WriteAllBytes(imagePath, bytes);
                    var lines = File.ReadAllLines(manifestPath);
                    lines[0] = "assembly|" + Convert.ToHexString(SHA256.HashData(bytes));
                    File.WriteAllLines(manifestPath, lines);
                    break;
                case "missing-symbols": File.Delete(symbolsPath); break;
                case "replaced-symbols":
                    File.Copy(Path.Combine(directory, "Harborline.Api.Kernel.Runtime.compile-symbols"), symbolsPath, overwrite: true);
                    break;
                case "wrong-manifest":
                    File.WriteAllText(manifestPath, "assembly|WRONG\n");
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(change));
            }
            Assert.False(HasReviewedConstructionArtifacts(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static bool HasSameMethodIdentity(MethodInfo compiled, MethodInfo runtime) =>
        compiled.DeclaringType?.AssemblyQualifiedName == runtime.DeclaringType?.AssemblyQualifiedName
        && compiled.Module.Assembly.FullName == runtime.Module.Assembly.FullName
        && compiled.Name == runtime.Name && compiled.IsStatic == runtime.IsStatic
        && compiled.GetGenericArguments().Length == runtime.GetGenericArguments().Length
        && compiled.ReturnType.AssemblyQualifiedName == runtime.ReturnType.AssemblyQualifiedName
        && compiled.GetParameters().Select(parameter => parameter.ParameterType.AssemblyQualifiedName)
            .SequenceEqual(runtime.GetParameters().Select(parameter => parameter.ParameterType.AssemblyQualifiedName));

    private static bool HasMatchingCompilerImage(Assembly runtimeAssembly, string imagePath)
    {
        var inputsPath = Path.ChangeExtension(imagePath, ".compile-inputs.txt");
        if (!File.Exists(imagePath) || !File.Exists(inputsPath)) return false;
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(imagePath)));
        if (File.ReadLines(inputsPath).FirstOrDefault() != $"assembly|{hash}") return false;
        using var stream = File.OpenRead(imagePath);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.GetGuid(metadata.GetModuleDefinition().Mvid) == runtimeAssembly.ManifestModule.ModuleVersionId
            && AssemblyName.GetAssemblyName(imagePath).FullName == runtimeAssembly.FullName;
    }

    private sealed class ConstructionProofContext : AssemblyLoadContext, IDisposable
    {
        internal Assembly Foundation { get; }
        internal Assembly Kernel { get; }
        internal Assembly? Host { get; }

        internal ConstructionProofContext(string foundationPath, string kernelPath, string? hostPath = null) : base(isCollectible: true)
        {
            Foundation = LoadImage(foundationPath);
            Kernel = LoadImage(kernelPath);
            if (hostPath is not null) Host = LoadImage(hostPath);
        }

        private Assembly LoadImage(string imagePath)
        {
            using var stream = File.OpenRead(imagePath);
            return LoadFromStream(stream);
        }

        // The two authenticated domain images share this context. Framework APIs
        // resolve through the default context, retaining exact genuine Type identity.
        protected override Assembly? Load(AssemblyName name) => null;
        public void Dispose() => Unload();
    }

    private static bool HasSameReviewedTokens(SyntaxNode actual, SyntaxNode expected) =>
        actual.DescendantTokens().Select(token => (token.RawKind, token.Text)).SequenceEqual(
            expected.DescendantTokens().Select(token => (token.RawKind, token.Text)));

    private static bool HasReviewedOuterBody(MethodDeclarationSyntax method, InvocationExpressionSyntax atomic,
        AnonymousFunctionExpressionSyntax callback, SemanticModel model)
    {
        if (method.Body is not { Statements.Count: > 0 } body
            || body.Statements[^1] is not ReturnStatementSyntax { Expression: { } result }) return false;
        foreach (var statement in body.Statements.Take(body.Statements.Count - 1))
        {
            switch (statement)
            {
                case ExpressionStatementSyntax { Expression: InvocationExpressionSyntax call }
                    when IsReviewedPreludeCall(call, model)
                        && model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: nameof(ArgumentNullException.ThrowIfNull) }:
                    if (!IsReviewedBoundaryExpression(call, atomic, callback, model)) return false;
                    break;
                case LocalDeclarationStatementSyntax local when local.UsingKeyword.RawKind == 0
                    && local.AwaitKeyword.RawKind == 0 && local.Modifiers.Count == 0
                    && local.Declaration.Type.IsVar && local.Declaration.Variables.Count == 1
                    && local.Declaration.Variables[0].Initializer?.Value is InvocationExpressionSyntax call
                    && IsReviewedPreludeCall(call, model)
                    && model.GetSymbolInfo(call).Symbol is IMethodSymbol prelude
                    && (prelude.Name == nameof(TimeProvider.GetUtcNow) || IsAdmittedClockRead(prelude)):
                    if (!IsReviewedBoundaryExpression(call, atomic, callback, model)) return false;
                    break;
                default:
                    return false;
            }
        }
        return IsReviewedBoundaryExpression(result, atomic, callback, model);
    }

    private static bool IsReviewedBoundaryExpression(ExpressionSyntax expression, InvocationExpressionSyntax atomic,
        AnonymousFunctionExpressionSyntax callback, SemanticModel model, bool isCallbackBody = false)
    {
        if (expression == callback && !isCallbackBody) return true; // Its execution/completion is proved separately below.
        var type = model.GetTypeInfo(expression).Type;
        if (type is { TypeKind: TypeKind.Dynamic or TypeKind.Error }
            || model.GetConversion(expression).MethodSymbol is not null) return false;
        switch (expression)
        {
            case ParenthesizedExpressionSyntax parentheses:
                return IsReviewedBoundaryExpression(parentheses.Expression, atomic, callback, model, isCallbackBody);
            case PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                return IsReviewedBoundaryExpression(postfix.Operand, atomic, callback, model, isCallbackBody);
            case AwaitExpressionSyntax awaited:
                // Only the reviewed atomic task and framework completion wrappers may be awaited here.
                return IsReviewedBoundaryExpression(awaited.Expression, atomic, callback, model, isCallbackBody)
                    && model.GetTypeInfo(awaited.Expression).Type is INamedTypeSymbol task
                    && ((task.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks"
                        && task.MetadataName is "Task\u00601" or "ValueTask\u00601")
                        || (task.ContainingNamespace.ToDisplayString() == "System.Runtime.CompilerServices"
                            && task.MetadataName is "ConfiguredTaskAwaitable\u00601" or "ConfiguredValueTaskAwaitable\u00601"))
                    && task.ContainingAssembly.Identity.ToString() == typeof(Task).Assembly.FullName;
            case InvocationExpressionSyntax call:
                if (isCallbackBody
                    ? !IsExecutorCall(call, model) && !IsTaskWrapperCall(call, model)
                    : call != atomic && !IsReviewedPreludeCall(call, model) && !IsTaskWrapperCall(call, model)) return false;
                if (model.GetSymbolInfo(call).Symbol is not IMethodSymbol method) return false;
                if (call.Expression is not MemberAccessExpressionSyntax member) return false;
                if (method.IsStatic)
                {
                    if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(member.Expression).Symbol,
                        method.ContainingType)) return false;
                }
                else if (!IsReviewedBoundaryExpression(member.Expression, atomic, callback, model, isCallbackBody)) return false;
                return call.ArgumentList.Arguments.All(argument => argument.RefKindKeyword.RawKind == 0
                    && IsReviewedBoundaryExpression(argument.Expression, atomic, callback, model, isCallbackBody));
            case ObjectCreationExpressionSyntax creation when isCallbackBody:
                return IsReviewedMergeConstruction(creation, callback, model)
                    && creation.ArgumentList is { } arguments
                    && arguments.Arguments.All(argument => argument.RefKindKeyword.RawKind == 0
                        && IsReviewedBoundaryExpression(argument.Expression, atomic, callback, model, true));
            case IdentifierNameSyntax:
                return model.GetSymbolInfo(expression).Symbol is IParameterSymbol or ILocalSymbol
                    or IFieldSymbol { IsStatic: false };
            case ThisExpressionSyntax:
                return type is not null;
            case LiteralExpressionSyntax literal:
                return literal.IsKind(SyntaxKind.TrueLiteralExpression)
                    || literal.IsKind(SyntaxKind.FalseLiteralExpression)
                    || literal.IsKind(SyntaxKind.DefaultLiteralExpression);
            default:
                return false;
        }
    }

    private static bool IsMutationCall(InvocationExpressionSyntax call, SemanticModel model)
    {
        if (model.GetSymbolInfo(call).Symbol is not IMethodSymbol symbol) return false;
        static string MetadataName(INamedTypeSymbol type) => type.ContainingType is { } parent
            ? MetadataName(parent) + "+" + type.MetadataName
            : (type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace + ".") + type.MetadataName;
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
            candidate.FullName == symbol.ContainingAssembly.Identity.ToString());
        var declaring = assembly?.GetType(MetadataName(symbol.ContainingType.OriginalDefinition));
        if (declaring is null) return false;
        return declaring.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Any(target => target.Name == symbol.MetadataName
                && target.GetParameters().Length == symbol.Parameters.Length
                && (target.IsGenericMethod ? target.GetGenericArguments().Length : 0) == symbol.Arity
                && IsCommitSink(target));
    }

    private static bool CompletesExecutorBeforeCallbackReturns(
        InvocationExpressionSyntax call, SyntaxNode callback, SemanticModel model)
        => CompletesTaskInDirectReachableBody(call, callback, model);

    private static bool CompletesTaskInDirectReachableBody(
        InvocationExpressionSyntax call, SyntaxNode function, SemanticModel model)
    {
        // A task merely created inside the callback can outlive the atomic scope. Require direct await or
        // task return; accepting arbitrary assignments would need data-flow proof of their eventual await.
        ExpressionSyntax task = call;
        while (true)
        {
            if (task.Parent is ParenthesizedExpressionSyntax parentheses)
                task = parentheses;
            else if (task.Parent is MemberAccessExpressionSyntax member
                && member.Expression == task
                && member.Parent is InvocationExpressionSyntax wrapper
                && IsTaskWrapperCall(wrapper, model))
                task = wrapper;
            else break;
        }
        var awaited = task.Parent is AwaitExpressionSyntax;
        if (awaited) task = (AwaitExpressionSyntax)task.Parent!;
        while (task.Parent is ParenthesizedExpressionSyntax
            || task.Parent is PostfixUnaryExpressionSyntax postfix && postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression))
            task = (ExpressionSyntax)task.Parent;
        // Async functions must await the task. A non-async callback may return that exact task instead.
        if (!awaited && function is not AnonymousFunctionExpressionSyntax { AsyncKeyword.RawKind: 0 }) return false;
        if (function is LambdaExpressionSyntax lambda && lambda.Body == task) return true;
        var body = function switch
        {
            MethodDeclarationSyntax method => method.Body,
            LambdaExpressionSyntax { Body: BlockSyntax block } => block,
            AnonymousMethodExpressionSyntax anonymous => anonymous.Block,
            _ => null,
        };
        // Deliberately require a direct body statement. Conditional expressions, nested branches and
        // deferred functions need a separate execution proof rather than broad descendant discovery.
        var statement = task.Parent as StatementSyntax;
        if (statement?.Parent != body || body is null
            || statement is not ReturnStatementSyntax && !(awaited && statement is ExpressionStatementSyntax)) return false;
        // Reachability alone permits an opaque early return followed by a reachable decoy. Require
        // the reviewed statement on every normal path through this straight-line supported prefix.
        // Other branch/loop/exception control flow needs a separate proof before an exemption is granted.
        var prefix = body.Statements.TakeWhile(candidate => candidate != statement);
        if (prefix.SelectMany(candidate => candidate.DescendantNodesAndSelf()).Any(node =>
            node is ReturnStatementSyntax or GotoStatementSyntax or YieldStatementSyntax
                or IfStatementSyntax or SwitchStatementSyntax or ForStatementSyntax
                or ForEachStatementSyntax or ForEachVariableStatementSyntax or WhileStatementSyntax
                or DoStatementSyntax or TryStatementSyntax)) return false;
        var flow = model.AnalyzeControlFlow(statement);
        return flow is { Succeeded: true, StartPointIsReachable: true };
    }

    [Fact(DisplayName = "ck-10 fence: the record writer commits only from its KernelWrite commit stages, EF saves included")]
    public void RecordWriterCommitsOnlyFromItsCommitStages()
    {
        // The legal-entity create persists through EF, which the shared sink set does not cover, so this
        // writer's EF saves are sinks here too: a save moved out of a commit stage reds this check.
        static bool InWriter(Type type)
        {
            for (var current = type; current is not null; current = current.DeclaringType)
                if (current == typeof(Harborline.Api.LocalNodeHost.Data.Entities.NodeEntityWriter)) return true;
            return false;
        }
        static bool Sink(MethodBase target) => IsCommitSink(target)
            || (target.Name.StartsWith("SaveChanges", StringComparison.Ordinal)
                && typeof(Microsoft.EntityFrameworkCore.DbContext).IsAssignableFrom(target.DeclaringType));

        var assembly = typeof(Harborline.Api.LocalNodeHost.Data.Entities.NodeEntityWriter).Assembly;
        var stages = CommitStages([assembly]);
        var sites = RawMutationPortSymbolInventoryTests.DiscoverCalls([assembly], Sink, InWriter);

        Assert.True(sites.All(site => stages.Contains(site.Symbol)),
            "The record writer commits outside a KernelWrite commit stage:\n"
            + string.Join("\n", sites.Where(site => !stages.Contains(site.Symbol)).Select(Describe)));
        foreach (var write in new[] { "LegalEntityCreate", "RecordCreate", "RecordUpdate", "RecordDelete" })
            Assert.Contains(sites, site => site.Symbol.Contains($"NodeEntityWriter+{write}.CommitAsync(", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ck-10 fence: a planted bypass outside the writer is caught by every check")]
    public void PlantedBypassIsCaught()
    {
        var planted = new[] { typeof(PlantedBypass).Assembly };
        static bool Planted(Type type)
        {
            // An async lambda compiles to a state machine nested inside its closure class, two levels down.
            for (var current = type; current is not null; current = current.DeclaringType)
                if (current == typeof(PlantedBypass)) return true;
            return false;
        }

        var getter = typeof(WritePipeline).GetProperty(nameof(WritePipeline.Order))!.GetMethod!;
        Assert.NotEmpty(RawMutationPortSymbolInventoryTests.DiscoverCalls(planted, target => target == getter, Planted));
        Assert.Contains(StageCalls(planted, Planted), site => !IsExecutor(site) && site.Symbol.Contains(".CallsItsOwnCommit(", StringComparison.Ordinal));

        var (onExecutor, off) = Classify(planted, Planted);
        Assert.Contains(off, key => key.EndsWith(".CommitsAroundThePipeline", StringComparison.Ordinal));
        Assert.Contains(off, key => key.EndsWith(".WritesARecordAroundThePipeline", StringComparison.Ordinal));
        Assert.Contains(onExecutor, key => key.EndsWith("+StagedWrite.CommitAsync", StringComparison.Ordinal));
        // A closure lifted from a KernelWrite method other than CommitAsync is not a commit stage.
        Assert.Contains(off, key => key.Contains("<WritesInsideAScopeOutsideCommit>", StringComparison.Ordinal));
    }

    private static (string[] OnExecutor, string[] Off) Classify(Assembly[] assemblies, Func<Type, bool>? typeFilter = null)
    {
        var commitStages = CommitStages(assemblies);
        var sites = RawMutationPortSymbolInventoryTests.DiscoverCalls(assemblies, IsCommitSink, typeFilter);
        string Key(RawMutationPortSymbolInventoryTests.CallSite site) =>
            $"{site.Path}|{site.Symbol[..site.Symbol.IndexOf('(', StringComparison.Ordinal)]}";
        return (
            [.. sites.Where(site => commitStages.Contains(site.Symbol)).Select(Key).Distinct().Order(StringComparer.Ordinal)],
            [.. sites.Where(site => !commitStages.Contains(site.Symbol)).Select(Key).Distinct().Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// The commit stages, and the closures the compiler lifts out of them: a write inside a lambda written in a
    /// <c>CommitAsync</c> body (the unit of work it opens, for one) is lexically that commit stage.
    /// </summary>
    private static HashSet<string> CommitStages(Assembly[] assemblies) =>
        assemblies.SelectMany(Types)
            .Where(type => IsKernelWrite(type) || (type.DeclaringType is { } declaring && IsKernelWrite(declaring)))
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(method => method.Name == "CommitAsync" || method.Name.StartsWith("<CommitAsync>", StringComparison.Ordinal))
            .Select(MethodSignatureSymbol.Format)
            .ToHashSet(StringComparer.Ordinal);

    private static bool IsCommitSink(MethodBase target) =>
        RawMutationPortSymbolInventoryTests.IsRawMutationSink(target)
        || AuthorizationDefinitionWriteFenceTests.Sink(target) is "commit" or "conferral-commit";

    private static RawMutationPortSymbolInventoryTests.CallSite[] StageCalls(Assembly[] assemblies, Func<Type, bool>? typeFilter = null) =>
        RawMutationPortSymbolInventoryTests.DiscoverCalls(assemblies,
            target => StageNames.Contains(target.Name) && target.DeclaringType is { } declaring && IsKernelWrite(declaring),
            typeFilter);

    private static bool IsExecutor(RawMutationPortSymbolInventoryTests.CallSite site) =>
        site.Symbol.StartsWith(typeof(WritePipeline).FullName + "." + nameof(WritePipeline.RunAsync) + "``", StringComparison.Ordinal);

    private static bool IsKernelWrite(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(KernelWrite<,,,>))
                return true;
        return false;
    }

    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException exception) { return exception.Types.OfType<Type>(); }
    }

    private static string Describe(RawMutationPortSymbolInventoryTests.CallSite site) => $"{site.Path}:{site.Line} {site.Symbol}";

    /// <summary>Every production Harborline assembly the host test run loads.</summary>
    private static Assembly[] ProductionAssemblies() =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, "Harborline*.dll")
            .Where(path => !Path.GetFileName(path).Contains("Test", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.Exists(Path.ChangeExtension(path, ".pdb")))
            .Select(Assembly.LoadFrom)
            .ToArray();

    /// <summary>
    /// Test-only planted bypass: a loop of its own over the stage order, a KernelWrite that calls its own commit
    /// stage, and two commits around the pipeline. The StagedWrite is the positive control. It is never executed.
    /// </summary>
    private sealed class PlantedBypass(IAuthorizationConfigurationStore store, IEntityMutationStore entities)
    {
        public int RunsItsOwnLoop() => WritePipeline.Order.Count;

        public ValueTask CommitsAroundThePipeline(ValidatedAuthorizationConfigurationWrite write) => store.CommitAsync(write);

        public Task WritesARecordAroundThePipeline(ValidatedRecordBody body) => entities.CreateAsync(body, null!);

        public sealed class StagedWrite(IAuthorizationConfigurationStore store)
            : KernelWrite<string, string, ValidatedAuthorizationConfigurationWrite, string>
        {
            public ValueTask CallsItsOwnCommit(ValidatedAuthorizationConfigurationWrite write) => CommitAsync(write, default);
            protected override ValueTask AuthorizeAsync(CancellationToken ct) => ValueTask.CompletedTask;
            protected override ValueTask<string?> BindAsync(CancellationToken ct) => ValueTask.FromResult<string?>("");
            protected override ValueTask<string> MutateAsync(string bound, CancellationToken ct) => ValueTask.FromResult(bound);
            protected override ValueTask<ValidatedAuthorizationConfigurationWrite> ValidateAsync(string bound, string mutation, CancellationToken ct) =>
                throw new NotSupportedException();
            protected override ValueTask CommitAsync(ValidatedAuthorizationConfigurationWrite validated, CancellationToken ct) =>
                store.CommitAsync(validated, ct);

            public Task<bool> WritesInsideAScopeOutsideCommit(IHierarchyCompositeUnitOfWork unit) =>
                unit.ExecuteAtomicAsync(async ct =>
                {
                    await unit.InvalidateEdgeAsync(1, DateTimeOffset.UnixEpoch, ct);
                    return true;
                });
            protected override ValueTask<string> ReactAsync(ValidatedAuthorizationConfigurationWrite validated, CancellationToken ct) =>
                ValueTask.FromResult("");
        }
    }
}
