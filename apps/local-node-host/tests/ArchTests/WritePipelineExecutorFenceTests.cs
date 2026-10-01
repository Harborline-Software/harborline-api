using System.Reflection;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Assets.Hierarchy;
using Harborline.Api.Kernel.Runtime;

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
public sealed class WritePipelineExecutorFenceTests
{
    private static readonly string[] StageNames =
        ["AuthorizeAsync", "BindAsync", "MutateAsync", "ValidateAsync", "CommitAsync", "ReactAsync"];

    /// <summary>Admitted write paths that commit outside the executor: path | caller, and the slice that moves it.</summary>
    private static readonly (string Key, string Slice)[] NotYetOnTheExecutor =
    [
        ("apps/local-node-host/Data/Configuration/ConfigurationActivationTarget.cs|Harborline.Api.LocalNodeHost.Data.Configuration.ConfigurationActivationTarget.CompareAndSwapAsync",
            "S5 configuration activation"),
        ("apps/local-node-host/Data/Entities/NodeHierarchyCompositeCoordinator.cs|Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator.MergeAsync",
            "S6 exemption candidate: merge runs its whole pipeline inside the unit it opens, because the displaced set it decides is read there (ticket 216, review round 7); its writes are Merge.CommitAsync"),
        ("packages/blocks-workflow/src/durable/AuthorizedWorkflowDefinitionLifecycle.cs|Harborline.Api.Blocks.Workflow.Durable.AuthorizedWorkflowDefinitionLifecycle+EntityWriterBackend.RegisterAsync",
            "S3 workflow definition lifecycle"),
        ("packages/foundation-forms/AuthorizedFormDefinitionLifecycle.cs|Harborline.Api.Foundation.Forms.AuthorizedFormDefinitionLifecycle+EntityWriterBackend.RegisterAsync",
            "S3 form definition lifecycle"),
        ("packages/foundation-packs/Install/PackInstaller.cs|Harborline.Api.Foundation.Packs.Install.PackInstaller.CommitActivationAsync",
            "S5 pack activation"),
        ("packages/foundation-packs/Install/PackInstaller.cs|Harborline.Api.Foundation.Packs.Install.PackInstaller.DeactivateCore",
            "S5 pack deactivation"),
        ("packages/foundation-packs/Install/PackInstaller.cs|Harborline.Api.Foundation.Packs.Install.PackInstaller.Harborline.Api.Foundation.Packs.Install.IPackProjectionReconciler.ReconcilePending",
            "S5 pack projection reconcile"),
        ("packages/foundation-packs/Install/PackInstaller.cs|Harborline.Api.Foundation.Packs.Install.PackInstaller.Install",
            "S5 pack install"),
        ("packages/foundation-packs/Install/PackInstaller.cs|Harborline.Api.Foundation.Packs.Install.PackInstaller.Narrow",
            "S5 pack narrowing"),
        ("packages/foundation-packs/Install/PackInstaller.cs|Harborline.Api.Foundation.Packs.Install.PackInstaller.ProjectAndRetire``1[!!0]",
            "S5 pack projection"),
        ("packages/foundation/Assets/Entities/IEntityStore.cs|Harborline.Api.Foundation.Assets.Entities.IEntityMutationStore.CreateBatchAsync",
            "S3 raw-port batch fan-out, no production caller"),
        ("packages/foundation/Definitions/EntityStoreDefinitionLifecycle.cs|Harborline.Api.Foundation.Definitions.EntityStoreDefinitionLifecycle`1[!0].TransitionAsync",
            "S3 definition lifecycle"),
    ];

    internal static string[] NotYetOnTheExecutorRows() => [.. NotYetOnTheExecutor.Select(row => row.Key)];

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

        var reviewed = NotYetOnTheExecutor.Select(row => row.Key).Order(StringComparer.Ordinal).ToArray();
        Assert.True(reviewed.SequenceEqual(off, StringComparer.Ordinal),
            "ck-10 not-yet-on-the-executor inventory mismatch.\nUnreviewed (move onto WritePipeline.RunAsync, or review with a slice):\n"
            + string.Join("\n", off.Except(reviewed))
            + "\nStale (on the executor now; delete the row):\n" + string.Join("\n", reviewed.Except(off)));
        Assert.All(NotYetOnTheExecutor, row => Assert.StartsWith("S", row.Slice, StringComparison.Ordinal));

        // The authorization configuration writer and the admission conferral commit from a KernelWrite.
        Assert.Contains(onExecutor, key => key.Contains("AuthorizationDefinitionWriter+ConfigurationWrite.CommitAsync", StringComparison.Ordinal));
        Assert.Contains(onExecutor, key => key.Contains("AuthorizationDefinitionWriter+ConferralWrite.CommitAsync", StringComparison.Ordinal));
        // ck-10 S2: the generic record create, update and delete commit from a KernelWrite.
        foreach (var write in new[] { "RecordCreate", "RecordUpdate", "RecordDelete" })
            Assert.Contains(onExecutor, key => key.Contains($"NodeEntityWriter+{write}.CommitAsync", StringComparison.Ordinal));
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
