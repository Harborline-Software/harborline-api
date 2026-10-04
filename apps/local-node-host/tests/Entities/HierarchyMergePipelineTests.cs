using System.Text.Json;

using Harborline.Api.Foundation.Assets.Audit;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Assets.Hierarchy;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.LocalNodeHost.Data.Entities;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// ck-10 S3 (DES-0029, ADR 0038): a hierarchy merge runs the six stages through <see cref="WritePipeline.RunAsync"/>
/// inside its atomic unit. A displaced child the caller may not write is refused at validate, before the replacement
/// is minted or any original is removed.
/// </summary>
public sealed class HierarchyMergePipelineTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new("merge-pipeline");
    private static readonly ActorId Actor = new("merge-operator");
    private static readonly SchemaId Schema = new("merge.pipeline");
    private static readonly EntityId OldA = new("entity", "test", "old-a");
    private static readonly EntityId OldB = new("entity", "test", "old-b");
    private static readonly EntityId Kid = new("entity", "test", "kid");
    private static readonly EntityId Merged = new("entity", "test", "merged");

    // ADR 0038's order, written out here rather than read from WritePipeline.Order.
    private static readonly WritePipelineStage[] SixStages =
    [
        WritePipelineStage.Authorize, WritePipelineStage.Bind, WritePipelineStage.Mutate,
        WritePipelineStage.Validate, WritePipelineStage.Commit, WritePipelineStage.React,
    ];

    [Fact(DisplayName = "ck-10 S3: a merge runs the six stages, mints the replacement and moves the displaced child onto it")]
    public async Task Merge_RunsTheSixStages()
    {
        var h = await Harness.CreateAsync(TestAuthorization.Gate(true));

        var result = await h.MergeAsync();

        Assert.Equal(SixStages, h.Stages);
        Assert.Equal(Merged, result.NewEntity);
        Assert.Null(await h.Entities.GetAsync(OldA));
        Assert.Null(await h.Entities.GetAsync(OldB));
        Assert.Equal([Merged], await h.ParentsOf(Kid));
    }

    [Fact(DisplayName = "ck-10 S3: a displaced child the caller may not write is refused at validate; nothing is minted or removed, and an allowed retry merges")]
    public async Task Merge_AnUnwritableDisplacedChild_IsRefusedAtValidate_AndAnAllowedRetryMerges()
    {
        var h = await Harness.CreateAsync(TestRouteGate.ScopedTo("merged", "old-a", "old-b"));

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => h.MergeAsync());

        Assert.Equal(WritePipelineStage.Validate, h.Stages[^1]);
        Assert.Null(await h.Entities.GetAsync(Merged));
        Assert.NotNull(await h.Entities.GetAsync(OldA));
        Assert.Equal([OldA], await h.ParentsOf(Kid));

        var retry = h.WithGate(TestAuthorization.Gate(true));
        await retry.MergeAsync();
        Assert.Equal(SixStages, retry.Stages);
        Assert.Equal([Merged], await retry.ParentsOf(Kid));
    }

    [Fact(DisplayName = "ck-10 S3: a caller refused a named original stops the merge at authorize")]
    public async Task Merge_RefusedNamedOriginal_StopsAtAuthorize()
    {
        var h = await Harness.CreateAsync(TestRouteGate.ScopedTo("merged", "old-a", "kid"));

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => h.MergeAsync());

        Assert.Equal([WritePipelineStage.Authorize], h.Stages);
        Assert.NotNull(await h.Entities.GetAsync(OldB));
    }

    private sealed class Harness : IWritePipelineObserver
    {
        private InMemoryAssetStorage storage = null!;
        private InMemoryHierarchyService hierarchy = null!;

        public InMemoryEntityStore Entities { get; private set; } = null!;
        public NodeHierarchyCompositeCoordinator Coordinator { get; private set; } = null!;
        public List<WritePipelineStage> Stages { get; } = [];

        public void OnStage(WritePipelineStage stage) => Stages.Add(stage);

        /// <summary>Two originals, with one child under the first, a day before the act.</summary>
        public static async Task<Harness> CreateAsync(AuthorizationGate gate)
        {
            var storage = new InMemoryAssetStorage();
            var entities = new InMemoryEntityStore(storage, new FixedTimeProvider(At));
            var hierarchy = new InMemoryHierarchyService(storage);
            foreach (var original in new[] { OldA, OldB, Kid })
            {
                using var body = JsonDocument.Parse($$"""{"name":"{{original.LocalPart}}"}""");
                await entities.CreateAsync(Schema, body, Options(original.LocalPart, At.AddDays(-1)));
            }
            await hierarchy.AddEdgeAsync(Kid, OldA, EdgeKind.ChildOf, At.AddDays(-1));
            var harness = new Harness { storage = storage, hierarchy = hierarchy, Entities = entities };
            harness.Coordinator = harness.Build(gate);
            return harness;
        }

        public Harness WithGate(AuthorizationGate gate)
        {
            var harness = new Harness { storage = storage, hierarchy = hierarchy, Entities = Entities };
            harness.Coordinator = harness.Build(gate);
            return harness;
        }

        public Task<MergeResult> MergeAsync() => Coordinator.MergeAsync(
            [OldA, OldB], Schema, JsonDocument.Parse("""{"name":"merged"}"""), Options("merged", At),
            "merge", Actor, Tenant, At);

        public async Task<List<EntityId>> ParentsOf(EntityId child)
        {
            var parents = new List<EntityId>();
            await foreach (var edge in hierarchy.GetParentsAsync(child, At))
                if (edge.Kind == EdgeKind.ChildOf)
                    parents.Add(edge.To);
            return parents;
        }

        private NodeHierarchyCompositeCoordinator Build(AuthorizationGate gate) => new(
            Entities, hierarchy, new HierarchyAuthorizedAuditWriter(new InMemoryAuditLog(storage)),
            gate, new FixedTimeProvider(At), NullEntityValidator.Instance, this);
    }

    private static CreateOptions Options(string localPart, DateTimeOffset validFrom) =>
        new("entity", "test", localPart, Actor, Tenant, validFrom, ExplicitLocalPart: localPart);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
