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
/// ck-10 S3 (DES-0029, ADR 0038): a hierarchy split runs the six stages through <see cref="WritePipeline.RunAsync"/>.
/// Every replacement is admitted at validate before commit opens the atomic unit, so one refused replacement
/// stops the split before anything is written.
/// </summary>
public sealed class HierarchySplitPipelineTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new("split-pipeline");
    private static readonly ActorId Actor = new("split-operator");
    private static readonly SchemaId Accepted = new("split.accepted");
    private static readonly SchemaId Refused = new("split.refused");

    // ADR 0038's order, written out here rather than read from WritePipeline.Order.
    private static readonly WritePipelineStage[] SixStages =
    [
        WritePipelineStage.Authorize, WritePipelineStage.Bind, WritePipelineStage.Mutate,
        WritePipelineStage.Validate, WritePipelineStage.Commit, WritePipelineStage.React,
    ];

    [Fact(DisplayName = "ck-10 S3: a split runs the six stages, mints both replacements and removes the original")]
    public async Task Split_RunsTheSixStages()
    {
        var h = await Harness.CreateAsync();

        var result = await h.Coordinator.SplitAsync(
            h.Original, [Target("east", Accepted), Target("west", Accepted)], new Dictionary<EntityId, EntityId>(),
            "split", Actor, Tenant, At);

        Assert.Equal(SixStages, h.Stages);
        Assert.Null(await h.Entities.GetAsync(h.Original));
        Assert.Equal(["east", "west"], result.NewEntities.Select(id => id.LocalPart));
        foreach (var id in result.NewEntities) Assert.NotNull(await h.Entities.GetAsync(id));
    }

    [Fact(DisplayName = "ck-10 S3: a refused second replacement stops the split at validate before the first is written; a valid retry then splits")]
    public async Task Split_ARefusedReplacement_StopsAtValidate_BeforeAnyWrite_AndAValidRetrySucceeds()
    {
        var h = await Harness.CreateAsync();

        await Assert.ThrowsAsync<EntityValidationException>(() => h.Coordinator.SplitAsync(
            h.Original, [Target("east", Accepted), Target("west", Refused)], new Dictionary<EntityId, EntityId>(),
            "split", Actor, Tenant, At));
        Assert.Equal(WritePipelineStage.Validate, h.Stages[^1]);
        Assert.NotNull(await h.Entities.GetAsync(h.Original));
        Assert.Null(await h.Entities.GetAsync(new EntityId("entity", "test", "east")));

        h.Stages.Clear();
        await h.Coordinator.SplitAsync(
            h.Original, [Target("east", Accepted), Target("west", Accepted)], new Dictionary<EntityId, EntityId>(),
            "retry", Actor, Tenant, At);
        Assert.Equal(SixStages, h.Stages);
        Assert.Null(await h.Entities.GetAsync(h.Original));
    }

    [Fact(DisplayName = "ck-10 S3: a refused caller stops the split at authorize and the original survives")]
    public async Task Split_RefusedCaller_StopsAtAuthorize()
    {
        var h = await Harness.CreateAsync(allow: false);

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => h.Coordinator.SplitAsync(
            h.Original, [Target("east", Accepted)], new Dictionary<EntityId, EntityId>(), "split", Actor, Tenant, At));

        Assert.Equal([WritePipelineStage.Authorize], h.Stages);
        Assert.NotNull(await h.Entities.GetAsync(h.Original));
    }

    private static SplitTarget Target(string localPart, SchemaId schema) => new(
        schema, JsonDocument.Parse($$"""{"name":"{{localPart}}"}"""),
        new CreateOptions("entity", "test", localPart, Actor, Tenant, At, ExplicitLocalPart: localPart));

    private sealed class Harness : IWritePipelineObserver
    {
        public InMemoryEntityStore Entities { get; private init; } = null!;
        public NodeHierarchyCompositeCoordinator Coordinator { get; private set; } = null!;
        public EntityId Original { get; private init; }
        public List<WritePipelineStage> Stages { get; } = [];

        public void OnStage(WritePipelineStage stage) => Stages.Add(stage);

        public static async Task<Harness> CreateAsync(bool allow = true)
        {
            var storage = new InMemoryAssetStorage();
            var entities = new InMemoryEntityStore(storage, new FixedTimeProvider(At));
            using var body = JsonDocument.Parse("""{"name":"original"}""");
            var original = await entities.CreateAsync(Accepted, body,
                new CreateOptions("entity", "test", "original", Actor, Tenant, At.AddDays(-1), ExplicitLocalPart: "original"));
            var harness = new Harness { Entities = entities, Original = original };
            harness.Coordinator = new NodeHierarchyCompositeCoordinator(
                entities, new InMemoryHierarchyService(storage),
                new HierarchyAuthorizedAuditWriter(new InMemoryAuditLog(storage)),
                TestAuthorization.Gate(allow), new FixedTimeProvider(At), new SchemaRefusingValidator(), harness);
            return harness;
        }
    }

    private sealed class SchemaRefusingValidator : IEntityValidator
    {
        public Task ValidateAsync(SchemaId schema, JsonDocument body, CancellationToken ct = default) =>
            schema == Refused
                ? throw new EntityValidationException("refused", "split.pipeline.refused", ["/name"])
                : Task.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
