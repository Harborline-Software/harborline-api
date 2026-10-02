using System.Text.Json;

using Harborline.Api.Foundation.Assets.Audit;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Assets.Hierarchy;
using Harborline.Api.Foundation.Assets.Temporal;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.LocalNodeHost.Data.Entities;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using NSubstitute;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// ck-10 S3 (DES-0029, ADR 0038): what a split, merge or reparent leaves in the stores. Each test reads the stored
/// edges, entities and audit rows back from the real in-memory stores rather than trusting the returned result.
/// </summary>
public sealed class HierarchyCompositeWriteEffectsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset JustBefore = At.AddTicks(-1);
    private static readonly TenantId Tenant = new("hierarchy-effects");
    private static readonly TenantId OtherTenant = new("hierarchy-effects-other");
    private static readonly ActorId Actor = new("hierarchy-effects-operator");
    private static readonly SchemaId Schema = new("hierarchy.effects");

    private static readonly EntityId Original = Id("original");
    private static readonly EntityId East = Id("east");
    private static readonly EntityId West = Id("west");
    private static readonly EntityId KidA = Id("kid-a");
    private static readonly EntityId KidB = Id("kid-b");
    private static readonly EntityId KidC = Id("kid-c");
    private static readonly EntityId OldA = Id("old-a");
    private static readonly EntityId OldB = Id("old-b");
    private static readonly EntityId Merged = Id("merged");

    [Fact(DisplayName = "ck-10 S3: a split moves each reassigned child to its replacement, supersedes the original with each, deletes it and audits once")]
    public async Task Split_WithChildReassignments_WritesEveryEdgeEntityAndAuditRow()
    {
        var h = await Harness.CreateAsync(Original);
        var finiteEnd = At.AddDays(1);
        await h.Hierarchy.AddEdgeAsync(KidA, Original, EdgeKind.ChildOf, At.AddDays(-1));
        var kidB = await h.Hierarchy.AddEdgeAsync(KidB, Original, EdgeKind.ChildOf, At.AddDays(-1));
        await h.Hierarchy.InvalidateEdgeAsync(kidB.Id, finiteEnd);
        await h.Hierarchy.AddEdgeAsync(KidC, Original, EdgeKind.ChildOf, At.AddDays(-1));

        var result = await h.Coordinator.SplitAsync(
            Original, [Target("east"), Target("west")],
            new Dictionary<EntityId, EntityId> { [KidA] = East, [KidB] = West },
            "split", Actor, Tenant, At);

        var oldA = Assert.Single(await h.ParentEdges(KidA, JustBefore));
        Assert.Equal((Original, At), (oldA.To, oldA.Validity.ValidTo));
        var newA = Assert.Single(await h.ParentEdges(KidA, At));
        Assert.Equal((East, At, (DateTimeOffset?)null), (newA.To, newA.Validity.ValidFrom, newA.Validity.ValidTo));

        var oldB = Assert.Single(await h.ParentEdges(KidB, JustBefore));
        Assert.Equal((Original, At), (oldB.To, oldB.Validity.ValidTo));
        var newB = Assert.Single(await h.ParentEdges(KidB, At));
        Assert.Equal((West, At, (DateTimeOffset?)finiteEnd), (newB.To, newB.Validity.ValidFrom, newB.Validity.ValidTo));

        Assert.Equal(Original, Assert.Single(await h.ParentEdges(KidC, At)).To);

        Assert.Equal(
            [(Original, East, At), (Original, West, At)],
            h.Hierarchy.Added.Where(edge => edge.Kind == EdgeKind.SupersededBy)
                .Select(edge => (edge.From, edge.To, edge.Validity.ValidFrom)));

        Assert.Equal([KidA, KidB], result.ReassignedChildren);
        Assert.Equal([East, West], result.NewEntities);
        Assert.Null(await h.Entities.GetAsync(Original));
        Assert.NotNull(await h.Entities.GetAsync(East));
        Assert.NotNull(await h.Entities.GetAsync(West));

        var row = Assert.Single(await h.AuditRows());
        Assert.Equal((Original, Op.Split, Actor, Tenant, At, "split"),
            (row.EntityId, row.Op, row.Actor, row.Tenant, row.At, row.Justification));
        var payload = row.Payload.RootElement;
        Assert.Equal("split", payload.GetProperty("op").GetString());
        Assert.Equal(Original.ToString(), payload.GetProperty("old").GetString());
        Assert.Equal([East.ToString(), West.ToString()], Strings(payload, "newIds"));
        Assert.Equal([KidA.ToString(), KidB.ToString()], Strings(payload, "reassigned"));
    }

    [Fact(DisplayName = "ck-10 S3: a merge moves each original's child to the merged entity, supersedes and deletes each original and audits once")]
    public async Task Merge_WithDisplacedChildren_WritesEveryEdgeEntityAndAuditRow()
    {
        var h = await Harness.CreateAsync(OldA, OldB);
        await h.Hierarchy.AddEdgeAsync(KidA, OldA, EdgeKind.ChildOf, At.AddDays(-1));
        await h.Hierarchy.AddEdgeAsync(KidB, OldB, EdgeKind.ChildOf, At.AddDays(-1));

        var result = await h.MergeAsync(h.Coordinator, Options("merged", Tenant));

        foreach (var (kid, oldParent) in new[] { (KidA, OldA), (KidB, OldB) })
        {
            var ended = Assert.Single(await h.ParentEdges(kid, JustBefore));
            Assert.Equal((oldParent, At), (ended.To, ended.Validity.ValidTo));
            var moved = Assert.Single(await h.ParentEdges(kid, At));
            Assert.Equal((Merged, At, (DateTimeOffset?)null), (moved.To, moved.Validity.ValidFrom, moved.Validity.ValidTo));
        }

        Assert.Equal(
            [(OldA, Merged, At), (OldB, Merged, At)],
            h.Hierarchy.Added.Where(edge => edge.Kind == EdgeKind.SupersededBy)
                .Select(edge => (edge.From, edge.To, edge.Validity.ValidFrom)));
        Assert.Null(await h.Entities.GetAsync(OldA));
        Assert.Null(await h.Entities.GetAsync(OldB));
        Assert.NotNull(await h.Entities.GetAsync(Merged));
        Assert.Equal(Merged, result.NewEntity);
        Assert.Equal([KidA, KidB], result.ReassignedChildren);

        var row = Assert.Single(await h.AuditRows());
        Assert.Equal((Merged, Op.Merge, Actor, Tenant, At, "merge"),
            (row.EntityId, row.Op, row.Actor, row.Tenant, row.At, row.Justification));
        var payload = row.Payload.RootElement;
        Assert.Equal("merge", payload.GetProperty("op").GetString());
        Assert.Equal(Merged.ToString(), payload.GetProperty("newId").GetString());
        Assert.Equal([OldA.ToString(), OldB.ToString()], Strings(payload, "oldIds"));
        Assert.Equal([KidA.ToString(), KidB.ToString()], Strings(payload, "reassigned"));
    }

    [Fact(DisplayName = "ck-10 S3: merging a record with its own child dissolves the edge between them and reparents only the outside children")]
    public async Task Merge_OfARecordWithItsOwnChild_ReparentsOnlyOutsideChildren()
    {
        var h = await Harness.CreateAsync(OldA, OldB);
        await h.Hierarchy.AddEdgeAsync(OldB, OldA, EdgeKind.ChildOf, At.AddDays(-1));
        await h.Hierarchy.AddEdgeAsync(KidA, OldA, EdgeKind.ChildOf, At.AddDays(-1));

        var result = await h.MergeAsync(h.Coordinator, Options("merged", Tenant));

        // Oracle: the internal ChildOf edge ends at merge, while its history and supersession remain.
        Assert.DoesNotContain(await h.ParentEdges(OldB, At), edge => edge.Kind == EdgeKind.ChildOf);
        var internalEdge = Assert.Single(await h.ParentEdges(OldB, JustBefore));
        Assert.Equal((OldA, At), (internalEdge.To, internalEdge.Validity.ValidTo));
        await foreach (var ancestor in h.Hierarchy.GetAncestorsAsync(OldB, At))
            Assert.Equal(0, ancestor.Depth);
        // GetParentsAsync exposes ChildOf edges only; inspect the distinct supersession write itself.
        var supersession = Assert.Single(h.Hierarchy.Added, edge =>
            edge.From == OldB && edge.Kind == EdgeKind.SupersededBy);
        Assert.Equal((Merged, At, (DateTimeOffset?)null),
            (supersession.To, supersession.Validity.ValidFrom, supersession.Validity.ValidTo));
        Assert.Equal([KidA], result.ReassignedChildren);
        Assert.Equal(Merged, Assert.Single(await h.ParentEdges(KidA, At)).To);
        Assert.Null(await h.Entities.GetAsync(OldB));
        var payload = Assert.Single(await h.AuditRows()).Payload.RootElement;
        Assert.Equal([KidA.ToString()], Strings(payload, "reassigned"));
    }

    [Fact(DisplayName = "ck-10 S3: a reparent writes one reparent audit row naming the child and both parents")]
    public async Task Reparent_WritesOneAuditRow()
    {
        var h = await Harness.CreateAsync();
        await h.Hierarchy.AddEdgeAsync(KidA, OldA, EdgeKind.ChildOf, At.AddDays(-1));

        await h.Coordinator.ReparentAsync(KidA, OldA, OldB, "move", Actor, Tenant, At);

        var row = Assert.Single(await h.AuditRows());
        Assert.Equal((KidA, Op.Reparent, Actor, Tenant, At, "move"),
            (row.EntityId, row.Op, row.Actor, row.Tenant, row.At, row.Justification));
        var payload = row.Payload.RootElement;
        Assert.Equal("reparent", payload.GetProperty("op").GetString());
        Assert.Equal(KidA.ToString(), payload.GetProperty("child").GetString());
        Assert.Equal(OldA.ToString(), payload.GetProperty("oldParent").GetString());
        Assert.Equal(OldB.ToString(), payload.GetProperty("newParent").GetString());
    }

    [Fact(DisplayName = "ck-10 S3: two displaced edges that end at different instants give a replacement that ends at the later one")]
    public async Task Reparent_TwoFiniteDisplacedEdges_ReplacementEndsAtTheLater()
    {
        var h = await Harness.CreateAsync();
        var earlier = At.AddDays(1);
        var later = At.AddDays(3);
        var first = await h.Hierarchy.AddEdgeAsync(KidA, OldA, EdgeKind.ChildOf, At.AddDays(-1));
        await h.Hierarchy.InvalidateEdgeAsync(first.Id, later);
        var second = await h.Hierarchy.AddEdgeAsync(KidA, OldA, EdgeKind.ChildOf, At.AddDays(-1));
        await h.Hierarchy.InvalidateEdgeAsync(second.Id, earlier);

        await h.Coordinator.ReparentAsync(KidA, OldA, OldB, "move", Actor, Tenant, At);

        var replacement = Assert.Single(await h.ParentEdges(KidA, At));
        Assert.Equal((OldB, At, (DateTimeOffset?)later),
            (replacement.To, replacement.Validity.ValidFrom, replacement.Validity.ValidTo));
    }

    [Fact(DisplayName = "ck-10 S3: one finite and one open-ended displaced edge give an open-ended replacement")]
    public async Task Reparent_FiniteAndOpenDisplacedEdges_ReplacementIsOpenEnded()
    {
        var h = await Harness.CreateAsync();
        var finite = await h.Hierarchy.AddEdgeAsync(KidA, OldA, EdgeKind.ChildOf, At.AddDays(-1));
        await h.Hierarchy.InvalidateEdgeAsync(finite.Id, At.AddDays(1));
        await h.Hierarchy.AddEdgeAsync(KidA, OldA, EdgeKind.ChildOf, At.AddDays(-1));

        await h.Coordinator.ReparentAsync(KidA, OldA, OldB, "move", Actor, Tenant, At);

        var replacement = Assert.Single(await h.ParentEdges(KidA, At));
        Assert.Equal((OldB, At, (DateTimeOffset?)null),
            (replacement.To, replacement.Validity.ValidFrom, replacement.Validity.ValidTo));
    }

    [Fact(DisplayName = "ck-10 S3: a split target in another tenant is refused and nothing is written")]
    public async Task Split_TargetInAnotherTenant_IsRefusedAndWritesNothing()
    {
        var h = await Harness.CreateAsync(Original);
        await h.Hierarchy.AddEdgeAsync(KidA, Original, EdgeKind.ChildOf, At.AddDays(-1));

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => h.Coordinator.SplitAsync(
            Original, [Target("east"), Target("west", OtherTenant)],
            new Dictionary<EntityId, EntityId> { [KidA] = East },
            "split", Actor, Tenant, At));

        Assert.Equal("newEntities", refused.ParamName);
        await h.AssertNothingWrittenAsync([Original], East, West);
        Assert.Equal(Original, Assert.Single(await h.ParentEdges(KidA, At)).To);
    }

    [Fact(DisplayName = "ck-10 S3: a merge target in another tenant is refused and nothing is written")]
    public async Task Merge_TargetInAnotherTenant_IsRefusedAndWritesNothing()
    {
        var h = await Harness.CreateAsync(OldA, OldB);
        await h.Hierarchy.AddEdgeAsync(KidA, OldA, EdgeKind.ChildOf, At.AddDays(-1));

        var refused = await Assert.ThrowsAsync<ArgumentException>(() =>
            h.MergeAsync(h.Coordinator, Options("merged", OtherTenant)));

        Assert.Equal("newOptions", refused.ParamName);
        await h.AssertNothingWrittenAsync([OldA, OldB], Merged);
        Assert.Equal(OldA, Assert.Single(await h.ParentEdges(KidA, At)).To);
    }

    [Fact(DisplayName = "ck-10 S3: an entity store that mints an id other than the pre-authorized one fails the merge inside the unit and nothing persists")]
    public async Task Merge_StoreMintsAnUnexpectedId_ThrowsInsideTheUnitAndPersistsNothing()
    {
        var h = await Harness.CreateAsync(OldA, OldB);
        await h.Hierarchy.AddEdgeAsync(KidA, OldA, EdgeKind.ChildOf, At.AddDays(-1));
        var rogue = Id("rogue");
        var mintsRogue = Substitute.For<IEntityMutationStore>();
        mintsRogue.CreateAsync(Arg.Any<ValidatedRecordBody>(), Arg.Any<CreateOptions>(), Arg.Any<CancellationToken>())
            .Returns(call => h.Entities.CreateAsync(
                call.Arg<ValidatedRecordBody>(),
                call.Arg<CreateOptions>() with { Nonce = "rogue", ExplicitLocalPart = "rogue" },
                call.Arg<CancellationToken>()));

        // The commit-time Require(newId) would also throw for the rogue id; the message shows the minted-id check fired first.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.MergeAsync(h.CoordinatorOver(mintsRogue), Options("merged", Tenant)));
        Assert.Contains("minted an id different from the pre-authorized merge target", refused.Message, StringComparison.Ordinal);

        Assert.Null(await h.Entities.GetAsync(rogue));
        await h.AssertNothingWrittenAsync([OldA, OldB], Merged);
        Assert.Equal(OldA, Assert.Single(await h.ParentEdges(KidA, At)).To);
    }

    [Fact(DisplayName = "ck-10 S3: a null replacement list or reassignment map fails the split before any stage")]
    public async Task Split_NullArguments_ThrowBeforeAnyStage()
    {
        var h = await Harness.CreateAsync(Original);

        var noTargets = await Assert.ThrowsAsync<ArgumentNullException>(() => h.Coordinator.SplitAsync(
            Original, null!, new Dictionary<EntityId, EntityId>(), "split", Actor, Tenant, At));
        Assert.Equal("newEntities", noTargets.ParamName);
        var noMap = await Assert.ThrowsAsync<ArgumentNullException>(() => h.Coordinator.SplitAsync(
            Original, [Target("east")], null!, "split", Actor, Tenant, At));
        Assert.Equal("childReassignments", noMap.ParamName);

        Assert.Empty(h.Stages);
        await h.AssertNothingWrittenAsync([Original], East);
    }

    [Fact(DisplayName = "ck-10 S3: a null original list or body fails the merge before any stage")]
    public async Task Merge_NullArguments_ThrowBeforeAnyStage()
    {
        var h = await Harness.CreateAsync(OldA, OldB);

        var noOriginals = await Assert.ThrowsAsync<ArgumentNullException>(() => h.Coordinator.MergeAsync(
            null!, Schema, JsonDocument.Parse("""{"name":"merged"}"""), Options("merged", Tenant),
            "merge", Actor, Tenant, At));
        Assert.Equal("oldEntities", noOriginals.ParamName);
        var noBody = await Assert.ThrowsAsync<ArgumentNullException>(() => h.Coordinator.MergeAsync(
            [OldA, OldB], Schema, null!, Options("merged", Tenant), "merge", Actor, Tenant, At));
        Assert.Equal("newBody", noBody.ParamName);

        Assert.Empty(h.Stages);
        await h.AssertNothingWrittenAsync([OldA, OldB], Merged);
    }

    [Fact(DisplayName = "ck-10 S3: the hierarchy audit writer refuses a decision for another record and appends nothing")]
    public async Task AuditWriter_DecisionForAnotherRecord_IsRefused()
    {
        var log = new InMemoryAuditLog(new InMemoryAssetStorage());
        var writer = new HierarchyAuthorizedAuditWriter(log);
        var decision = TestAuthorization.AllowedDecision(Tenant, "another-record", principal: Actor.Value, at: At);

        await Assert.ThrowsAsync<ArgumentException>(() => writer.AppendAsync(Append(KidA), decision));

        Assert.Empty(await Rows(log));
    }

    [Fact(DisplayName = "ck-10 S3: the hierarchy audit writer refuses a decision made for a different principal and appends nothing")]
    public async Task AuditWriter_DecisionForAnotherPrincipal_IsDenied()
    {
        var log = new InMemoryAuditLog(new InMemoryAssetStorage());
        var writer = new HierarchyAuthorizedAuditWriter(log);
        var decision = TestAuthorization.AllowedDecision(Tenant, KidA.LocalPart, principal: "someone-else", at: At);

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => writer.AppendAsync(Append(KidA), decision));

        Assert.Empty(await Rows(log));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ChangedDisplacedEdges_RefuseCommitAndWriteNothing(bool split, bool addEdge)
    {
        var h = await Harness.CreateAsync(Original);
        var displaced = await h.Hierarchy.AddEdgeAsync(KidA, Original, EdgeKind.ChildOf, At.AddDays(-1));
        h.Hierarchy.BeforeAtomic = async () =>
        {
            if (addEdge)
                await h.Hierarchy.AddEdgeAsync(KidA, Original, EdgeKind.ChildOf, At);
            else
                await h.Hierarchy.InvalidateEdgeAsync(displaced.Id, At);
            h.Hierarchy.Added.Clear();
        };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (split)
                await h.Coordinator.SplitAsync(Original, [Target("east")],
                    new Dictionary<EntityId, EntityId> { [KidA] = East }, "split", Actor, Tenant, At);
            else
                await h.Coordinator.ReparentAsync(KidA, Original, East, "move", Actor, Tenant, At);
        });

        // Oracle: a stale bind must preserve the intervening write and produce no composite effects.
        await h.AssertNothingWrittenAsync([Original], East);
        Assert.Equal(addEdge ? 2 : 0, (await h.ParentEdges(KidA, At)).Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task CompetingReparent_RefusesStaleCommitAndPreservesFirstReplacement(int admissionDays)
    {
        var h = await Harness.CreateAsync();
        await h.Hierarchy.AddEdgeAsync(KidA, OldA, EdgeKind.ChildOf, At.AddDays(-1));
        var admittedAt = At.AddDays(admissionDays);
        var first = h.CoordinatorOver(h.Entities, admittedAt);
        h.Hierarchy.BeforeAtomic = () =>
            first.ReparentAsync(KidA, OldA, OldB, "first", Actor, Tenant, admittedAt);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Coordinator.ReparentAsync(KidA, OldA, East, "stale", Actor, Tenant, At));

        // Oracle: only the first committed move and its audit row survive.
        Assert.Equal(OldB, Assert.Single(await h.ParentEdges(KidA, admittedAt)).To);
        if (admissionDays != 0)
            Assert.Equal(OldA, Assert.Single(await h.ParentEdges(KidA, At)).To);
        var audit = Assert.Single(await h.AuditRows());
        Assert.Equal("first", audit.Justification);
        Assert.Equal(admittedAt, audit.At);
    }

    [Fact]
    public async Task SplitAfterLaterAdmittedReparent_RefusesChangedIntervalBeforeMintingAnyReplacement()
    {
        var h = await Harness.CreateAsync(Original);
        await h.Hierarchy.AddEdgeAsync(KidA, Original, EdgeKind.ChildOf, At.AddDays(-1));
        var later = h.CoordinatorOver(h.Entities, At.AddDays(1));
        h.Hierarchy.BeforeAtomic = () =>
            later.ReparentAsync(KidA, Original, OldB, "later-first", Actor, Tenant, At.AddDays(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Coordinator.SplitAsync(
            Original, [Target("east")], new Dictionary<EntityId, EntityId> { [KidA] = East },
            "stale-split", Actor, Tenant, At));

        // Oracle: the later move preserves the earlier parent history; stale split mints/deletes/audits nothing.
        Assert.NotNull(await h.Entities.GetAsync(Original));
        Assert.Null(await h.Entities.GetAsync(East));
        Assert.Equal(Original, Assert.Single(await h.ParentEdges(KidA, At)).To);
        Assert.Equal(OldB, Assert.Single(await h.ParentEdges(KidA, At.AddDays(1))).To);
        Assert.Equal("later-first", Assert.Single(await h.AuditRows()).Justification);
    }

    private static EntityId Id(string localPart) => new("entity", "test", localPart);

    [Fact]
    public async Task OpposingReparentAtLaterInstant_RefusesEarlierMoveAtCommit()
    {
        var h = await Harness.CreateAsync();
        await h.Hierarchy.AddEdgeAsync(KidA, OldA, EdgeKind.ChildOf, At.AddDays(-1));
        await h.Hierarchy.AddEdgeAsync(OldB, East, EdgeKind.ChildOf, At.AddDays(-1));
        var later = h.CoordinatorOver(h.Entities, At.AddDays(1));
        h.Hierarchy.BeforeAtomic = () =>
            later.ReparentAsync(OldB, East, KidA, "later-first", Actor, Tenant, At.AddDays(1));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Coordinator.ReparentAsync(KidA, OldA, OldB, "earlier-second", Actor, Tenant, At));

        // Oracle: the committed later move survives, while the overlapping reverse move writes nothing.
        Assert.Equal(OldA, Assert.Single(await h.ParentEdges(KidA, At.AddDays(1))).To);
        Assert.Equal(KidA, Assert.Single(await h.ParentEdges(OldB, At.AddDays(1))).To);
        var audit = Assert.Single(await h.AuditRows());
        Assert.Equal("later-first", audit.Justification);
        Assert.Equal(At.AddDays(1), audit.At);
    }

    [Fact]
    public async Task ReparentWithNonoverlappingFutureReverseEdge_DoesNotRefuse()
    {
        var h = await Harness.CreateAsync();
        var displaced = await h.Hierarchy.AddEdgeAsync(KidA, OldA, EdgeKind.ChildOf, At.AddDays(-1));
        await h.Hierarchy.InvalidateEdgeAsync(displaced.Id, At.AddDays(1));
        await h.Hierarchy.AddEdgeAsync(OldB, KidA, EdgeKind.ChildOf, At.AddDays(1));

        await h.Coordinator.ReparentAsync(KidA, OldA, OldB, "nonoverlapping", Actor, Tenant, At);

        // Oracle: half-open edge intervals touch at the boundary without forming a temporal cycle.
        Assert.Equal(OldB, Assert.Single(await h.ParentEdges(KidA, At)).To);
        Assert.Empty(await h.ParentEdges(KidA, At.AddDays(1)));
        Assert.Equal(KidA, Assert.Single(await h.ParentEdges(OldB, At.AddDays(1))).To);
        Assert.Equal("nonoverlapping", Assert.Single(await h.AuditRows()).Justification);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FutureMultihopDescendant_RefusesOnlyWhenAllPathIntervalsOverlap(bool overlap)
    {
        var h = await Harness.CreateAsync();
        await h.Hierarchy.AddEdgeAsync(KidA, OldA, EdgeKind.ChildOf, At.AddDays(-1));
        var first = await h.Hierarchy.AddEdgeAsync(KidB, KidA, EdgeKind.ChildOf, At.AddDays(1));
        await h.Hierarchy.InvalidateEdgeAsync(first.Id, At.AddDays(2));
        await h.Hierarchy.AddEdgeAsync(OldB, KidB, EdgeKind.ChildOf,
            overlap ? At.AddHours(36) : At.AddDays(2));

        var move = () => h.Coordinator.ReparentAsync(KidA, OldA, OldB, "multihop", Actor, Tenant, At);
        if (overlap)
        {
            await Assert.ThrowsAsync<ArgumentException>(move);
            Assert.Equal(OldA, Assert.Single(await h.ParentEdges(KidA, At)).To);
            Assert.Empty(await h.AuditRows());
        }
        else
        {
            await move();
            Assert.Equal(OldB, Assert.Single(await h.ParentEdges(KidA, At)).To);
            Assert.Equal("multihop", Assert.Single(await h.AuditRows()).Justification);
        }
        // Oracle: each leg separately overlaps the open-ended new edge; only the literal common
        // interval [At+36h, At+48h) forms a cycle. Touching legs have no simultaneous descendant path.
        Assert.Equal(KidA, Assert.Single(await h.ParentEdges(KidB, At.AddHours(36))).To);
        Assert.Empty(await h.ParentEdges(KidB, At.AddDays(2)));
        Assert.Equal(KidB, Assert.Single(await h.ParentEdges(OldB, At.AddDays(2))).To);
    }

    private static CreateOptions Options(string localPart, TenantId tenant) =>
        new("entity", "test", localPart, Actor, tenant, At, ExplicitLocalPart: localPart);

    private static SplitTarget Target(string localPart, TenantId? tenant = null) => new(
        Schema, JsonDocument.Parse($$"""{"name":"{{localPart}}"}"""), Options(localPart, tenant ?? Tenant));

    private static AuditAppend Append(EntityId entity) =>
        new(entity, null, Op.Reparent, Actor, Tenant, At, JsonDocument.Parse("""{"op":"reparent"}"""), "move");

    private static string[] Strings(JsonElement payload, string property) =>
        [.. payload.GetProperty(property).EnumerateArray().Select(item => item.GetString()!)];

    private static async Task<List<AuditRecord>> Rows(InMemoryAuditLog log)
    {
        var rows = new List<AuditRecord>();
        await foreach (var row in log.QueryAsync(new AuditQuery())) rows.Add(row);
        return rows;
    }

    private sealed class Harness : IWritePipelineObserver
    {
        private InMemoryAssetStorage storage = null!;

        public InMemoryEntityStore Entities { get; private init; } = null!;
        public RecordingHierarchy Hierarchy { get; private init; } = null!;
        public InMemoryAuditLog Audit { get; private init; } = null!;
        public NodeHierarchyCompositeCoordinator Coordinator { get; private set; } = null!;
        public List<WritePipelineStage> Stages { get; } = [];

        public void OnStage(WritePipelineStage stage) => Stages.Add(stage);

        /// <summary>Real stores sharing one asset storage, with each named entity created a day before the act.</summary>
        public static async Task<Harness> CreateAsync(params EntityId[] existing)
        {
            var storage = new InMemoryAssetStorage();
            var entities = new InMemoryEntityStore(storage, new FixedTimeProvider(At));
            foreach (var id in existing)
            {
                using var body = JsonDocument.Parse($$"""{"name":"{{id.LocalPart}}"}""");
                await entities.CreateAsync(Schema, body, Options(id.LocalPart, Tenant) with { ValidFrom = At.AddDays(-1) });
            }
            var harness = new Harness
            {
                storage = storage,
                Entities = entities,
                Hierarchy = new RecordingHierarchy(new InMemoryHierarchyService(storage)),
                Audit = new InMemoryAuditLog(storage),
            };
            harness.Coordinator = harness.CoordinatorOver(entities);
            return harness;
        }

        public NodeHierarchyCompositeCoordinator CoordinatorOver(IEntityMutationStore entities, DateTimeOffset? admittedAt = null) => new(
            entities, Hierarchy, new HierarchyAuthorizedAuditWriter(Audit),
            TestAuthorization.Gate(true), new FixedTimeProvider(admittedAt ?? At), NullEntityValidator.Instance, this);

        public Task<MergeResult> MergeAsync(NodeHierarchyCompositeCoordinator coordinator, CreateOptions options) =>
            coordinator.MergeAsync(
                [OldA, OldB], Schema, JsonDocument.Parse("""{"name":"merged"}"""), options, "merge", Actor, Tenant, At);

        public async Task<List<EntityEdge>> ParentEdges(EntityId child, DateTimeOffset asOf)
        {
            var edges = new List<EntityEdge>();
            await foreach (var edge in Hierarchy.GetParentsAsync(child, asOf)) edges.Add(edge);
            return edges;
        }

        public Task<List<AuditRecord>> AuditRows() => Rows(Audit);

        /// <summary>The originals survive, no replacement exists, no edge was added at the act and no audit row exists.</summary>
        public async Task AssertNothingWrittenAsync(EntityId[] originals, params EntityId[] replacements)
        {
            foreach (var id in originals) Assert.NotNull(await Entities.GetAsync(id));
            foreach (var id in replacements) Assert.Null(await Entities.GetAsync(id));
            Assert.DoesNotContain(Hierarchy.Added, edge => edge.Validity.ValidFrom == At);
            Assert.Empty(await AuditRows());
        }
    }

    /// <summary>The real hierarchy store, also keeping each edge it returned from an add.</summary>
    private sealed class RecordingHierarchy(InMemoryHierarchyService inner) : IHierarchyCompositeUnitOfWork
    {
        public List<EntityEdge> Added { get; } = [];
        public Func<Task>? BeforeAtomic { get; set; }

        public async Task<T> ExecuteAtomicAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
        {
            var beforeAtomic = BeforeAtomic;
            BeforeAtomic = null;
            if (beforeAtomic is not null) await beforeAtomic();
            return await inner.ExecuteAtomicAsync(action, ct);
        }

        public async Task<EntityEdge> AddEdgeAsync(
            EntityId from, EntityId to, EdgeKind kind, DateTimeOffset validFrom,
            JsonDocument? metadata = null, CancellationToken ct = default)
        {
            var edge = await inner.AddEdgeAsync(from, to, kind, validFrom, metadata, ct);
            Added.Add(edge);
            return edge;
        }

        public Task InvalidateEdgeAsync(long edgeId, DateTimeOffset validTo, CancellationToken ct = default) =>
            inner.InvalidateEdgeAsync(edgeId, validTo, ct);

        public IAsyncEnumerable<EntityEdge> GetChildrenAsync(EntityId parent, DateTimeOffset? asOf = null, CancellationToken ct = default) =>
            inner.GetChildrenAsync(parent, asOf, ct);

        public IAsyncEnumerable<EntityEdge> GetChildrenNotEndedAsync(EntityId parent, DateTimeOffset asOf, CancellationToken ct = default) =>
            inner.GetChildrenNotEndedAsync(parent, asOf, ct);

        public IAsyncEnumerable<EntityEdge> GetParentsAsync(EntityId child, DateTimeOffset? asOf = null, CancellationToken ct = default) =>
            inner.GetParentsAsync(child, asOf, ct);

        public IAsyncEnumerable<ClosureEntry> GetAncestorsAsync(EntityId descendant, DateTimeOffset? asOf = null, CancellationToken ct = default) =>
            inner.GetAncestorsAsync(descendant, asOf, ct);

        public IAsyncEnumerable<ClosureEntry> GetDescendantsAsync(EntityId ancestor, DateTimeOffset? asOf = null, CancellationToken ct = default) =>
            inner.GetDescendantsAsync(ancestor, asOf, ct);

        public Task<TemporalSnapshot> GetSubtreeAsync(EntityId root, DateTimeOffset? asOf = null, CancellationToken ct = default) =>
            inner.GetSubtreeAsync(root, asOf, ct);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
