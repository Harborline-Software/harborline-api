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
/// ck-10 S3 (DES-0029, ADR 0038): a hierarchy reparent runs the six stages through
/// <see cref="WritePipeline.RunAsync"/> inside one atomic unit. Validate refuses a new parent that is the child
/// or one of its descendants, so the closure table never gains a cycle; every refusal leaves the edges as they were.
/// </summary>
public sealed class HierarchyReparentPipelineTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new("hierarchy-pipeline");
    private static readonly ActorId Actor = new("hierarchy-operator");
    private static readonly EntityId Root = new("entity", "test", "root");
    private static readonly EntityId Site = new("entity", "test", "site");
    private static readonly EntityId Building = new("entity", "test", "building");
    private static readonly EntityId Room = new("entity", "test", "room");

    // ADR 0038's order, written out here rather than read from WritePipeline.Order.
    private static readonly WritePipelineStage[] SixStages =
    [
        WritePipelineStage.Authorize, WritePipelineStage.Bind, WritePipelineStage.Mutate,
        WritePipelineStage.Validate, WritePipelineStage.Commit, WritePipelineStage.React,
    ];

    [Fact(DisplayName = "ck-10 S3: a reparent runs the six stages and moves the child to its new parent")]
    public async Task Reparent_RunsTheSixStages()
    {
        var h = await Harness.CreateAsync();

        await h.Coordinator.ReparentAsync(Room, Building, Site, "move", Actor, Tenant, At);

        Assert.Equal(SixStages, h.Stages);
        Assert.Equal([Site], await h.ParentsOf(Room));
    }

    [Fact(DisplayName = "ck-10 S3: a refused caller stops at authorize and the edges are unchanged")]
    public async Task Reparent_RefusedCaller_StopsAtAuthorize()
    {
        var h = await Harness.CreateAsync(allow: false);

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            h.Coordinator.ReparentAsync(Room, Building, Site, "move", Actor, Tenant, At));

        Assert.Equal([WritePipelineStage.Authorize], h.Stages);
        Assert.Equal([Building], await h.ParentsOf(Room));
    }

    [Fact(DisplayName = "ck-10 S3: an entity reparented under itself is refused at validate, with or without a closure row of its own")]
    public async Task Reparent_UnderItself_IsRefusedAtValidate()
    {
        var h = await Harness.CreateAsync();
        var loose = new EntityId("entity", "test", "loose");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Coordinator.ReparentAsync(Building, Site, Building, "loop", Actor, Tenant, At));
        Assert.Equal(WritePipelineStage.Validate, h.Stages[^1]);
        Assert.Equal([Site], await h.ParentsOf(Building));

        h.Stages.Clear();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Coordinator.ReparentAsync(loose, Root, loose, "loop", Actor, Tenant, At));
        Assert.Equal(WritePipelineStage.Validate, h.Stages[^1]);
        Assert.Empty(await h.ParentsOf(loose));
    }

    [Fact(DisplayName = "ck-10 S3: a reparent under the entity's own descendant is refused at validate; a valid retry then moves it")]
    public async Task Reparent_UnderOwnDescendant_IsRefusedAtValidate_AndAValidRetrySucceeds()
    {
        var h = await Harness.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Coordinator.ReparentAsync(Site, Root, Room, "cycle", Actor, Tenant, At));
        Assert.Equal(WritePipelineStage.Validate, h.Stages[^1]);
        Assert.Equal([Root], await h.ParentsOf(Site));

        h.Stages.Clear();
        await h.Coordinator.ReparentAsync(Room, Building, Root, "flatten", Actor, Tenant, At);
        Assert.Equal(SixStages, h.Stages);
        Assert.Equal([Root], await h.ParentsOf(Room));
    }

    private sealed class Harness : IWritePipelineObserver
    {
        private Harness(InMemoryHierarchyService hierarchy, NodeHierarchyCompositeCoordinator coordinator)
        {
            Hierarchy = hierarchy;
            Coordinator = coordinator;
        }

        public InMemoryHierarchyService Hierarchy { get; }
        public NodeHierarchyCompositeCoordinator Coordinator { get; private set; }
        public List<WritePipelineStage> Stages { get; } = [];

        public void OnStage(WritePipelineStage stage) => Stages.Add(stage);

        /// <summary>Root, then Site under Root, Building under Site and Room under Building, a day before the act.</summary>
        public static async Task<Harness> CreateAsync(bool allow = true)
        {
            var storage = new InMemoryAssetStorage();
            var hierarchy = new InMemoryHierarchyService(storage);
            await hierarchy.AddEdgeAsync(Site, Root, EdgeKind.ChildOf, At.AddDays(-1));
            await hierarchy.AddEdgeAsync(Building, Site, EdgeKind.ChildOf, At.AddDays(-1));
            await hierarchy.AddEdgeAsync(Room, Building, EdgeKind.ChildOf, At.AddDays(-1));
            var harness = new Harness(hierarchy, null!);
            harness.Coordinator = new NodeHierarchyCompositeCoordinator(
                new InMemoryEntityStore(storage, new FixedTimeProvider(At)), hierarchy,
                new HierarchyAuthorizedAuditWriter(new InMemoryAuditLog(storage)),
                TestAuthorization.Gate(allow), new FixedTimeProvider(At),
                NullEntityValidator.Instance, harness);
            return harness;
        }

        public async Task<List<EntityId>> ParentsOf(EntityId child)
        {
            var parents = new List<EntityId>();
            await foreach (var edge in Hierarchy.GetParentsAsync(child, At))
                if (edge.Kind == EdgeKind.ChildOf)
                    parents.Add(edge.To);
            return parents;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
