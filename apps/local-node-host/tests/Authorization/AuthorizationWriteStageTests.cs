using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Blocks.Banking.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Assets.Hierarchy;
using Harborline.Api.Foundation.Assets.Temporal;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Assets.Audit;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Compatibility;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Foundation.Recovery.Crypto;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Entities;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

public sealed class AuthorizationWriteStageTests
{
    [Fact]
    public async Task NodeEntityWriter_DenialPerformsNoLoadValidationMutationOrSave()
    {
        var store = Substitute.For<IEntityMutationStore>();
        var validator = Substitute.For<IEntityValidator>();
        var admission = new EntityBodyAdmission(validator);
        var factory = Substitute.For<IDbContextFactory<LocalNodeDbContext>>();
        var authority = Authority("entity", "direct", "/records/direct");
        using var body = JsonDocument.Parse("{}");
        var options = new CreateOptions("entity", "test", "direct", authority.Principal, authority.Tenant);
        var denied = new NodeEntityWriter(factory, store, admission, TestNodeRecordSchemas.Fresh(), TestAuthorization.Gate(false));

        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
            await denied.CreateAsync("schema", body, options, authority));
        Assert.Empty(store.ReceivedCalls());
        Assert.Empty(validator.ReceivedCalls());
        Assert.Empty(factory.ReceivedCalls());

        var order = new List<string>();
        DateTimeOffset? storedAt = null;
        store.CreateAsync(default!, default!, default).ReturnsForAnyArgs(call =>
        {
            order.Add("store");
            storedAt = call.ArgAt<CreateOptions>(1).ValidFrom;
            return Task.FromResult(new EntityId("entity", "test", "direct"));
        });
        var allowed = new NodeEntityWriter(factory, store, admission, TestNodeRecordSchemas.Fresh(),
            TestAuthorization.Gate(true, request =>
            {
                order.Add("gate");
                Assert.Equal(authority.At, request.At);
            }));
        await allowed.CreateAsync("schema", body, options, authority);
        Assert.Equal(["gate", "store"], order);
        Assert.Equal(authority.At, storedAt);
    }

    [Fact]
    public async Task NodeEntityWriter_EveryEntryPointDeniesBeforeValidationStoreOrDb()
    {
        var store = Substitute.For<IEntityMutationStore>();
        var validator = Substitute.For<IEntityValidator>();
        var admission = new EntityBodyAdmission(validator);
        var factory = Substitute.For<IDbContextFactory<LocalNodeDbContext>>();
        var writer = new NodeEntityWriter(factory, store, admission, TestNodeRecordSchemas.Fresh(), TestAuthorization.Gate(false));
        var authority = Authority("record", "denied", "/records/denied");
        using var body = JsonDocument.Parse("{}");

        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
            await writer.AuthorizeAsync("denied", authority.Principal, authority.Tenant, authority.At));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
            await writer.CreateLegalEntityAsync(
                new CreateLegalEntityCommand(new LegalEntityId("denied"), null, null, null, null), authority));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
            await writer.UpdateAsync(new EntityId("entity", "test", "denied"), body,
                new UpdateOptions(authority.Principal, ValidFrom: authority.At), authority));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
            await writer.DeleteAsync(new EntityId("entity", "test", "denied"),
                new DeleteOptions(authority.Principal, authority.At, "denied"), authority));

        Assert.Empty(store.ReceivedCalls());
        Assert.Empty(validator.ReceivedCalls());
        Assert.Empty(factory.ReceivedCalls());
    }

    [Fact]
    public void HierarchyOperations_OwnsNoRawMutationPort()
    {
        var source = Read("packages/foundation/Assets/Hierarchy/HierarchyOperations.cs");
        Assert.Contains("IHierarchyCompositeCoordinator", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IEntityMutationStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IHierarchyMutationStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IHierarchyCompositeUnitOfWork", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HierarchySplit_UsesOneDecisionAndCommitsEntityEdgeAndAuditTogether()
    {
        var at = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var tenant = new TenantId("hierarchy-atomic");
        var actor = new ActorId("operator");
        var storage = new InMemoryAssetStorage();
        var entities = new InMemoryEntityStore(storage, new FixedTimeProvider(at));
        var hierarchy = new Harborline.Api.Foundation.Assets.Hierarchy.InMemoryHierarchyService(storage);
        var audit = new InMemoryAuditLog(storage);
        var decisions = 0;
        var coordinator = new NodeHierarchyCompositeCoordinator(
            entities, TestEntityWritePipeline.Accepting, hierarchy, new HierarchyAuthorizedAuditWriter(audit),
            TestAuthorization.Gate(true, _ => decisions++), new FixedTimeProvider(at));
        using var oldBody = JsonDocument.Parse("{\"name\":\"old\"}");
        using var firstBody = JsonDocument.Parse("{\"name\":\"first\"}");
        using var secondBody = JsonDocument.Parse("{\"name\":\"second\"}");
        var oldId = await entities.CreateAsync(new SchemaId("schema"), oldBody,
            Options("old", actor, tenant, at));

        var result = await coordinator.SplitAsync(oldId,
        [
            new Harborline.Api.Foundation.Assets.Hierarchy.SplitTarget(new SchemaId("schema"), firstBody, Options("first", actor, tenant, at)),
            new Harborline.Api.Foundation.Assets.Hierarchy.SplitTarget(new SchemaId("schema"), secondBody, Options("second", actor, tenant, at)),
        ], new Dictionary<EntityId, EntityId>(), "split", actor, tenant, at);

        Assert.Equal(3, decisions);
        Assert.Null(await entities.GetAsync(oldId));
        Assert.Equal(2, result.NewEntities.Count);
        foreach (var id in result.NewEntities) Assert.NotNull(await entities.GetAsync(id));
        var records = new List<Harborline.Api.Foundation.Assets.Audit.AuditRecord>();
        await foreach (var record in audit.QueryAsync(new Harborline.Api.Foundation.Assets.Audit.AuditQuery(Entity: oldId))) records.Add(record);
        Assert.Single(records);
    }

    [Fact]
    public async Task HierarchySplit_SamplesClockOnceForEveryDecisionAndUnitOfWorkTimestamp()
    {
        var at = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);
        var requestedAt = at.AddDays(-1);
        var tenant = new TenantId("hierarchy-one-clock");
        var actor = new ActorId("operator");
        var storage = new InMemoryAssetStorage();
        var entities = new InMemoryEntityStore(storage, new FixedTimeProvider(requestedAt));
        var hierarchy = new InMemoryHierarchyService(storage);
        var audit = new InMemoryAuditLog(storage);
        var clock = new AdvancingTimeProvider(at);
        var requests = new List<AuthorizationGateRequest>();
        var coordinator = new NodeHierarchyCompositeCoordinator(
            entities, TestEntityWritePipeline.Accepting, hierarchy, new HierarchyAuthorizedAuditWriter(audit),
            TestAuthorization.Gate(true, requests.Add), clock);
        using var oldBody = JsonDocument.Parse("{\"name\":\"old\"}");
        using var firstBody = JsonDocument.Parse("{\"name\":\"first\"}");
        using var secondBody = JsonDocument.Parse("{\"name\":\"second\"}");
        var oldId = await entities.CreateAsync(
            new SchemaId("schema"), oldBody, Options("clock-old", actor, tenant, requestedAt));

        var result = await coordinator.SplitAsync(oldId,
        [
            new SplitTarget(new SchemaId("schema"), firstBody, Options("clock-first", actor, tenant, requestedAt)),
            new SplitTarget(new SchemaId("schema"), secondBody, Options("clock-second", actor, tenant, requestedAt)),
        ], new Dictionary<EntityId, EntityId>(), "split", actor, tenant, requestedAt);

        Assert.Equal(1, clock.ReadCount);
        Assert.Equal(3, requests.Count);
        Assert.All(requests, request => Assert.Equal(at, request.At));
        Assert.All(result.NewEntities, id => Assert.Equal(at, storage.Entities[id].CreatedAt));
        Assert.Equal(at, storage.Entities[oldId].DeletedAt);
        var records = new List<Harborline.Api.Foundation.Assets.Audit.AuditRecord>();
        await foreach (var record in audit.QueryAsync(
                           new Harborline.Api.Foundation.Assets.Audit.AuditQuery(Entity: oldId)))
            records.Add(record);
        Assert.Equal(at, Assert.Single(records).At);
    }

    [Fact]
    public async Task HierarchyMerge_SnapshotsAndAdmitsConcurrentChildInsideAtomicScope()
    {
        var at = new DateTimeOffset(2026, 9, 3, 11, 0, 0, TimeSpan.Zero);
        var tenant = new TenantId("hierarchy-merge-snapshot");
        var actor = new ActorId("operator");
        var storage = new InMemoryAssetStorage();
        var entities = new InMemoryEntityStore(storage, new FixedTimeProvider(at));
        var hierarchy = new MergeSnapshotRaceUnitOfWork();
        var audit = new InMemoryAuditLog(storage);
        var coordinator = new NodeHierarchyCompositeCoordinator(
            entities, TestEntityWritePipeline.Accepting, hierarchy, new HierarchyAuthorizedAuditWriter(audit),
            TestAuthorization.Gate(true), new FixedTimeProvider(at));
        using var oldBody = JsonDocument.Parse("{\"name\":\"old\"}");
        using var newBody = JsonDocument.Parse("{\"name\":\"new\"}");
        var oldId = await entities.CreateAsync(
            new SchemaId("schema"), oldBody, Options("merge-old", actor, tenant, at));
        var firstChild = new EntityId("entity", "test", "merge-child-a");
        var secondChild = new EntityId("entity", "test", "merge-child-b");
        await hierarchy.AddEdgeAsync(firstChild, oldId, EdgeKind.ChildOf, at);
        var newOptions = Options("merge-new", actor, tenant, at);
        var newId = InMemoryEntityStore.DeriveEntityId(new SchemaId("schema"), newOptions);

        var merge = coordinator.MergeAsync(
            [oldId], new SchemaId("schema"), newBody, newOptions, "merge", actor, tenant, at);
        await hierarchy.RaceWindow;
        await hierarchy.AddEdgeAsync(secondChild, oldId, EdgeKind.ChildOf, at);
        hierarchy.Resume();
        await merge;

        var activeChildren = hierarchy.ActiveChildEdges(at);
        Assert.Equal(2, activeChildren.Count);
        Assert.All(activeChildren, edge => Assert.Equal(newId, edge.To));
        Assert.Equal([firstChild, secondChild],
            activeChildren.Select(edge => edge.From).OrderBy(id => id.LocalPart).ToArray());
    }

    [Fact]
    public async Task HierarchyMerge_AdmitsChildCommittedAfterTheActInstantWhileWaitingForTheScope()
    {
        var at = new DateTimeOffset(2026, 9, 3, 11, 0, 0, TimeSpan.Zero);
        var tenant = new TenantId("hierarchy-merge-stale-asof");
        var actor = new ActorId("operator");
        var storage = new InMemoryAssetStorage();
        var clock = new AdvancingTimeProvider(at);
        var entities = new InMemoryEntityStore(storage, new FixedTimeProvider(at));
        var hierarchy = new MergeSnapshotRaceUnitOfWork();
        var audit = new InMemoryAuditLog(storage);
        var coordinator = new NodeHierarchyCompositeCoordinator(
            entities, TestEntityWritePipeline.Accepting, hierarchy, new HierarchyAuthorizedAuditWriter(audit),
            TestAuthorization.Gate(true), clock);
        using var oldBody = JsonDocument.Parse("{\"name\":\"old\"}");
        using var newBody = JsonDocument.Parse("{\"name\":\"new\"}");
        var oldId = await entities.CreateAsync(
            new SchemaId("schema"), oldBody, Options("merge-old", actor, tenant, at));
        var firstChild = new EntityId("entity", "test", "merge-child-a");
        var secondChild = new EntityId("entity", "test", "merge-child-b");
        await hierarchy.AddEdgeAsync(firstChild, oldId, EdgeKind.ChildOf, at);
        var newOptions = Options("merge-new", actor, tenant, at);
        var newId = InMemoryEntityStore.DeriveEntityId(new SchemaId("schema"), newOptions);

        // The merge samples the act instant (first clock read) before it reaches the atomic scope.
        var merge = coordinator.MergeAsync(
            [oldId], new SchemaId("schema"), newBody, newOptions, "merge", actor, tenant, at);
        await hierarchy.RaceWindow;
        // A concurrent act commits a child with a LATER instant while the merge is waiting.
        var later = clock.GetUtcNow();
        Assert.True(later > at);
        await hierarchy.AddEdgeAsync(secondChild, oldId, EdgeKind.ChildOf, later);
        hierarchy.Resume();
        await merge;

        var activeChildren = hierarchy.ActiveChildEdges(later);
        Assert.Equal(2, activeChildren.Count);
        Assert.All(activeChildren, edge => Assert.Equal(newId, edge.To));
        Assert.Equal([firstChild, secondChild],
            activeChildren.Select(edge => edge.From).OrderBy(id => id.LocalPart).ToArray());
    }

    [Fact]
    public async Task HierarchyMerge_ReparentsActiveChildWhoseEdgeHasAFiniteFutureEnd()
    {
        var at = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var tenant = new TenantId("hierarchy-merge-finite-end");
        var actor = new ActorId("operator");
        var storage = new InMemoryAssetStorage();
        var entities = new InMemoryEntityStore(storage, new FixedTimeProvider(at));
        var hierarchy = new InMemoryHierarchyService(storage);
        var audit = new InMemoryAuditLog(storage);
        var coordinator = new NodeHierarchyCompositeCoordinator(
            entities, TestEntityWritePipeline.Accepting, hierarchy, new HierarchyAuthorizedAuditWriter(audit),
            TestAuthorization.Gate(true), new FixedTimeProvider(at));
        using var oldBody = JsonDocument.Parse("{\"name\":\"old\"}");
        using var newBody = JsonDocument.Parse("{\"name\":\"new\"}");
        var oldId = await entities.CreateAsync(
            new SchemaId("schema"), oldBody, Options("merge-old-finite", actor, tenant, at));
        var child = new EntityId("entity", "test", "merge-child-finite");
        var originalEnd = at.AddDays(1);
        var edge = await hierarchy.AddEdgeAsync(child, oldId, EdgeKind.ChildOf, at.AddDays(-1));
        await hierarchy.InvalidateEdgeAsync(edge.Id, originalEnd);
        var newOptions = Options("merge-new-finite", actor, tenant, at);
        var newId = InMemoryEntityStore.DeriveEntityId(new SchemaId("schema"), newOptions);

        await coordinator.MergeAsync(
            [oldId], new SchemaId("schema"), newBody, newOptions, "merge", actor, tenant, at);

        var oldChildren = new List<EntityEdge>();
        await foreach (var oldChild in hierarchy.GetChildrenAsync(oldId, at)) oldChildren.Add(oldChild);
        var newChildren = new List<EntityEdge>();
        await foreach (var newChild in hierarchy.GetChildrenAsync(newId, at)) newChildren.Add(newChild);
        var childrenAtOriginalEnd = new List<EntityEdge>();
        await foreach (var newChild in hierarchy.GetChildrenAsync(newId, originalEnd))
            childrenAtOriginalEnd.Add(newChild);
        Assert.Empty(oldChildren);
        Assert.Equal(child, Assert.Single(newChildren).From);
        Assert.Empty(childrenAtOriginalEnd);
    }

    [Fact]
    public async Task HierarchySplit_ReparentedChildKeepsItsFiniteFutureEnd()
    {
        var at = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var originalEnd = at.AddDays(1);
        var tenant = new TenantId("hierarchy-split-finite-end");
        var actor = new ActorId("operator");
        var storage = new InMemoryAssetStorage();
        var entities = new InMemoryEntityStore(storage, new FixedTimeProvider(at));
        var hierarchy = new InMemoryHierarchyService(storage);
        var coordinator = new NodeHierarchyCompositeCoordinator(
            entities, TestEntityWritePipeline.Accepting, hierarchy, new HierarchyAuthorizedAuditWriter(new InMemoryAuditLog(storage)),
            TestAuthorization.Gate(true), new FixedTimeProvider(at));
        using var oldBody = JsonDocument.Parse("{\"name\":\"old\"}");
        using var newBody = JsonDocument.Parse("{\"name\":\"new\"}");
        var oldId = await entities.CreateAsync(
            new SchemaId("schema"), oldBody, Options("split-old-finite", actor, tenant, at));
        var newOptions = Options("split-new-finite", actor, tenant, at);
        var newId = InMemoryEntityStore.DeriveEntityId(new SchemaId("schema"), newOptions);
        var child = new EntityId("entity", "test", "split-child-finite");
        var edge = await hierarchy.AddEdgeAsync(child, oldId, EdgeKind.ChildOf, at.AddDays(-1));
        await hierarchy.InvalidateEdgeAsync(edge.Id, originalEnd);

        await coordinator.SplitAsync(oldId,
            [new SplitTarget(new SchemaId("schema"), newBody, newOptions)],
            new Dictionary<EntityId, EntityId> { [child] = newId },
            "split", actor, tenant, at);

        var activeChildren = new List<EntityEdge>();
        await foreach (var newChild in hierarchy.GetChildrenAsync(newId, at)) activeChildren.Add(newChild);
        var childrenAtOriginalEnd = new List<EntityEdge>();
        await foreach (var newChild in hierarchy.GetChildrenAsync(newId, originalEnd))
            childrenAtOriginalEnd.Add(newChild);
        Assert.Equal(child, Assert.Single(activeChildren).From);
        Assert.Empty(childrenAtOriginalEnd);
    }

    [Fact]
    public async Task HierarchyReparent_ReplacementEdgeKeepsItsFiniteFutureEnd()
    {
        var at = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var originalEnd = at.AddDays(1);
        var tenant = new TenantId("hierarchy-reparent-finite-end");
        var actor = new ActorId("operator");
        var storage = new InMemoryAssetStorage();
        var hierarchy = new InMemoryHierarchyService(storage);
        var coordinator = new NodeHierarchyCompositeCoordinator(
            new InMemoryEntityStore(storage, new FixedTimeProvider(at)), TestEntityWritePipeline.Accepting, hierarchy,
            new HierarchyAuthorizedAuditWriter(new InMemoryAuditLog(storage)),
            TestAuthorization.Gate(true), new FixedTimeProvider(at));
        var child = new EntityId("entity", "test", "reparent-child-finite");
        var oldParent = new EntityId("entity", "test", "reparent-old-finite");
        var newParent = new EntityId("entity", "test", "reparent-new-finite");
        var edge = await hierarchy.AddEdgeAsync(child, oldParent, EdgeKind.ChildOf, at.AddDays(-1));
        await hierarchy.InvalidateEdgeAsync(edge.Id, originalEnd);

        await coordinator.ReparentAsync(child, oldParent, newParent, "reparent", actor, tenant, at);

        var activeChildren = new List<EntityEdge>();
        await foreach (var newChild in hierarchy.GetChildrenAsync(newParent, at)) activeChildren.Add(newChild);
        var childrenAtOriginalEnd = new List<EntityEdge>();
        await foreach (var newChild in hierarchy.GetChildrenAsync(newParent, originalEnd))
            childrenAtOriginalEnd.Add(newChild);
        Assert.Equal(child, Assert.Single(activeChildren).From);
        Assert.Empty(childrenAtOriginalEnd);
    }

    [Fact]
    public async Task HierarchyReparent_MultipleRepresentableOldEdgesDoesNotThrow()
    {
        var at = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var tenant = new TenantId("hierarchy-reparent-duplicates");
        var actor = new ActorId("operator");
        var storage = new InMemoryAssetStorage();
        var hierarchy = new InMemoryHierarchyService(storage);
        var coordinator = new NodeHierarchyCompositeCoordinator(
            new InMemoryEntityStore(storage, new FixedTimeProvider(at)), TestEntityWritePipeline.Accepting, hierarchy,
            new HierarchyAuthorizedAuditWriter(new InMemoryAuditLog(storage)),
            TestAuthorization.Gate(true), new FixedTimeProvider(at));
        var child = new EntityId("entity", "test", "reparent-dup-child");
        var absent = new EntityId("entity", "test", "reparent-dup-absent");
        var oldParent = new EntityId("entity", "test", "reparent-dup-old");
        var newParent = new EntityId("entity", "test", "reparent-dup-new");
        // Two reparents from an absent parent both complete and leave two active child -> oldParent edges
        // (nothing in the store or the coordinator forbids that); the next reparent must still complete.
        await coordinator.ReparentAsync(child, absent, oldParent, "first", actor, tenant, at);
        await coordinator.ReparentAsync(child, absent, oldParent, "second", actor, tenant, at);
        var duplicates = new List<EntityEdge>();
        await foreach (var edge in hierarchy.GetChildrenAsync(oldParent, at)) duplicates.Add(edge);
        Assert.Equal(2, duplicates.Count);

        await coordinator.ReparentAsync(child, oldParent, newParent, "third", actor, tenant, at);

        var oldChildren = new List<EntityEdge>();
        await foreach (var edge in hierarchy.GetChildrenAsync(oldParent, at)) oldChildren.Add(edge);
        var newChildren = new List<EntityEdge>();
        await foreach (var edge in hierarchy.GetChildrenAsync(newParent, at)) newChildren.Add(edge);
        Assert.Empty(oldChildren);
        Assert.Equal(child, Assert.Single(newChildren).From);
    }

    [Fact]
    public async Task HierarchySplit_LateAuditFailureRollsBackEntitiesEdgesAndAudit()
    {
        var at = new DateTimeOffset(2026, 9, 2, 13, 0, 0, TimeSpan.Zero);
        var tenant = new TenantId("hierarchy-rollback");
        var actor = new ActorId("operator");
        var storage = new InMemoryAssetStorage();
        var entities = new InMemoryEntityStore(storage, new FixedTimeProvider(at));
        var hierarchy = new Harborline.Api.Foundation.Assets.Hierarchy.InMemoryHierarchyService(storage);
        var innerAudit = new InMemoryAuditLog(storage);
        var coordinator = new NodeHierarchyCompositeCoordinator(
            entities, TestEntityWritePipeline.Accepting, hierarchy, new HierarchyAuthorizedAuditWriter(new AppendThenThrowAudit(innerAudit)),
            TestAuthorization.Gate(true), new FixedTimeProvider(at));
        using var oldBody = JsonDocument.Parse("{\"name\":\"old\"}");
        using var newBody = JsonDocument.Parse("{\"name\":\"new\"}");
        var oldId = await entities.CreateAsync(new SchemaId("schema"), oldBody,
            Options("old-rollback", actor, tenant, at));
        var newOptions = Options("new-rollback", actor, tenant, at);
        var newId = new EntityId(newOptions.Scheme, newOptions.Authority, newOptions.ExplicitLocalPart!);

        await Assert.ThrowsAsync<IOException>(() => coordinator.SplitAsync(oldId,
            [new Harborline.Api.Foundation.Assets.Hierarchy.SplitTarget(new SchemaId("schema"), newBody, newOptions)],
            new Dictionary<EntityId, EntityId>(), "split", actor, tenant, at));

        Assert.NotNull(await entities.GetAsync(oldId));
        Assert.Null(await entities.GetAsync(newId));
        var children = new List<Harborline.Api.Foundation.Assets.Hierarchy.EntityEdge>();
        await foreach (var edge in hierarchy.GetChildrenAsync(newId, at)) children.Add(edge);
        Assert.Empty(children);
        var records = new List<Harborline.Api.Foundation.Assets.Audit.AuditRecord>();
        await foreach (var record in innerAudit.QueryAsync(new Harborline.Api.Foundation.Assets.Audit.AuditQuery())) records.Add(record);
        Assert.Empty(records);
    }

    [Fact]
    public async Task HierarchyRollback_DoesNotEraseConcurrentProductionWrite_AndReadersSeeNoPartialState()
    {
        var at = new DateTimeOffset(2026, 9, 2, 13, 15, 0, TimeSpan.Zero);
        var tenant = new TenantId("hierarchy-isolation");
        var actor = new ActorId("operator");
        var storage = new InMemoryAssetStorage();
        var entities = new InMemoryEntityStore(storage, new FixedTimeProvider(at));
        var reader = new InMemoryEntityStoreReader(entities);
        var hierarchy = new Harborline.Api.Foundation.Assets.Hierarchy.InMemoryHierarchyService(storage);
        var innerAudit = new InMemoryAuditLog(storage);
        var failingAudit = new BlockingAppendThenThrowAudit(innerAudit);
        var coordinator = new NodeHierarchyCompositeCoordinator(
            entities, TestEntityWritePipeline.Accepting, hierarchy, new HierarchyAuthorizedAuditWriter(failingAudit),
            TestAuthorization.Gate(true), new FixedTimeProvider(at));
        var productionWriter = new NodeEntityWriter(
            Substitute.For<IDbContextFactory<LocalNodeDbContext>>(),
            entities,
            TestEntityWritePipeline.Accepting,
            TestNodeRecordSchemas.Fresh(),
            TestAuthorization.Gate(true));
        using var oldBody = JsonDocument.Parse("{\"name\":\"old\"}");
        using var replacementBody = JsonDocument.Parse("{\"name\":\"replacement\"}");
        using var unrelatedBody = JsonDocument.Parse("{\"name\":\"unrelated\"}");
        var oldId = await entities.CreateAsync(
            new SchemaId("schema"), oldBody, Options("isolated-old", actor, tenant, at));
        var replacementOptions = Options("isolated-replacement", actor, tenant, at);
        var replacementId = InMemoryEntityStore.DeriveEntityId(new SchemaId("schema"), replacementOptions);

        var composite = coordinator.SplitAsync(oldId,
            [new Harborline.Api.Foundation.Assets.Hierarchy.SplitTarget(
                new SchemaId("schema"), replacementBody, replacementOptions)],
            new Dictionary<EntityId, EntityId>(), "split", actor, tenant, at);
        await failingAudit.Reached;

        var concurrentWrite = Task.Run(async () =>
        {
            var options = Options("unrelated", actor, tenant, at.AddMinutes(1));
            var id = await productionWriter.CreateAsync(
                new SchemaId("schema"), unrelatedBody, options,
                new AuthorizationWriteContext(actor, tenant, at.AddMinutes(1)));
            using var payload = JsonDocument.Parse("{\"source\":\"concurrent\"}");
            await innerAudit.AppendAsync(new AuditAppend(
                id, null, Op.Mint, actor, tenant, at.AddMinutes(1), payload, "concurrent"));
            return id;
        });
        var concurrentRead = Task.Run(() => reader.GetAsync(replacementId));

        await Task.Delay(50);
        Assert.False(concurrentWrite.IsCompleted);
        Assert.False(concurrentRead.IsCompleted);
        failingAudit.Fail();

        await Assert.ThrowsAsync<IOException>(() => composite);
        var unrelatedId = await concurrentWrite;
        Assert.Null(await concurrentRead);
        Assert.NotNull(await reader.GetAsync(oldId));
        Assert.Null(await reader.GetAsync(replacementId));
        Assert.NotNull(await reader.GetAsync(unrelatedId));
        var versions = new List<Harborline.Api.Foundation.Assets.Versions.Version>();
        await foreach (var version in new Harborline.Api.Foundation.Assets.Versions.InMemoryVersionStore(storage)
                           .GetHistoryAsync(unrelatedId))
            versions.Add(version);
        Assert.Single(versions);
        var auditRows = new List<Harborline.Api.Foundation.Assets.Audit.AuditRecord>();
        await foreach (var row in innerAudit.QueryAsync(
                           new Harborline.Api.Foundation.Assets.Audit.AuditQuery(Entity: unrelatedId)))
            auditRows.Add(row);
        Assert.Single(auditRows);
    }

    [Fact]
    public async Task EntityReaders_CaptureWholeCommittedSnapshotBeforeCompositeRollback()
    {
        var at = new DateTimeOffset(2026, 9, 2, 13, 25, 0, TimeSpan.Zero);
        var tenant = new TenantId("entity-query-isolation");
        var actor = new ActorId("operator");
        var storage = new InMemoryAssetStorage();
        var entities = new InMemoryEntityStore(storage, new FixedTimeProvider(at));
        var reader = new InMemoryEntityStoreReader(entities);
        var hierarchy = new Harborline.Api.Foundation.Assets.Hierarchy.InMemoryHierarchyService(storage);
        using var committedBody = JsonDocument.Parse("{\"state\":\"committed\"}");
        using var uncommittedBody = JsonDocument.Parse("{\"state\":\"uncommitted\"}");
        var firstId = await entities.CreateAsync(
            new SchemaId("schema"), committedBody, Options("snapshot-first", actor, tenant, at));
        var secondId = await entities.CreateAsync(
            new SchemaId("schema"), committedBody, Options("snapshot-second", actor, tenant, at));

        await using var query = reader.QueryAsync(new EntityQuery(Tenant: tenant)).GetAsyncEnumerator();
        Assert.True(await query.MoveNextAsync());
        var observed = new List<Entity> { query.Current };
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rollBack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var composite = hierarchy.ExecuteAtomicAsync<bool>(async ct =>
        {
            await entities.UpdateAsync(firstId, uncommittedBody, new UpdateOptions(actor, ValidFrom: at.AddMinutes(1)), ct);
            await entities.UpdateAsync(secondId, uncommittedBody, new UpdateOptions(actor, ValidFrom: at.AddMinutes(1)), ct);
            updated.TrySetResult();
            await rollBack.Task.WaitAsync(ct);
            throw new IOException("force rollback after uncommitted updates");
        });
        await updated.Task;

        Assert.True(await query.MoveNextAsync());
        observed.Add(query.Current);
        Assert.Equal("committed", query.Current.Body.RootElement.GetProperty("state").GetString());
        var getDuringComposite = reader.GetAsync(firstId);
        await Task.Delay(50);
        Assert.False(getDuringComposite.IsCompleted);
        rollBack.TrySetResult();

        await Assert.ThrowsAsync<IOException>(() => composite);
        var fetched = Assert.IsType<Entity>(await getDuringComposite);
        Assert.Equal("committed", fetched.Body.RootElement.GetProperty("state").GetString());
        while (await query.MoveNextAsync()) observed.Add(query.Current);
        Assert.Equal(2, observed.Count);
        Assert.All(observed, entity =>
            Assert.Equal("committed", entity.Body.RootElement.GetProperty("state").GetString()));
    }

    [Fact]
    public async Task HierarchySplit_LateTargetValidationFailureLeavesNoPartialWrite()
    {
        var at = new DateTimeOffset(2026, 9, 2, 13, 30, 0, TimeSpan.Zero);
        var tenant = new TenantId("hierarchy-late-target");
        var actor = new ActorId("operator");
        var storage = new InMemoryAssetStorage();
        var validator = new RejectNthValidation();
        var rejectingAdmission = new EntityBodyAdmission(validator);
        var entities = new InMemoryEntityStore(storage, new FixedTimeProvider(at));
        var hierarchy = new Harborline.Api.Foundation.Assets.Hierarchy.InMemoryHierarchyService(storage);
        var audit = Substitute.For<IHierarchyAuthorizedAuditWriter>();
        using var oldBody = JsonDocument.Parse("{\"name\":\"old\"}");
        using var firstBody = JsonDocument.Parse("{\"name\":\"first\"}");
        using var lateBody = JsonDocument.Parse("{\"name\":\"late\"}");
        var oldId = await entities.CreateAsync(new SchemaId("schema"), oldBody,
            Options("old-late", actor, tenant, at));
        validator.RejectOn = 2;
        var firstOptions = Options("first-late", actor, tenant, at);
        var lateOptions = Options("second-late", actor, tenant, at);
        var firstId = new EntityId("entity", "test", firstOptions.ExplicitLocalPart!);
        var lateId = new EntityId("entity", "test", lateOptions.ExplicitLocalPart!);
        var coordinator = new NodeHierarchyCompositeCoordinator(
            entities, rejectingAdmission, hierarchy, audit, TestAuthorization.Gate(true), new FixedTimeProvider(at));

        await Assert.ThrowsAsync<EntityValidationException>(() => coordinator.SplitAsync(oldId,
        [
            new Harborline.Api.Foundation.Assets.Hierarchy.SplitTarget(new SchemaId("schema"), firstBody, firstOptions),
            new Harborline.Api.Foundation.Assets.Hierarchy.SplitTarget(new SchemaId("schema"), lateBody, lateOptions),
        ], new Dictionary<EntityId, EntityId>(), "split", actor, tenant, at));

        Assert.NotNull(await entities.GetAsync(oldId));
        Assert.Null(await entities.GetAsync(firstId));
        Assert.Null(await entities.GetAsync(lateId));
        Assert.DoesNotContain(firstId, storage.Entities.Keys);
        Assert.DoesNotContain(lateId, storage.Entities.Keys);
        Assert.Empty(audit.ReceivedCalls());
    }

    [Fact]
    public async Task HierarchyCompositeDenialTouchesNoEntityEdgeOrAuditWrites()
    {
        var at = new DateTimeOffset(2026, 9, 2, 14, 0, 0, TimeSpan.Zero);
        var tenant = new TenantId("hierarchy-denied");
        var actor = new ActorId("operator");
        var entities = Substitute.For<IEntityMutationStore>();
        var transaction = Substitute.For<Harborline.Api.Foundation.Assets.Hierarchy.IHierarchyCompositeUnitOfWork>();
        ExecuteMergeAtomicCallback(transaction);
        var audit = Substitute.For<IHierarchyAuthorizedAuditWriter>();
        var coordinator = new NodeHierarchyCompositeCoordinator(
            entities, TestEntityWritePipeline.Accepting, transaction, audit, TestAuthorization.Gate(false), new FixedTimeProvider(at));
        using var body = JsonDocument.Parse("{}");
        var old = new EntityId("entity", "test", "old");
        var options = Options("new", actor, tenant, at);

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => coordinator.SplitAsync(old,
            [new Harborline.Api.Foundation.Assets.Hierarchy.SplitTarget(new SchemaId("schema"), body, options)],
            new Dictionary<EntityId, EntityId>(), "split", actor, tenant, at));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => coordinator.MergeAsync(
            [old], new SchemaId("schema"), body, options, "merge", actor, tenant, at));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => coordinator.ReparentAsync(
            old, new EntityId("entity", "test", "parent-a"), new EntityId("entity", "test", "parent-b"),
            "reparent", actor, tenant, at));

        Assert.Empty(entities.ReceivedCalls());
        Assert.Contains(transaction.ReceivedCalls(), call =>
            call.GetMethodInfo().Name == nameof(IHierarchyCompositeUnitOfWork.ExecuteAtomicAsync));
        Assert.DoesNotContain(transaction.ReceivedCalls(), call =>
            call.GetMethodInfo().Name is nameof(IHierarchyMutationStore.AddEdgeAsync)
                or nameof(IHierarchyMutationStore.InvalidateEdgeAsync));
        Assert.Empty(audit.ReceivedCalls());
    }

    [Fact]
    public async Task HierarchySplit_ReplacementDenialPrecedesUnitOfWorkAndAllPersistence()
    {
        var at = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var tenant = new TenantId("hierarchy-denial");
        var principal = new ActorId("operator");
        var entities = Substitute.For<IEntityMutationStore>();
        var transaction = Substitute.For<Harborline.Api.Foundation.Assets.Hierarchy.IHierarchyCompositeUnitOfWork>();
        var audit = Substitute.For<IHierarchyAuthorizedAuditWriter>();
        var coordinator = new NodeHierarchyCompositeCoordinator(
            entities, TestEntityWritePipeline.Accepting, transaction, audit,
            await RealScopedHierarchyGateAsync(at, tenant, principal, "old"), new FixedTimeProvider(at));
        using var body = JsonDocument.Parse("{}");

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => coordinator.SplitAsync(
            new EntityId("entity", "test", "old"),
            [new Harborline.Api.Foundation.Assets.Hierarchy.SplitTarget(
                new SchemaId("schema"), body, Options("replacement", principal, tenant, at))],
            new Dictionary<EntityId, EntityId>(), "split", principal, tenant, at));

        Assert.Empty(entities.ReceivedCalls());
        Assert.Empty(transaction.ReceivedCalls());
        Assert.Empty(audit.ReceivedCalls());
    }

    [Fact]
    public async Task HierarchyMerge_SourceDenialInsideAtomicScopePrecedesAllPersistence()
    {
        var at = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var tenant = new TenantId("hierarchy-merge-denial");
        var principal = new ActorId("operator");
        var entities = Substitute.For<IEntityMutationStore>();
        var transaction = Substitute.For<Harborline.Api.Foundation.Assets.Hierarchy.IHierarchyCompositeUnitOfWork>();
        ExecuteMergeAtomicCallback(transaction);
        var audit = Substitute.For<IHierarchyAuthorizedAuditWriter>();
        var coordinator = new NodeHierarchyCompositeCoordinator(
            entities, TestEntityWritePipeline.Accepting, transaction, audit,
            await RealScopedHierarchyGateAsync(at, tenant, principal, "new", "old-a"), new FixedTimeProvider(at));
        using var body = JsonDocument.Parse("{}");

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => coordinator.MergeAsync(
            [new EntityId("entity", "test", "old-a"), new EntityId("entity", "test", "old-b")],
            new SchemaId("schema"), body, Options("new", principal, tenant, at),
            "merge", principal, tenant, at));

        Assert.Empty(entities.ReceivedCalls());
        Assert.Contains(transaction.ReceivedCalls(), call =>
            call.GetMethodInfo().Name == nameof(IHierarchyCompositeUnitOfWork.ExecuteAtomicAsync));
        Assert.DoesNotContain(transaction.ReceivedCalls(), call =>
            call.GetMethodInfo().Name is nameof(IHierarchyMutationStore.AddEdgeAsync)
                or nameof(IHierarchyMutationStore.InvalidateEdgeAsync));
        Assert.Empty(audit.ReceivedCalls());
    }

    [Fact]
    public async Task HierarchyReparent_ParentDenialPrecedesUnitOfWorkAndAllPersistence()
    {
        var at = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var tenant = new TenantId("hierarchy-reparent-denial");
        var principal = new ActorId("operator");
        var entities = Substitute.For<IEntityMutationStore>();
        var transaction = Substitute.For<Harborline.Api.Foundation.Assets.Hierarchy.IHierarchyCompositeUnitOfWork>();
        var audit = Substitute.For<IHierarchyAuthorizedAuditWriter>();
        var coordinator = new NodeHierarchyCompositeCoordinator(
            entities, TestEntityWritePipeline.Accepting, transaction, audit,
            await RealScopedHierarchyGateAsync(at, tenant, principal, "child", "old-parent"),
            new FixedTimeProvider(at));

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => coordinator.ReparentAsync(
            new EntityId("entity", "test", "child"),
            new EntityId("entity", "test", "old-parent"),
            new EntityId("entity", "test", "new-parent"),
            "reparent", principal, tenant, at));

        Assert.Empty(entities.ReceivedCalls());
        Assert.Empty(transaction.ReceivedCalls());
        Assert.Empty(audit.ReceivedCalls());
    }

    [Fact]
    public async Task BankWriter_GatesCreateArchiveAndOpeningBalance()
    {
        var accounts = Substitute.For<IBankAccountMutationRepository>();
        var writer = new NodeBankAccountWriter(accounts, TestAuthorization.Gate(false));
        var authority = Authority("bank-account", "bank", "/records/bank");
        var accountId = new BankAccountId("bank");
        var account = new BankAccount(accountId, authority.Tenant, default, default, "Bank", null,
            default, default, 0, default, null, default, default);

        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () => await writer.CreateAsync(account, authority));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () => await writer.ArchiveAsync(accountId, authority));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
            await writer.SetOpeningBalanceAsync(accountId, 10, default, authority));
        Assert.Empty(accounts.ReceivedCalls());
    }

    [Fact]
    public async Task FormEngine_GatesBeforeTokenBindingAndEntityCreate()
    {
        var source = Read("packages/foundation-forms-engine/FormEngine.cs");
        var save = source[source.IndexOf("public async Task<FormSubmitReceipt> SaveWithReceiptAsync", StringComparison.Ordinal)..];
        AssertBefore(save, "_authorizationGate.DecideAsync", "EnsureNotExpired");
        AssertBefore(save, "authorizationDecision.RequireAllowed", "_authorizedEntityWriter.CreateAsync");

        var definitions = Substitute.For<IFormDefinitionStore>();
        var schemas = Substitute.For<ISchemaRegistry>();
        var entities = Substitute.For<IEntityStore>();
        var encryptor = Substitute.For<IFieldEncryptor>();
        var audit = Substitute.For<IAuditLog>();
        var authorizedWriter = Substitute.For<IAuthorizedFormEntityWriter>();
        var authorizedAudit = Substitute.For<IAuthorizedAuditTrail>();
        var signer = Substitute.For<IOperationSigner>();
        var engine = new FormEngine(
            definitions, schemas, entities, encryptor, audit, new FormEngineOptions(),
            TimeProvider.System, TestAuthorization.Gate(false), authorizedWriter, authorizedAudit, signer);
        using var candidate = JsonDocument.Parse("{}");
        var authority = Authority("form", "gate-first", "/records/forms/gate-first");
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            engine.SaveWithReceiptAsync(new FormDefinitionId("gate-first"), candidate, null!, authority));
        Assert.Empty(definitions.ReceivedCalls());
        Assert.Empty(schemas.ReceivedCalls());
        Assert.Empty(entities.ReceivedCalls());
        Assert.Empty(encryptor.ReceivedCalls());
        Assert.Empty(audit.ReceivedCalls());
        Assert.Empty(authorizedWriter.ReceivedCalls());
        Assert.Empty(authorizedAudit.ReceivedCalls());
    }

    [Fact]
    public async Task FormDefinitionLifecycle_GatesCreateReplacePublishBeforeLegalHoldAndPersistence()
    {
        using var store = new InMemoryFormDefinitionStore(TimeProvider.System);
        var lifecycle = TestAuthorization.FormLifecycle(store, TestAuthorization.Gate(false), TestAuthorization.RoleGate());
        var authority = Authority("form-definition", "form", "/definitions/forms/form");
        var coordinates = new DefinitionCoordinates(authority.Tenant, "form", "1.0.0");
        var definition = new FormDefinition(
            new FormDefinitionId("form"), new SemanticVersion(1, 0, 0), FormDefinitionStatus.Draft,
            authority.Tenant, IdentityRef.System, new SchemaId("schema"), HarborlineOverlay.Empty,
            null, authority.At, authority.At);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
            await lifecycle.RegisterAsync(definition, authority));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
            await lifecycle.PublishAsync(coordinates, authority));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
            await lifecycle.DeprecateAsync(coordinates, authority));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
            await lifecycle.WithdrawAsync(coordinates, authority));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
            await lifecycle.RestorePackProjectionAsync(coordinates, authority));
        Assert.Empty(await ReadForms(store, authority.Tenant));

        var admitted = TestAuthorization.FormLifecycle(store, TestAuthorization.AllowGate(), TestAuthorization.RoleGate());
        var projected = definition with
        {
            PackSource = new PackProjectionSource("forms-pack", "2.0.0"),
            CreatedAt = authority.At,
            UpdatedAt = authority.At,
        };
        var foreign = await PackAuthority(authority, "foreign-pack", "2.0.0");
        await Assert.ThrowsAsync<PackProjectionAuthorityException>(async () => await admitted.RegisterAsync(projected, foreign));
        await Assert.ThrowsAsync<PackProjectionAuthorityException>(async () => await admitted.PublishAsync(projected, foreign));
        await Assert.ThrowsAsync<PackProjectionAuthorityException>(async () => await admitted.WithdrawAsync(projected, foreign));

        var matchingDecision = await PackDecision(authority, "forms-pack");
        var stale = new PackProjectionAuthority(
            matchingDecision, "forms-pack", "2.0.0", authority.Tenant, authority.Principal, authority.At.AddMinutes(1));
        await Assert.ThrowsAsync<PackProjectionAuthorityException>(async () => await admitted.RegisterAsync(projected, stale));
        var otherSource = projected with { PackSource = new PackProjectionSource("other-pack", "2.0.0") };
        var matching = new PackProjectionAuthority(
            matchingDecision, "forms-pack", "2.0.0", authority.Tenant, authority.Principal, authority.At);
        var sourceRefusal = await Assert.ThrowsAsync<PackProjectionAuthorityException>(async () =>
            await admitted.RegisterAsync(otherSource, matching));
        Assert.Equal(PackProjectionAuthorityCodes.SourceMismatch, sourceRefusal.Code);
        await Assert.ThrowsAsync<PackProjectionAuthorityException>(async () => await admitted.RegisterAsync(
            projected with { PackSource = new PackProjectionSource("forms-pack", "9.0.0") }, matching));
        await Assert.ThrowsAsync<PackProjectionAuthorityException>(async () => await admitted.RegisterAsync(
            projected with { CreatedAt = authority.At.AddTicks(1) }, matching));
        await Assert.ThrowsAsync<PackProjectionAuthorityException>(async () => await admitted.RegisterAsync(
            projected with { UpdatedAt = authority.At.AddTicks(1) }, matching));
        await Assert.ThrowsAsync<PackProjectionAuthorityException>(async () => await admitted.RegisterAsync(
            projected with { Tenant = new TenantId("definition-tenant-mismatch") }, matching));
        Assert.Empty(await ReadForms(store, authority.Tenant));

        var unclaimed = projected with
        {
            Id = new FormDefinitionId("form-with-null-pack-source"),
            PackSource = null,
        };
        var registered = await admitted.RegisterAsync(unclaimed, matching);
        var expectedSource = new PackProjectionSource(matching.PackId, matching.PackVersion);
        Assert.Equal(expectedSource, registered.PackSource);
    }

    [Fact]
    public async Task WorkflowDefinitionLifecycle_GatesCreateReplacePublish()
    {
        var entities = new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System);
        var store = new EntityStoreWorkflowDefinitionStore(
            entities, Substitute.For<IWorkflowAdmissionValidator>(), Harborline.Api.Foundation.Assets.Entities.TestEntityWritePipeline.Accepting, TimeProvider.System);
        var lifecycle = TestAuthorization.WorkflowLifecycle(store, TestAuthorization.Gate(false), TestAuthorization.RoleGate());
        var authority = Authority("workflow-definition", "workflow", "/definitions/workflows/workflow");
        var coordinates = new DefinitionCoordinates(authority.Tenant, "workflow", "1.0.0");
        var definition = new WorkflowDefinition
        {
            Key = "workflow", Version = "1.0.0", Tenant = authority.Tenant.Value, InitialState = "start",
        };
        using var authored = WorkflowAuthored(definition.Key, definition.Version, definition.Tenant);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
            await lifecycle.RegisterAsync(authored.RootElement, authority));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
            await lifecycle.PublishAsync(coordinates, authority));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
            await lifecycle.WithdrawAsync(coordinates, authority));
        Assert.Empty(await ReadWorkflows(store, authority.Tenant));

        var admitted = TestAuthorization.WorkflowLifecycle(store, TestAuthorization.AllowGate(), TestAuthorization.RoleGate());
        var projected = new WorkflowDefinition
        {
            Key = definition.Key,
            Version = definition.Version,
            Tenant = definition.Tenant,
            InitialState = definition.InitialState,
            PackSource = new PackProjectionSource("workflow-pack", "2.0.0"),
        };
        using var wrongTenant = WorkflowAuthored(projected.Key, projected.Version, "definition-tenant-mismatch");
        var foreign = await PackAuthority(authority, "foreign-pack", "2.0.0");
        await Assert.ThrowsAsync<PackProjectionAuthorityException>(async () => await admitted.PublishAsync(wrongTenant.RootElement, foreign));
        await Assert.ThrowsAsync<PackProjectionAuthorityException>(async () => await admitted.WithdrawAsync(wrongTenant.RootElement, foreign));
        var matchingDecision = await PackDecision(authority, "workflow-pack");
        var stale = new PackProjectionAuthority(
            matchingDecision, "workflow-pack", "2.0.0", authority.Tenant, authority.Principal, authority.At.AddSeconds(1));
        await Assert.ThrowsAsync<PackProjectionAuthorityException>(async () =>
            await admitted.RegisterAsync(authored.RootElement, stale));
        var matching = new PackProjectionAuthority(
            matchingDecision, "workflow-pack", "2.0.0", authority.Tenant, authority.Principal, authority.At);
        await Assert.ThrowsAsync<PackProjectionAuthorityException>(async () =>
            await admitted.RegisterAsync(wrongTenant.RootElement, matching));
        Assert.Empty(await ReadWorkflows(store, authority.Tenant));

        using var unclaimed = WorkflowAuthored("workflow-with-null-pack-source", projected.Version, projected.Tenant);
        var registered = await admitted.RegisterAsync(unclaimed.RootElement, matching);
        var expectedSource = new PackProjectionSource(matching.PackId, matching.PackVersion);
        Assert.Equal(expectedSource, registered.PackSource);
    }

    [Fact]
    public async Task PackInstaller_GatesInstallActivateDeactivateBeforeAdmissionOrTransaction()
    {
        var source = Read("packages/foundation-packs/Install/PackInstaller.cs");
        // Activate/Deactivate decide in their cores; Narrow validates its caller's carried decision.
        // A new unreviewed local decision fails here.
        Assert.Equal(2, Count(source, "AuthorizeOrAudit(tenant"));
        Assert.Contains("var decision = AuthorizeOrAudit(", source, StringComparison.Ordinal);
        var narrow = source[source.IndexOf("public PackNarrowingOutcome Narrow", StringComparison.Ordinal)..];
        AssertBefore(narrow, "decision.RequireAllowedReaction(", "_store.GetActive(");
        AssertBefore(narrow, "decision.RequireAllowedReaction(", "_mutations.SaveOverride(");
        var install = source[source.IndexOf("public PackInstallOutcome Install", StringComparison.Ordinal)..];
        AssertBefore(install, "var decision = AuthorizeOrAudit(", "_admission.Admit(");
        AssertBefore(source, "AuthorizeOrAudit(tenant", "_store.GetVersion");

        var packBytes = await CreateValidSignedPackAsync();
        var verifier = new ThrowingPackVerifier();
        var store = new ThrowingPackInstallStore();
        var admission = new ThrowingPackContentAdmission();
        var platform = new ThrowingPackPlatformCompatibility();
        var audit = Substitute.For<IPackInstallAudit>();
        var installer = new PackInstaller(
            verifier, store, admission, audit, TestAuthorization.Gate(false), platform);
        var context = new PackInstallContext(
            new TenantId("pack-tenant"),
            Substitute.For<IPackTrustStore>(),
            Substitute.For<IPackRevocationList>(),
            new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero),
            TimeSpan.FromHours(1),
            Principal: "pack-operator");
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Task.Run(() =>
            installer.Install(packBytes, context)));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Task.Run(() =>
            installer.Activate(context, "pack", "1.0.0")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Task.Run(() =>
            installer.Deactivate(context, "pack", "1.0.0")));
        var deniedNarrowing = await TestAuthorization.Gate(false).DecideAsync(
            new AuthorizationWriteContext(new ActorId(context.Principal!), context.Tenant, context.Now)
                .Request(AuthorizationOperation.Parse(Permission.PackagesOperate), "pack", "pack"));
        Assert.Throws<AuthorizationDeniedException>(() => installer.Narrow(
            context, "pack", "content", new JsonObject(), deniedNarrowing));
        Assert.Equal(0, verifier.CallCount);
        Assert.Equal(0, store.CallCount);
        Assert.Equal(0, admission.CallCount);
        Assert.Equal(0, platform.CallCount);
        audit.Received(3).Append(Arg.Is<PackInstallAuditEntry>(entry =>
            entry.Action == PackInstallAuditAction.Refused &&
            entry.PreDecision &&
            entry.Detail == PackInstallCodes.RefusedAuthorizationDenied));
        Assert.DoesNotContain(
            audit.ReceivedCalls(),
            call => string.Equals(call.GetMethodInfo().Name, nameof(IPackInstallAudit.AppendAuthorized), StringComparison.Ordinal));
    }

    [Fact]
    public async Task PackInstaller_DeniedInstallReadsClaimedCoordinatesWithoutParsingContents()
    {
        var packBytes = await CreateValidSignedPackAsync();
        var tampered = JsonNode.Parse(packBytes)!;
        tampered["contents"]![0]!["kind"] = "UnknownContentKind";

        AuthorizationGateRequest? captured = null;
        var verifier = new ThrowingPackVerifier();
        var store = new ThrowingPackInstallStore();
        var admission = new ThrowingPackContentAdmission();
        var platform = new ThrowingPackPlatformCompatibility();
        var audit = Substitute.For<IPackInstallAudit>();
        var installer = new PackInstaller(
            verifier,
            store,
            admission,
            audit,
            TestAuthorization.Gate(false, request => captured = request),
            platform);
        var context = new PackInstallContext(
            new TenantId("pack-tenant"),
            Substitute.For<IPackTrustStore>(),
            Substitute.For<IPackRevocationList>(),
            new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero),
            TimeSpan.FromHours(1),
            Principal: "pack-operator");

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Task.Run(() =>
            installer.Install(JsonSerializer.SerializeToUtf8Bytes(tampered), context)));

        Assert.NotNull(captured);
        Assert.Equal("pack", captured.Target.RecordId);
        Assert.Equal(0, verifier.CallCount);
        Assert.Equal(0, store.CallCount);
        Assert.Equal(0, admission.CallCount);
        Assert.Equal(0, platform.CallCount);
    }

    /// <summary>
    /// A transport encoding with duplicate properties (a second key/version inside the manifest, or a
    /// second manifest inside the payload) must never let the pre-gate claimed coordinates and the
    /// verifier disagree about which pack this is. The codec refuses ANY duplicate property, so such a
    /// pack can never verify; the gate is still consulted (and denied here) before any collaborator runs.
    /// </summary>
    [Theory]
    [InlineData("\"manifest\":{", "\"key\":\"decoy\",\"version\":\"1.0.0\",", "(unverified)")]
    [InlineData("\"payload\":{", "\"manifest\":{\"key\":\"decoy\",\"version\":\"1.0.0\"},", null)]
    public async Task PackInstaller_DuplicatePropertiesCanNeverVerifyAndNeverReachCollaborators(
        string marker, string injection, string? expectedTarget)
    {
        var packBytes = await CreateValidSignedPackAsync();
        // Re-serialize compactly so the marker is byte-predictable, then inject the duplicate as the
        // FIRST member of the enclosing object (JsonNode cannot hold duplicates).
        var json = JsonSerializer.Serialize(JsonNode.Parse(packBytes));
        var at = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        Assert.True(at >= 0, $"{marker} not found in the serialized pack");
        var duplicated = Encoding.UTF8.GetBytes(json.Insert(at + marker.Length, injection));

        // The shipping decoder refuses the duplicate outright: there is no "real" pack for the reader
        // to disagree with.
        Assert.Null(new PackFileCodec().TryDecode(duplicated));

        AuthorizationGateRequest? captured = null;
        var verifier = new ThrowingPackVerifier();
        var store = new ThrowingPackInstallStore();
        var admission = new ThrowingPackContentAdmission();
        var platform = new ThrowingPackPlatformCompatibility();
        var installer = new PackInstaller(
            verifier,
            store,
            admission,
            Substitute.For<IPackInstallAudit>(),
            TestAuthorization.Gate(false, request => captured = request),
            platform);
        var context = new PackInstallContext(
            new TenantId("pack-tenant"),
            Substitute.For<IPackTrustStore>(),
            Substitute.For<IPackRevocationList>(),
            new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero),
            TimeSpan.FromHours(1),
            Principal: "pack-operator");

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Task.Run(() =>
            installer.Install(duplicated, context)));

        Assert.NotNull(captured);
        if (expectedTarget is not null)
        {
            Assert.Equal(expectedTarget, captured.Target.RecordId);
        }
        Assert.Equal(0, verifier.CallCount);
        Assert.Equal(0, store.CallCount);
        Assert.Equal(0, admission.CallCount);
        Assert.Equal(0, platform.CallCount);
    }

    [Fact]
    public async Task PackSeedProjector_RejectsDeniedReactionBeforeReadingOrWritingStores()
    {
        var tenant = new TenantId("denied-pack-projection");
        var at = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var scope = ScopeExpression.Parse("/records/named-pack");
        var request = new AuthorizationGateRequest(
            new PermissionAtom(AuthorizationOperation.Parse(Permission.PackagesOperate), scope),
            new ActorId("denied-projector"),
            tenant,
            new AuthorizationTarget("pack", "named-pack", scope),
            at);
        var decision = await TestAuthorization.Gate(false).DecideAsync(request);
        var store = Substitute.For<IPackInstallStore>();
        var types = Substitute.For<IEntityTypeRegistry>();
        var projector = new PackSeedProjector(
            store,
            types,
            NullLogger<PackSeedProjector>.Instance, time: TimeProvider.System);

        var authority = new PackProjectionAuthority(
            decision, "named-pack", "1.0.0", tenant, request.Principal, at);
        await Assert.ThrowsAsync<PackProjectionAuthorityException>(() =>
            projector.ProjectActivePacksAsync(authority));

        Assert.Empty(store.ReceivedCalls());
        Assert.Empty(types.ReceivedCalls());

        var foreignScope = ScopeExpression.Parse("/records/foreign-pack");
        var foreignDecision = await TestAuthorization.AllowGate().DecideAsync(new AuthorizationGateRequest(
            new PermissionAtom(AuthorizationOperation.Parse(Permission.PackagesOperate), foreignScope),
            request.Principal,
            tenant,
            new AuthorizationTarget("pack", "foreign-pack", foreignScope),
            at));
        var mismatched = new PackProjectionAuthority(
            foreignDecision, "named-pack", "1.0.0", tenant, request.Principal, at);
        await Assert.ThrowsAsync<PackProjectionAuthorityException>(() =>
            projector.ProjectActivePacksAsync(mismatched));
        Assert.Empty(store.ReceivedCalls());
        Assert.Empty(types.ReceivedCalls());
    }

    [Fact]
    public void PackProjectionAuthority_EveryDecisionBindingIsFailClosed()
    {
        var tenant = new TenantId("authority-tenant");
        var principal = new ActorId("authority-principal");
        var at = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var scope = ScopeExpression.Parse("/records/pack-a");
        var request = new AuthorizationGateRequest(
            new PermissionAtom(AuthorizationOperation.Parse(Permission.PackagesOperate), scope),
            principal, tenant, new AuthorizationTarget("pack", "pack-a", scope), at);
        var allowed = Decision(request, AuthorizationVerdict.Allowed);
        new PackProjectionAuthority(allowed, "pack-a", "1.0.0", tenant, principal, at).RequireValid();

        AssertAuthorityCode(PackProjectionAuthorityCodes.DecisionDenied,
            new PackProjectionAuthority(Decision(request, AuthorizationVerdict.Denied), "pack-a", "1.0.0", tenant, principal, at));
        AssertAuthorityCode(PackProjectionAuthorityCodes.OperationMismatch,
            new PackProjectionAuthority(Decision(request with
            {
                Act = new PermissionAtom(AuthorizationOperation.Parse(Permission.FormsAuthor), scope),
            }, AuthorizationVerdict.Allowed), "pack-a", "1.0.0", tenant, principal, at));
        AssertAuthorityCode(PackProjectionAuthorityCodes.TargetMismatch,
            new PackProjectionAuthority(Decision(request with
            {
                Target = new AuthorizationTarget("packages", "pack-a", scope),
            }, AuthorizationVerdict.Allowed), "pack-a", "1.0.0", tenant, principal, at));
        AssertAuthorityCode(PackProjectionAuthorityCodes.TargetMismatch,
            new PackProjectionAuthority(allowed, "pack-b", "1.0.0", tenant, principal, at));
        AssertAuthorityCode(PackProjectionAuthorityCodes.TenantMismatch,
            new PackProjectionAuthority(allowed, "pack-a", "1.0.0", new TenantId("other"), principal, at));
        AssertAuthorityCode(PackProjectionAuthorityCodes.PrincipalMismatch,
            new PackProjectionAuthority(allowed, "pack-a", "1.0.0", tenant, new ActorId("other"), at));
        AssertAuthorityCode(PackProjectionAuthorityCodes.InstantMismatch,
            new PackProjectionAuthority(allowed, "pack-a", "1.0.0", tenant, principal, at.AddTicks(1)));

        // 274: a blank ActorId is no longer constructible (the canonical form refuses it); the only
        // unset actor left is default(ActorId), which is what this mismatch case now presents.
        var blankPrincipal = default(ActorId);
        var blankRequest = request with { Principal = blankPrincipal };
        AssertAuthorityCode(PackProjectionAuthorityCodes.PrincipalMismatch,
            new PackProjectionAuthority(
                Decision(blankRequest, AuthorizationVerdict.Allowed),
                "pack-a", "1.0.0", tenant, blankPrincipal, at));

        AssertAuthorityCode(PackProjectionAuthorityCodes.InstantMismatch,
            new PackProjectionAuthority(
                allowed, "pack-a", "1.0.0", tenant, principal, at,
                requestAt: at.AddTicks(1), decidedAt: at));
        AssertAuthorityCode(PackProjectionAuthorityCodes.InstantMismatch,
            new PackProjectionAuthority(
                allowed, "pack-a", "1.0.0", tenant, principal, at,
                requestAt: at, decidedAt: at.AddTicks(1)));
    }

    [Fact]
    public void PackProjectionAuthority_RefusesIncompleteAdmissionEvidence()
    {
        var valid = new PackProjectionAdmission(
            Guid.NewGuid(), "pack-a", "1.0.0", new TenantId("tenant-a"), new ActorId("operator"),
            new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero), ["grant", "definition"], Projected: false);
        Assert.Throws<ArgumentException>(() => new TenantId(" "));
        PackProjectionAdmission[] incomplete =
        [
            valid with { Tenant = default },
            valid with { PackId = " " },
            valid with { PackVersion = " " },
            valid with { Instant = default },
            valid with { DerivationIds = [] },
            valid with { DerivationIds = ["grant", " "] },
        ];

        foreach (var admission in incomplete)
            Assert.Throws<InvalidOperationException>(() => PackProjectionAuthority.FromAdmission(admission));
    }

    [Fact]
    public async Task PackProjectionAuthority_IsConsumedOnceByProjector()
    {
        var tenant = new TenantId("one-shot-tenant");
        var at = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var context = new AuthorizationWriteContext(new ActorId("operator"), tenant, at);
        var authority = await PackAuthority(context, "one-shot-pack", "1.0.0");
        var store = Substitute.For<IPackInstallStore>();
        store.ListInstalled(tenant).Returns(Array.Empty<InstalledPack>());
        var services = new ServiceCollection();
        services.AddScoped(_ => new PackSeedProjector(
            store, Substitute.For<IEntityTypeRegistry>(), NullLogger<PackSeedProjector>.Instance, time: TimeProvider.System));
        using var provider = services.BuildServiceProvider();
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        var firstProjector = firstScope.ServiceProvider.GetRequiredService<PackSeedProjector>();
        var secondProjector = secondScope.ServiceProvider.GetRequiredService<PackSeedProjector>();

        await firstProjector.ProjectActivePacksAsync(authority);
        var replay = await Assert.ThrowsAsync<PackProjectionAuthorityException>(
            () => secondProjector.ProjectActivePacksAsync(authority));
        Assert.Equal(PackProjectionAuthorityCodes.Replayed, replay.Code);
    }

    [Fact]
    public async Task PackInstaller_RetiresAuthorityCapturedByProjectionSeam()
    {
        var tenant = new TenantId("retired-authority-tenant");
        var at = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        using var signer = KeyPair.Generate();
        var store = new InMemoryPackInstallStore();
        var pack = new InstalledPack(
            "retired-pack", "1.0.0", PackScopeTier.Horizontal, PackLifecycleState.Draft,
            Array.Empty<PackSeedItem>(), new Dictionary<string, int>(), at,
            signer.PrincipalId, 1, TrustScope.OwnRoster, Array.Empty<PackDependencyRef>());
        store.Commit(new PackInstallTransaction(
            tenant, pack, new PackInstallWatermark(pack.PackKey, pack.Version, new Dictionary<string, int>()),
            Array.Empty<PackTenantOverride>()));
        var dispatcher = new CapturingProjectionDispatcher();
        var installer = new PackInstaller(
            Substitute.For<IPackVerifier>(), store, Substitute.For<IPackContentAdmission>(),
            Substitute.For<IPackInstallAudit>(), TestAuthorization.AllowGate(), dispatcher);
        var context = new PackInstallContext(
            tenant, Substitute.For<IPackTrustStore>(), Substitute.For<IPackRevocationList>(),
            at, TimeSpan.FromHours(1), Principal: "operator");

        var outcome = installer.Activate(context, pack.PackKey, pack.Version);
        Assert.True(outcome.Activated);
        Assert.True(outcome.Projected);
        var leaked = Assert.Single(dispatcher.Captured);
        var projector = new PackSeedProjector(
            store, Substitute.For<IEntityTypeRegistry>(), NullLogger<PackSeedProjector>.Instance, time: TimeProvider.System);
        var refused = await Assert.ThrowsAsync<PackProjectionAuthorityException>(
            () => projector.ProjectActivePacksAsync(leaked));
        Assert.Equal(PackProjectionAuthorityCodes.Replayed, refused.Code);

        await AssertRetiredAtBothLifecycles(leaked);

        var deactivated = installer.Deactivate(context with { Now = at.AddMinutes(1) }, pack.PackKey, pack.Version);
        Assert.True(deactivated.Deactivated);
        Assert.True(deactivated.Projected);
        var deactivationAuthority = dispatcher.Captured[1];
        await AssertRetiredAtBothLifecycles(deactivationAuthority);
    }

    [Fact]
    public async Task PackFirstPublish_StampsAuthoritySourceAndRejectsFabricatedClaimsBeforeWrite()
    {
        var tenant = new TenantId("first-publish-tenant");
        var at = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var authority = await PackAuthority(
            new AuthorizationWriteContext(new ActorId("operator"), tenant, at), "pack-a", "1.0.0");

        using var formStore = new InMemoryFormDefinitionStore(TimeProvider.System);
        var forms = TestAuthorization.FormLifecycle(formStore, TestAuthorization.AllowGate(), TestAuthorization.RoleGate());
        var form = new FormDefinition(
            new FormDefinitionId("first-publish-form"), new SemanticVersion(1, 0, 0),
            FormDefinitionStatus.Draft, tenant, IdentityRef.System, new SchemaId("schema"),
            HarborlineOverlay.Empty, null, at, at);
        var publishedForm = await forms.RegisterAndPublishAsync(form, authority);
        Assert.Equal(new PackProjectionSource("pack-a", "1.0.0"), publishedForm.PackSource);

        var forgedForm = form with
        {
            Id = new FormDefinitionId("forged-first-publish-form"),
            PackSource = new PackProjectionSource("pack-b", "9.0.0"),
        };
        var formRefusal = await Assert.ThrowsAsync<PackProjectionAuthorityException>(async () =>
            await forms.RegisterAndPublishAsync(forgedForm, authority));
        Assert.Equal(PackProjectionAuthorityCodes.SourceMismatch, formRefusal.Code);
        await Assert.ThrowsAsync<FormDefinitionNotFoundException>(async () => await formStore.GetAsync(
            new DefinitionCoordinates(tenant, forgedForm.Id.Value, forgedForm.Version.ToString())));

        var entityStore = new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System);
        var workflowStore = new EntityStoreWorkflowDefinitionStore(
            entityStore, Substitute.For<IWorkflowAdmissionValidator>(), Harborline.Api.Foundation.Assets.Entities.TestEntityWritePipeline.Accepting, TimeProvider.System);
        var workflows = TestAuthorization.WorkflowLifecycle(workflowStore, TestAuthorization.AllowGate(), TestAuthorization.RoleGate());
        using var authored = WorkflowAuthored("first-publish-workflow", "1.0.0", tenant.Value);
        var publishedWorkflow = await workflows.RegisterAndPublishAsync(
            authored.RootElement, authority);
        Assert.Equal(new PackProjectionSource("pack-a", "1.0.0"), publishedWorkflow.PackSource);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackFormLifecycle_PersistsActivationInstantForEveryTransition(bool durableEntityStore)
    {
        var tenant = new TenantId("pack-instant-tenant");
        var publishAt = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var withdrawAt = publishAt.AddMinutes(1);
        var restoreAt = publishAt.AddMinutes(2);
        using var sqlite = durableEntityStore ? new SqliteEntityStore() : null;
        using var memory = durableEntityStore
            ? null
            : new InMemoryFormDefinitionStore(new FixedTimeProvider(publishAt.AddDays(1)));
        IFormDefinitionStore store = durableEntityStore
            ? new EntityStoreFormDefinitionStore(sqlite!, Harborline.Api.Foundation.Assets.Entities.TestEntityWritePipeline.Accepting, new FixedTimeProvider(publishAt.AddDays(1)))
            : memory!;
        var lifecycle = TestAuthorization.FormLifecycle(store, TestAuthorization.AllowGate(), TestAuthorization.RoleGate());
        var definition = new FormDefinition(
            new FormDefinitionId("pack-instant-form"), new SemanticVersion(1, 0, 0),
            FormDefinitionStatus.Draft, tenant, IdentityRef.System, new SchemaId("schema"),
            HarborlineOverlay.Empty, null, publishAt, publishAt)
        {
            PackSource = new PackProjectionSource("instant-pack", "1.0.0"),
        };

        var publishAuthority = await PackAuthority(
            new AuthorizationWriteContext(new ActorId("operator"), tenant, publishAt),
            "instant-pack", "1.0.0");
        await lifecycle.RegisterAsync(definition, publishAuthority);
        var published = await lifecycle.PublishAsync(definition, publishAuthority);
        Assert.Equal(publishAt, published.UpdatedAt);
        Assert.Equal(publishAt, (await store.GetAsync(
            new DefinitionCoordinates(tenant, definition.Id.Value, definition.Version.ToString()))).UpdatedAt);

        var withdrawAuthority = await PackAuthority(
            new AuthorizationWriteContext(new ActorId("operator"), tenant, withdrawAt),
            "instant-pack", "1.0.0");
        var withdrawalModel = definition with { CreatedAt = withdrawAt, UpdatedAt = withdrawAt };
        var withdrawn = await lifecycle.WithdrawAsync(withdrawalModel, withdrawAuthority);
        Assert.Equal(withdrawAt, withdrawn.UpdatedAt);
        Assert.Equal(withdrawAt, (await store.GetAsync(
            new DefinitionCoordinates(tenant, definition.Id.Value, definition.Version.ToString()))).UpdatedAt);

        var restoreAuthority = await PackAuthority(
            new AuthorizationWriteContext(new ActorId("operator"), tenant, restoreAt),
            "instant-pack", "1.0.0");
        var restoreModel = definition with { CreatedAt = restoreAt, UpdatedAt = restoreAt };
        var restored = await lifecycle.RestorePackProjectionAsync(restoreModel, restoreAuthority);
        Assert.Equal(restoreAt, restored.UpdatedAt);
        Assert.Equal(restoreAt, (await store.GetAsync(
            new DefinitionCoordinates(tenant, definition.Id.Value, definition.Version.ToString()))).UpdatedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackWorkflowLifecycle_PersistsActivationInstantForEveryTransition(bool durableEntityStore)
    {
        var tenant = new TenantId("pack-workflow-instant-tenant");
        var publishAt = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var withdrawAt = publishAt.AddMinutes(1);
        var restoreAt = publishAt.AddMinutes(2);
        using var sqlite = durableEntityStore ? new SqliteEntityStore() : null;
        IEntityMutationStore entities = durableEntityStore
            ? sqlite!
            : new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System);
        var store = new EntityStoreWorkflowDefinitionStore(
            entities,
            Substitute.For<IWorkflowAdmissionValidator>(), Harborline.Api.Foundation.Assets.Entities.TestEntityWritePipeline.Accepting,
            new FixedTimeProvider(publishAt.AddDays(1)));
        var lifecycle = TestAuthorization.WorkflowLifecycle(store, TestAuthorization.AllowGate(), TestAuthorization.RoleGate());
        using var authored = WorkflowAuthored("pack-instant-workflow", "1.0.0", tenant.Value);

        var publishAuthority = await PackAuthority(
            new AuthorizationWriteContext(new ActorId("operator"), tenant, publishAt),
            "instant-pack", "1.0.0");
        await lifecycle.RegisterAsync(authored.RootElement, publishAuthority);
        var model = PackModel(authored.RootElement, publishAuthority);
        var published = await lifecycle.PublishAsync(authored.RootElement, publishAuthority);
        Assert.Equal(publishAt, published.UpdatedAt);
        Assert.Equal(publishAt, (await store.GetAsync(
            new DefinitionCoordinates(tenant, model.Key, model.Version))).UpdatedAt);

        var withdrawAuthority = await PackAuthority(
            new AuthorizationWriteContext(new ActorId("operator"), tenant, withdrawAt),
            "instant-pack", "1.0.0");
        var withdrawn = await lifecycle.WithdrawAsync(authored.RootElement, withdrawAuthority);
        Assert.Equal(withdrawAt, withdrawn.UpdatedAt);
        Assert.Equal(withdrawAt, (await store.GetAsync(
            new DefinitionCoordinates(tenant, model.Key, model.Version))).UpdatedAt);

        var restoreAuthority = await PackAuthority(
            new AuthorizationWriteContext(new ActorId("operator"), tenant, restoreAt),
            "instant-pack", "1.0.0");
        var restored = await lifecycle.RestorePackProjectionAsync(authored.RootElement, restoreAuthority);
        Assert.Equal(restoreAt, restored.UpdatedAt);
        Assert.Equal(restoreAt, (await store.GetAsync(
            new DefinitionCoordinates(tenant, model.Key, model.Version))).UpdatedAt);
    }

    [Fact]
    public async Task PackFormLifecycle_UsesPersistedSourceInsteadOfFabricatedModelClaim()
    {
        var tenant = new TenantId("persisted-source-tenant");
        var at = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        using var store = new InMemoryFormDefinitionStore(TimeProvider.System);
        var persisted = new FormDefinition(
            new FormDefinitionId("persisted-source-form"), new SemanticVersion(1, 0, 0),
            FormDefinitionStatus.Draft, tenant, IdentityRef.System, new SchemaId("schema"),
            HarborlineOverlay.Empty, null, at, at)
        {
            PackSource = new PackProjectionSource("pack-b", "1.0.0"),
        };
        await store.RegisterAsync(persisted);
        var fabricated = persisted with { PackSource = new PackProjectionSource("pack-a", "1.0.0") };
        var authority = await PackAuthority(
            new AuthorizationWriteContext(new ActorId("operator"), tenant, at), "pack-a", "1.0.0");
        var lifecycle = TestAuthorization.FormLifecycle(store, TestAuthorization.AllowGate(), TestAuthorization.RoleGate());

        foreach (var transition in new Func<Task>[]
        {
            async () => await lifecycle.PublishAsync(fabricated, authority),
            async () => await lifecycle.WithdrawAsync(fabricated, authority),
            async () => await lifecycle.RestorePackProjectionAsync(fabricated, authority),
        })
        {
            var refused = await Assert.ThrowsAsync<PackProjectionAuthorityException>(transition);
            Assert.Equal(PackProjectionAuthorityCodes.SourceMismatch, refused.Code);
        }
    }

    [Fact]
    public async Task AuthorizationDefinitionWriter_AuthorizeStageUsesGateAndPreservesSixStageOrder()
    {
        var source = Read("packages/blocks-access-grant/AuthorizationDefinitionWriter.cs");
        AssertBefore(source, "stages.Add(\"authorize\")", "stages.Add(\"bind\")");
        AssertBefore(source, "stages.Add(\"bind\")", "stages.Add(\"mutate\")");
        AssertBefore(source, "stages.Add(\"mutate\")", "stages.Add(\"validate\")");
        AssertBefore(source, "stages.Add(\"validate\")", "stages.Add(\"commit\")");
        AssertBefore(source, "stages.Add(\"commit\")", "stages.Add(\"react\")");
        AssertBefore(source, "await AuthorizeAsync(command", "var bound = await BindAsync(command");
        AssertBefore(source, "gate.DecideAsync", "decision.RequireAllowed");

        var at = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var tenant = new TenantId("seed-sealed-tenant");
        var principal = new ActorId("administrator");
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        await grants.AppendAsync(tenant, new AccessGrant(
            GrantId.New(), tenant, principal, RoleReference.Administrator, ScopeExpression.Parse("/"),
            GrantResidency.Cache, new GrantValidity(at.AddMinutes(-1), at.AddDays(1)),
            GranterKind.Person, principal, at.AddMinutes(-1),
            new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual), principal),
            at.AddMinutes(-1)));
        var configuration = TestInMemoryAuthorizationStores.ConfigurationStore();
        var vocabulary = new InMemoryRoleVocabulary(AccessGrantAuthorizationSeed.RoleDefinitions);
        var writer = new AuthorizationDefinitionWriter(
            configuration,
            configuration,
            new AuthorizationDefinitionAdmission(vocabulary),
            new AuthorizationCapabilityBindingAdmission(),
            TestAuthorization.AllowGate(),
            grants);
        var seed = new AccessGrantAuthorizationSeed(writer, configuration, grants);
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await seed.InstallAsync(tenant, at, AuthorizationSeedProfile.Production));
        Assert.Contains("sealed", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthorizationDefinitionWriter_DeniedOrdinaryAndBootstrapWritesNeverReachStore()
    {
        var store = Substitute.For<IAuthorizationConfigurationStore>();
        var states = Substitute.For<AuthorizationConfigurationStateReader>();
        var vocabulary = new InMemoryRoleVocabulary(AccessGrantAuthorizationSeed.RoleDefinitions);
        var authority = Authority("grant", "definition", "/records/definition");
        var definition = new AuthorizationCapabilityDefinition(
            new AuthorizationCapabilityDefinitionId(Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444")),
            "package.test",
            1,
            AuthorizationOperation.Parse(Permission.ContactsRead),
            PermissionAtom.Parse($"{Permission.ContactsRead}@/"),
            RoleBindingSet.Of(RoleReference.Administrator));
        var writer = new AuthorizationDefinitionWriter(
            store,
            states,
            new AuthorizationDefinitionAdmission(vocabulary),
            new AuthorizationCapabilityBindingAdmission(),
            TestAuthorization.Gate(false),
            TestInMemoryAuthorizationStores.GrantStore());

        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
            await writer.WriteAsync(new InstallAuthorizationDefinition(definition, authority.Tenant), authority));
        Assert.Empty(store.ReceivedCalls());
        Assert.Empty(states.ReceivedCalls());

        var scope = ScopeExpression.Parse($"/records/{definition.DefinitionId.Value}");
        var deniedBootstrap = await TestAuthorization.Gate(false).DecideAsync(new AuthorizationGateRequest(
            new PermissionAtom(AuthorizationOperation.Parse(Permission.GrantPermissions), scope),
            authority.Principal,
            authority.Tenant,
            new AuthorizationTarget("grant", definition.DefinitionId.Value.ToString(), scope),
            authority.At));
        Assert.Throws<ArgumentException>(() => PlatformBootstrapDecision.Mint(deniedBootstrap));
        Assert.Empty(store.ReceivedCalls());
        Assert.Empty(states.ReceivedCalls());
    }

    [Fact]
    public async Task AuthorizationSeed_RevokedAdministratorPermanentlySealsBootstrap()
    {
        var at = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var tenant = new TenantId("retired-admin-tenant");
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        // The retired-administrator state is APPENDED already revoked: ticket 211's
        // not_last_administrator() guard (L619) refuses to revoke the last Administrator in force, and this
        // test's subject is the seal AFTER that state exists, not the transition into it.
        var administrator = AdministratorGrant(tenant, at) with
        {
            Status = GrantStatus.Revoked,
            Revocation = new GrantRevocation(
                new ActorId("administrator"), at, new GrantReason(GrantReasonCodes.RevocationOffboarding)),
        };
        await grants.AppendAsync(tenant, administrator);

        var (seed, configuration) = Seed(grants);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await seed.InstallAsync(tenant, at, AuthorizationSeedProfile.Production));
        Assert.Null((await configuration.ReadStateAsync(DefinitionId(Permission.ContactsRead))).Definition);
    }

    [Fact]
    public async Task AuthorizationSeed_UpgradesAdministeredPreSliceInstallOnceWithAdditiveDefinitionsAndSchedulerGrant()
    {
        var at = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var tenant = new TenantId("administered-upgrade-tenant");
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        var configuration = TestInMemoryAuthorizationStores.ConfigurationStore();
        var writer = new AuthorizationDefinitionWriter(
            configuration,
            configuration,
            new AuthorizationDefinitionAdmission(
                new InMemoryRoleVocabulary(AccessGrantAuthorizationSeed.RoleDefinitions)),
            new AuthorizationCapabilityBindingAdmission(),
            TestAuthorization.AllowGate(),
            grants);
        var administrator = AdministratorGrant(tenant, at);
        var administratorAuthority = new AuthorizationWriteContext(administrator.Subject, tenant, at);

        foreach (var definition in AccessGrantAuthorizationSeed.FoundingDefinitions)
            await writer.WriteAsync(new InstallAuthorizationDefinition(definition), administratorAuthority);
        await grants.AppendAsync(tenant, administrator);
        foreach (var definition in AccessGrantAuthorizationSeed.AdditiveSystemDefinitions)
            Assert.Null((await configuration.ReadStateAsync(definition.DefinitionId)).Definition);

        var definitionsBefore = (await configuration.ListAsync(tenant)).Count;
        var grantsBefore = (await grants.SnapshotAsync(tenant)).Count;

        var seed = new AccessGrantAuthorizationSeed(writer, configuration, grants);
        await seed.InstallAsync(tenant, at, AuthorizationSeedProfile.Production);

        Assert.Equal(definitionsBefore + 3, (await configuration.ListAsync(tenant)).Count);
        // Two installer seed grants now: the scheduler, and the desktop node operator's workshop:unlock.
        Assert.Equal(grantsBefore + 2, (await grants.SnapshotAsync(tenant)).Count);

        var firstDefinitions = new List<AuthorizationCapabilityDefinition>();
        foreach (var expected in AccessGrantAuthorizationSeed.AdditiveSystemDefinitions)
        {
            var installed = (await configuration.ReadStateAsync(expected.DefinitionId)).Definition;
            firstDefinitions.Add(Assert.IsType<AuthorizationCapabilityDefinition>(installed));
            Assert.Equal(expected, installed);
        }
        var scheduler = await grants.FindBySourceReferenceAsync(
            tenant, AccessGrantAuthorizationSeed.SchedulerGrantSource);
        Assert.NotNull(scheduler);
        Assert.Equal(AccessGrantAuthorizationSeed.SchedulerPrincipal, scheduler!.Subject.Value);
        Assert.Null(await grants.FindBySourceReferenceAsync(
            tenant, AccessGrantAuthorizationSeed.DevIndexerGrantSource));

        await seed.InstallAsync(tenant, at.AddMinutes(1), AuthorizationSeedProfile.Production);

        Assert.Equal(definitionsBefore + 3, (await configuration.ListAsync(tenant)).Count);
        // Two installer seed grants now: the scheduler, and the desktop node operator's workshop:unlock.
        Assert.Equal(grantsBefore + 2, (await grants.SnapshotAsync(tenant)).Count);

        for (var index = 0; index < firstDefinitions.Count; index++)
        {
            var current = (await configuration.ReadStateAsync(
                AccessGrantAuthorizationSeed.AdditiveSystemDefinitions[index].DefinitionId)).Definition;
            Assert.Equal(firstDefinitions[index], current);
        }
        Assert.Equal(scheduler, await grants.FindBySourceReferenceAsync(
            tenant, AccessGrantAuthorizationSeed.SchedulerGrantSource));
    }

    [Fact]
    public async Task AuthorizationSeed_AdministratorInAnotherTenantSealsGlobalBootstrap()
    {
        var at = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        var firstTenant = new TenantId("first-tenant");
        await grants.AppendAsync(firstTenant, AdministratorGrant(firstTenant, at));

        var (seed, configuration) = Seed(grants);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await seed.InstallAsync(
                new TenantId("second-tenant"), at, AuthorizationSeedProfile.Production));
        Assert.Null((await configuration.ReadStateAsync(DefinitionId(Permission.ContactsRead))).Definition);
    }

    [Fact]
    public async Task AuthorizationSeed_ForeignGrantStoreIsRejectedByStableFenceFailure()
    {
        var at = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var tenant = new TenantId("race-tenant");
        var fence = new InMemoryAuthorizationBootstrapFence();
        var grants = new InMemoryGrantStore(fence);
        var configuration = new InMemoryAuthorizationConfigurationStore(fence);
        var racingGrants = Substitute.For<IGrantStore>();
        var checks = 0;
        racingGrants.HasAdministratorGrantEverAsync(default).ReturnsForAnyArgs(async _ =>
        {
            checks++;
            var observed = await grants.HasAdministratorGrantEverAsync();
            if (checks == 2)
                await grants.AppendAsync(tenant, AdministratorGrant(tenant, at));
            return observed;
        });
        var vocabulary = new InMemoryRoleVocabulary(AccessGrantAuthorizationSeed.RoleDefinitions);
        var writer = new AuthorizationDefinitionWriter(
            configuration,
            configuration,
            new AuthorizationDefinitionAdmission(vocabulary),
            new AuthorizationCapabilityBindingAdmission(),
            TestAuthorization.AllowGate(),
            racingGrants);
        var seed = new AccessGrantAuthorizationSeed(writer, configuration, racingGrants);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await seed.InstallAsync(tenant, at, AuthorizationSeedProfile.Production));
        Assert.Equal(InMemoryAuthorizationConfigurationStore.BootstrapFenceMismatchMessage, error.Message);
        Assert.False(await grants.HasAdministratorGrantEverAsync());
        Assert.Equal(1, checks);
        Assert.Null((await configuration.ReadStateAsync(DefinitionId(Permission.ContactsRead))).Definition);
    }

    [Fact]
    public void RouteGuards_RemainPresent_AndRoutesResolveAdmittedCoordinators()
    {
        var entity = Read("apps/local-node-host/Health/EntityRoutes.cs");
        var bank = Read("apps/local-node-host/Health/BankAccountRoutes.cs");
        var form = Read("apps/local-node-host/Health/FormDefinitionRoutes.cs");
        var workflow = Read("apps/local-node-host/Health/WorkflowDefinitionRoutes.cs");
        Assert.Contains("NodeEntityWriter writer", entity, StringComparison.Ordinal);
        Assert.Contains("NodeBankAccountWriter writer", bank, StringComparison.Ordinal);
        Assert.Contains("AuthorizedFormDefinitionLifecycle", form, StringComparison.Ordinal);
        Assert.Contains("AuthorizedWorkflowDefinitionLifecycle", workflow, StringComparison.Ordinal);
        Assert.Contains("RequestAuthorization.RefusalAsync", entity + bank + form, StringComparison.Ordinal);
    }

    private static void AssertBefore(string source, string first, string second)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        Assert.True(firstIndex >= 0, $"Missing '{first}'.");
        Assert.True(secondIndex >= 0, $"Missing '{second}'.");
        Assert.True(firstIndex < secondIndex, $"Expected '{first}' before '{second}'.");
    }

    private static int Count(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

    private static (AccessGrantAuthorizationSeed Seed, InMemoryAuthorizationConfigurationStore Configuration)
        Seed(InMemoryGrantStore grants)
    {
        var configuration = TestInMemoryAuthorizationStores.ConfigurationStore();
        var vocabulary = new InMemoryRoleVocabulary(AccessGrantAuthorizationSeed.RoleDefinitions);
        var writer = new AuthorizationDefinitionWriter(
            configuration,
            configuration,
            new AuthorizationDefinitionAdmission(vocabulary),
            new AuthorizationCapabilityBindingAdmission(),
            TestAuthorization.AllowGate(),
            grants);
        return (new AccessGrantAuthorizationSeed(writer, configuration, grants), configuration);
    }

    private static AccessGrant AdministratorGrant(TenantId tenant, DateTimeOffset at)
    {
        var principal = new ActorId("administrator");
        return new AccessGrant(
            GrantId.New(), tenant, principal, RoleReference.Administrator, ScopeExpression.Parse("/"),
            GrantResidency.Cache, new GrantValidity(at.AddMinutes(-1), at.AddDays(1)),
            GranterKind.Person, principal, at.AddMinutes(-1),
            new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual), principal),
            at.AddMinutes(-1));
    }

    private static async Task<AuthorizationGate> RealScopedHierarchyGateAsync(
        DateTimeOffset at,
        TenantId tenant,
        ActorId principal,
        params string[] allowedRecordIds)
    {
        var (grants, configuration) = TestInMemoryAuthorizationStores.Pair();
        var member = new RoleReference(RoleVocabularies.Domain, "member");
        var vocabulary = new InMemoryRoleVocabulary([AccessGrantAuthorizationSeed.MemberDefinition]);
        var writer = new AuthorizationDefinitionWriter(
            configuration,
            configuration,
            new AuthorizationDefinitionAdmission(vocabulary),
            new AuthorizationCapabilityBindingAdmission(),
            TestAuthorization.AllowGate(),
            grants);
        var operation = AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite);
        await writer.WriteAsync(new InstallAuthorizationDefinition(new AuthorizationCapabilityDefinition(
            new AuthorizationCapabilityDefinitionId(Guid.NewGuid()),
            AccessGrantAuthorizationSeed.PackageId,
            1,
            operation,
            new PermissionAtom(operation, ScopeExpression.Parse("/")),
            RoleBindingSet.Of(member))));
        foreach (var recordId in allowedRecordIds)
        {
            await grants.AppendAsync(tenant, new AccessGrant(
                GrantId.New(), tenant, principal, member, ScopeExpression.Parse($"/records/{recordId}"),
                GrantResidency.Cache, new GrantValidity(at.AddMinutes(-1), at.AddMinutes(1)),
                GranterKind.Person, principal, at.AddMinutes(-1),
                new GrantProvenance(
                    GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual), principal),
                at.AddMinutes(-1)));
        }
        var reader = new DefinitionJoinedAuthorizationReader(grants, configuration);
        return new AuthorizationGate(reader, new EmptyRecordStandingResolver(), configuration);
    }

    private static AuthorizationCapabilityDefinitionId DefinitionId(string operation)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"harborline.authorization-operation/v1:{operation}"));
        return new AuthorizationCapabilityDefinitionId(new Guid(digest.AsSpan(0, 16)));
    }

    private sealed class RacingBootstrapStore(
        IAuthorizationConfigurationStore inner,
        IGrantStore grants,
        AccessGrant racingGrant) : IAuthorizationConfigurationStore
    {
        private bool _raced;

        public ValueTask CommitAsync(ValidatedAuthorizationConfigurationWrite write, CancellationToken ct = default) =>
            inner.CommitAsync(write, ct);

        public async ValueTask CommitBootstrapAsync(
            ValidatedAuthorizationConfigurationWrite write,
            TenantId tenant,
            IGrantStore ignored,
            CancellationToken ct = default)
        {
            if (!_raced)
            {
                _raced = true;
                await grants.AppendAsync(tenant, racingGrant, ct: ct);
            }
            await inner.CommitBootstrapAsync(write, tenant, grants, ct);
        }
    }

    private static AuthorizationWriteContext Authority(
        string kind,
        string id,
        string scope)
    {
        _ = kind;
        _ = id;
        _ = scope;
        return new(
        new ActorId("test-actor"),
        new TenantId("test-tenant"),
        new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero));
    }

    private static JsonDocument WorkflowAuthored(string key, string version, string tenant) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            key,
            version,
            tenant,
            initialState = "start",
            states = Array.Empty<object>(),
            transitions = Array.Empty<object>(),
            triggers = Array.Empty<object>(),
            actions = Array.Empty<object>(),
            provenance = "Pack",
            owner = new { scheme = "system", value = "__sunfish" },
        }));

    private static WorkflowDefinition PackModel(
        JsonElement authored,
        PackProjectionAuthority authority)
    {
        var model = WorkflowDefinitionWireMapper.ToModel(authored, CascadeLayer.Pack);
        return new WorkflowDefinition
        {
            Envelope = model.Envelope,
            Status = model.Status,
            SubjectFormRef = model.SubjectFormRef,
            Mutability = model.Mutability,
            InitialState = model.InitialState,
            States = model.States,
            Transitions = model.Transitions,
            Triggers = model.Triggers,
            Actions = model.Actions,
            GuardRuleIds = model.GuardRuleIds,
            PackSource = new PackProjectionSource(authority.PackId, authority.PackVersion),
        };
    }

    private static async Task<PackProjectionAuthority> PackAuthority(
        AuthorizationWriteContext authority, string packId, string version)
    {
        var decision = await PackDecision(authority, packId);
        return new PackProjectionAuthority(
            decision, packId, version, authority.Tenant, authority.Principal, authority.At);
    }

    private static async Task<AuthorizationDecision> PackDecision(
        AuthorizationWriteContext authority, string packId)
    {
        var scope = ScopeExpression.Parse($"/records/{packId}");
        return await TestAuthorization.AllowGate().DecideAsync(new AuthorizationGateRequest(
            new PermissionAtom(AuthorizationOperation.Parse(Permission.PackagesOperate), scope),
            authority.Principal,
            authority.Tenant,
            new AuthorizationTarget("pack", packId, scope),
            authority.At));
    }

    private static AuthorizationDecision Decision(
        AuthorizationGateRequest request, AuthorizationVerdict verdict)
    {
        var constructor = typeof(AuthorizationDecision).GetConstructors(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single();
        return (AuthorizationDecision)constructor.Invoke(
        [
            request,
            verdict,
            Array.Empty<PermissionAtom>(),
            Array.Empty<AuthorizationAtomDerivation>(),
            Array.Empty<RecordStanding>(),
            Array.Empty<AuthorizationResolutionStep>(),
            Array.Empty<AuthorizationExcludedBinding>(),
        ]);
    }

    private static void AssertAuthorityCode(string code, PackProjectionAuthority authority)
    {
        var exception = Assert.Throws<PackProjectionAuthorityException>(authority.RequireValid);
        Assert.Equal(code, exception.Code);
    }

    private static async Task AssertRetiredAtBothLifecycles(PackProjectionAuthority authority)
    {
        using var formStore = new InMemoryFormDefinitionStore(TimeProvider.System);
        var forms = TestAuthorization.FormLifecycle(formStore, TestAuthorization.AllowGate(), TestAuthorization.RoleGate());
        var form = new FormDefinition(
            new FormDefinitionId($"retired-form-{authority.Nonce:N}"), new SemanticVersion(1, 0, 0),
            FormDefinitionStatus.Draft, authority.Tenant, IdentityRef.System, new SchemaId("schema"),
            HarborlineOverlay.Empty, null, authority.ActivationInstant, authority.ActivationInstant)
        {
            PackSource = new PackProjectionSource(authority.PackId, authority.PackVersion),
        };
        var formRefusal = await Assert.ThrowsAsync<PackProjectionAuthorityException>(async () =>
            await forms.RegisterAndPublishAsync(form, authority));
        Assert.Equal(PackProjectionAuthorityCodes.Replayed, formRefusal.Code);

        var workflowStore = new EntityStoreWorkflowDefinitionStore(
            new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System),
            new WorkflowAdmissionValidator(), Harborline.Api.Foundation.Assets.Entities.TestEntityWritePipeline.Accepting,
            TimeProvider.System);
        var workflows = TestAuthorization.WorkflowLifecycle(workflowStore, TestAuthorization.AllowGate(), TestAuthorization.RoleGate());
        using var authored = WorkflowAuthored(
            $"retired-workflow-{authority.Nonce:N}", "1.0.0", authority.Tenant.Value);
        var workflowRefusal = await Assert.ThrowsAsync<PackProjectionAuthorityException>(async () =>
            await workflows.RegisterAndPublishAsync(authored.RootElement, authority));
        Assert.Equal(PackProjectionAuthorityCodes.Replayed, workflowRefusal.Code);
    }

    private static async Task<IReadOnlyList<FormDefinition>> ReadForms(
        IFormDefinitionStore store, TenantId tenant)
    {
        var values = new List<FormDefinition>();
        await foreach (var value in store.ListByTenantAsync(tenant)) values.Add(value);
        return values;
    }

    private static async Task<IReadOnlyList<WorkflowDefinitionRecord>> ReadWorkflows(
        IWorkflowDefinitionStore store, TenantId tenant)
    {
        var values = new List<WorkflowDefinitionRecord>();
        await foreach (var value in store.ListByTenantAsync(tenant)) values.Add(value);
        return values;
    }

    private static string Read(string relative, [CallerFilePath] string file = "") =>
        File.ReadAllText(Path.Combine(RepositoryRoot(file), relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepositoryRoot(string file)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "apps"))
                && Directory.Exists(Path.Combine(directory.FullName, "packages")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }

    private static void ExecuteMergeAtomicCallback(IHierarchyCompositeUnitOfWork transaction)
    {
        transaction.GetChildrenNotEndedAsync(
                Arg.Any<EntityId>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(EmptyEdges());
        transaction.ExecuteAtomicAsync(
                Arg.Any<Func<CancellationToken, Task<MergeResult>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<MergeResult>>>(0)(
                call.ArgAt<CancellationToken>(1)));
    }

    private static async IAsyncEnumerable<EntityEdge> EmptyEdges()
    {
        yield break;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class AdvancingTimeProvider(DateTimeOffset first) : TimeProvider
    {
        private int _reads;

        internal int ReadCount => Volatile.Read(ref _reads);

        public override DateTimeOffset GetUtcNow() =>
            first.AddMinutes(Interlocked.Increment(ref _reads) - 1);
    }

    private sealed class MergeSnapshotRaceUnitOfWork : IHierarchyCompositeUnitOfWork
    {
        private readonly object _gate = new();
        private readonly List<EntityEdge> _edges = [];
        private readonly AsyncLocal<int> _atomicDepth = new();
        private readonly TaskCompletionSource _raceWindow =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _nextEdgeId;
        private int _raceStarted;

        internal Task RaceWindow => _raceWindow.Task;

        internal void Resume() => _resume.TrySetResult();

        internal IReadOnlyList<EntityEdge> ActiveChildEdges(DateTimeOffset at)
        {
            lock (_gate)
                return _edges
                    .Where(edge => edge.Kind == EdgeKind.ChildOf && edge.Validity.IsValidAt(at))
                    .ToArray();
        }

        public async Task<T> ExecuteAtomicAsync<T>(
            Func<CancellationToken, Task<T>> action,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(action);
            _atomicDepth.Value++;
            try
            {
                if (BeginRace())
                    await _resume.Task.WaitAsync(ct);
                return await action(ct);
            }
            finally
            {
                _atomicDepth.Value--;
            }
        }

        public Task<EntityEdge> AddEdgeAsync(
            EntityId from,
            EntityId to,
            EdgeKind kind,
            DateTimeOffset validFrom,
            JsonDocument? metadata = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var edge = new EntityEdge(
                    Interlocked.Increment(ref _nextEdgeId), from, to, kind,
                    new TemporalRange(validFrom, null), metadata);
                _edges.Add(edge);
                return Task.FromResult(edge);
            }
        }

        public Task InvalidateEdgeAsync(
            long edgeId,
            DateTimeOffset validTo,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var index = _edges.FindIndex(edge => edge.Id == edgeId);
                if (index < 0) throw new InvalidOperationException($"Edge {edgeId} not found.");
                var edge = _edges[index];
                _edges[index] = edge with
                {
                    Validity = new TemporalRange(edge.Validity.ValidFrom, validTo),
                };
            }
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<EntityEdge> GetChildrenAsync(
            EntityId parent,
            DateTimeOffset? asOf = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            EntityEdge[] snapshot;
            var at = asOf ?? DateTimeOffset.MaxValue;
            lock (_gate)
                snapshot = _edges
                    .Where(edge => edge.Kind == EdgeKind.ChildOf
                        && edge.To == parent
                        && edge.Validity.IsValidAt(at))
                    .ToArray();
            if (_atomicDepth.Value == 0 && BeginRace())
                await _resume.Task.WaitAsync(ct);
            foreach (var edge in snapshot)
            {
                ct.ThrowIfCancellationRequested();
                yield return edge;
            }
        }

        public async IAsyncEnumerable<EntityEdge> GetChildrenNotEndedAsync(
            EntityId parent,
            DateTimeOffset asOf,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            EntityEdge[] snapshot;
            lock (_gate)
                snapshot = _edges
                    .Where(edge => edge.Kind == EdgeKind.ChildOf
                        && edge.To == parent
                        && (edge.Validity.ValidTo is null || edge.Validity.ValidTo > asOf))
                    .ToArray();
            foreach (var edge in snapshot)
            {
                ct.ThrowIfCancellationRequested();
                yield return edge;
                await Task.Yield();
            }
        }

        public IAsyncEnumerable<EntityEdge> GetParentsAsync(
            EntityId child,
            DateTimeOffset? asOf = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public IAsyncEnumerable<ClosureEntry> GetAncestorsAsync(
            EntityId descendant,
            DateTimeOffset? asOf = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public IAsyncEnumerable<ClosureEntry> GetDescendantsAsync(
            EntityId ancestor,
            DateTimeOffset? asOf = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TemporalSnapshot> GetSubtreeAsync(
            EntityId root,
            DateTimeOffset? asOf = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        private bool BeginRace()
        {
            if (Interlocked.CompareExchange(ref _raceStarted, 1, 0) != 0)
                return false;
            _raceWindow.TrySetResult();
            return true;
        }
    }

    private static CreateOptions Options(
        string localPart,
        ActorId actor,
        TenantId tenant,
        DateTimeOffset at) =>
        new("entity", "test", localPart, actor, tenant, at, ExplicitLocalPart: localPart);

    private sealed class AppendThenThrowAudit(IAuditLog inner) : IAuditLog
    {
        public async Task<AuditId> AppendAsync(AuditAppend append, CancellationToken ct = default)
        {
            await inner.AppendAsync(append, ct);
            throw new IOException("late audit persistence failure");
        }

        public IAsyncEnumerable<Harborline.Api.Foundation.Assets.Audit.AuditRecord> QueryAsync(
            Harborline.Api.Foundation.Assets.Audit.AuditQuery query,
            CancellationToken ct = default) =>
            inner.QueryAsync(query, ct);

        public Task<bool> VerifyChainAsync(EntityId entity, CancellationToken ct = default) =>
            inner.VerifyChainAsync(entity, ct);
    }

    private sealed class BlockingAppendThenThrowAudit(IAuditLog inner) : IAuditLog
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _fail = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Reached => _reached.Task;

        internal void Fail() => _fail.TrySetResult();

        public async Task<AuditId> AppendAsync(AuditAppend append, CancellationToken ct = default)
        {
            var id = await inner.AppendAsync(append, ct);
            _reached.TrySetResult();
            await _fail.Task.WaitAsync(ct);
            throw new IOException("late audit persistence failure");
        }

        public IAsyncEnumerable<Harborline.Api.Foundation.Assets.Audit.AuditRecord> QueryAsync(
            Harborline.Api.Foundation.Assets.Audit.AuditQuery query,
            CancellationToken ct = default) => inner.QueryAsync(query, ct);

        public Task<bool> VerifyChainAsync(EntityId entity, CancellationToken ct = default) =>
            inner.VerifyChainAsync(entity, ct);
    }

    private sealed class RejectNthValidation : IEntityValidator
    {
        private int _calls;
        private int _rejectOn = int.MaxValue;
        internal int RejectOn
        {
            get => _rejectOn;
            set
            {
                _calls = 0;
                _rejectOn = value;
            }
        }

        public Task ValidateAsync(SchemaId schema, JsonDocument body, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) == _rejectOn)
                throw new EntityValidationException(
                    EntityValidationException.BodyInvalid, "late target refused", ["/name"]);
            return Task.CompletedTask;
        }
    }

    private static async Task<byte[]> CreateValidSignedPackAsync()
    {
        var signer = new Ed25519Signer(KeyPair.Generate());
        var exporter = new PackExporter(
            new PackContentCanonicalizer(),
            new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            new PackFileCodec(),
            TimeProvider.System);
        var exported = await exporter.ExportAsync(new PackExportRequest(
            "pack", "1.0.0", "Authorization stage pack", "Valid signed denial fixture",
            PackScopeTier.Horizontal,
            [new PackContentSource("pack-form", PackContentKind.FormDefinition, "1.0.0",
                new JsonObject { ["title"] = "Authorization stage" })],
            Array.Empty<PackDependencyRef>(),
            Array.Empty<string>(),
            1,
            Dcp: DomainComplianceProfile.General("authorization-stage")), signer);
        Assert.True(exported.Succeeded);
        return exported.FileBytes!;
    }

    private sealed class ThrowingPackVerifier : IPackVerifier
    {
        internal int CallCount { get; private set; }

        public PackVerificationResult Verify(ReadOnlySpan<byte> packFileBytes, IPackTrustStore trustStore)
        {
            CallCount++;
            throw new InvalidOperationException("Verifier ran before authorization.");
        }
    }

    private sealed class ThrowingPackInstallStore : IPackInstallMutationStore
    {
        internal int CallCount { get; private set; }

        private T Touched<T>()
        {
            CallCount++;
            throw new InvalidOperationException("Pack store ran before authorization.");
        }

        private void Touched()
        {
            CallCount++;
            throw new InvalidOperationException("Pack store ran before authorization.");
        }

        public InstalledPack? GetActive(TenantId tenant, string packKey) => Touched<InstalledPack?>();
        public InstalledPack? GetVersion(TenantId tenant, string packKey, string version) => Touched<InstalledPack?>();
        public IReadOnlyList<InstalledPack> ListInstalled(TenantId tenant) => Touched<IReadOnlyList<InstalledPack>>();
        public PackInstallWatermark? GetWatermark(TenantId tenant, string packKey) => Touched<PackInstallWatermark?>();
        public IReadOnlyList<PackTenantOverride> GetOverrides(TenantId tenant, string packKey) => Touched<IReadOnlyList<PackTenantOverride>>();
        public void SaveOverride(TenantId tenant, string packKey, PackTenantOverride tenantOverride) => Touched();
        public void Commit(PackInstallTransaction transaction) => Touched();
        public void Activate(TenantId tenant, string packKey, string version) => Touched();
        public void Deactivate(TenantId tenant, string packKey, string version) => Touched();
        public IReadOnlyDictionary<string, string> GetKeyOwnership(TenantId tenant) => Touched<IReadOnlyDictionary<string, string>>();
        public void RecordKeyOwnership(TenantId tenant, string contentKey, string owningPackKey) => Touched();
    }

    private sealed class ThrowingPackContentAdmission : IPackContentAdmission
    {
        internal int CallCount { get; private set; }

        public PackAdmissionResult Admit(IReadOnlyList<PackComposedItem> composed, TenantId tenant)
        {
            CallCount++;
            throw new InvalidOperationException("Pack admission ran before authorization.");
        }
    }

    private sealed class ThrowingPackPlatformCompatibility : IPackPlatformCompatibility
    {
        internal int CallCount { get; private set; }
        public string PlatformVersion => Touched<string>();
        public IReadOnlySet<string> Provides => Touched<IReadOnlySet<string>>();

        private T Touched<T>()
        {
            CallCount++;
            throw new InvalidOperationException("Pack platform compatibility ran before authorization.");
        }
    }

    private sealed class CapturingProjectionDispatcher : IPackProjectionDispatcher
    {
        internal List<PackProjectionAuthority> Captured { get; } = [];

        public object? Project(PackProjectionAuthority authority, CancellationToken cancellationToken = default)
        {
            Captured.Add(authority);
            return null;
        }
    }
}
