using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Banking.Services;
using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Assets.Hierarchy;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.LocalNodeHost.Data.Entities;
using Harborline.Api.LocalNodeHost.Data.Banking;
using Harborline.Api.LocalNodeHost.Data.Packs;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

public sealed class RawMutationPortSymbolInventoryTests
{
    private sealed record AllowRow(
        string Path,
        string Symbol,
        string Target,
        int Ordinal,
        string Classification,
        string Reason);
    internal sealed record CallSite(string Path, string Symbol, int Line, string Target, int Ordinal);

    private static readonly AllowRow[] Allowed =
    [
        new("packages/foundation-packs/Install/PackInstaller.cs", "Harborline.Api.Foundation.Packs.Install.PackInstaller.Narrow(Harborline.Api.Foundation.Packs.Install.PackInstallContext,System.String,System.String,System.Text.Json.Nodes.JsonNode,Harborline.Api.Foundation.Authorization.AuthorizationDecision): Harborline.Api.Foundation.Packs.Install.PackNarrowingOutcome", "Harborline.Api.Foundation.Packs.Install.IPackInstallMutationStore.SaveOverride(Harborline.Foundation.Assets.Common.TenantId,System.String,Harborline.Api.Foundation.Packs.Install.PackTenantOverride): System.Void", 0, "admitted coordinator", "narrowing writes through the existing override mutation seam only after validating the carried caller decision and rejecting widening"),
        new("apps/local-node-host/Data/Entities/NodeEntityWriter.cs", "Harborline.Api.LocalNodeHost.Data.Entities.NodeEntityWriter.CreateAsync(Harborline.Api.Foundation.Assets.Common.SchemaId,System.Text.Json.JsonDocument,Harborline.Api.Foundation.Assets.Entities.CreateOptions,Harborline.Api.Foundation.Authorization.AuthorizationWriteContext,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask`1[Harborline.Api.Foundation.Assets.Common.EntityId]", "Harborline.Api.Foundation.Assets.Entities.IEntityMutationStore.CreateAsync(Harborline.Api.Foundation.Assets.Common.SchemaId,System.Text.Json.JsonDocument,Harborline.Api.Foundation.Assets.Entities.CreateOptions,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Common.EntityId]", 0, "admitted coordinator", "entity create after the carried decision is allowed"),
        new("apps/local-node-host/Data/Entities/NodeEntityWriter.cs", "Harborline.Api.LocalNodeHost.Data.Entities.NodeEntityWriter.DeleteAsync(Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Entities.DeleteOptions,Harborline.Api.Foundation.Authorization.AuthorizationWriteContext,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask", "Harborline.Api.Foundation.Assets.Entities.IEntityMutationStore.DeleteAsync(Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Entities.DeleteOptions,System.Threading.CancellationToken): System.Threading.Tasks.Task", 0, "admitted coordinator", "entity delete after the carried decision is allowed"),
        new("apps/local-node-host/Data/Entities/NodeEntityWriter.cs", "Harborline.Api.LocalNodeHost.Data.Entities.NodeEntityWriter.UpdateAsync(Harborline.Api.Foundation.Assets.Common.EntityId,System.Text.Json.JsonDocument,Harborline.Api.Foundation.Assets.Entities.UpdateOptions,Harborline.Api.Foundation.Authorization.AuthorizationWriteContext,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask`1[Harborline.Api.Foundation.Assets.Common.VersionId]", "Harborline.Api.Foundation.Assets.Entities.IEntityMutationStore.UpdateAsync(Harborline.Api.Foundation.Assets.Common.EntityId,System.Text.Json.JsonDocument,Harborline.Api.Foundation.Assets.Entities.UpdateOptions,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Common.VersionId]", 0, "admitted coordinator", "entity update after the carried decision is allowed"),
        new("apps/local-node-host/Data/Entities/NodeHierarchyCompositeCoordinator.cs", "Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator+<>c__DisplayClass_0.<ReparentAsync>b__0(System.Threading.CancellationToken): System.Threading.Tasks.Task`1[System.Boolean]", "Harborline.Api.Foundation.Assets.Hierarchy.IHierarchyMutationStore.InvalidateEdgeAsync(System.Int64,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task", 0, "composite transaction", "reparent invalidates the old edge inside the completely admitted unit of work"),
        new("apps/local-node-host/Data/Entities/NodeHierarchyCompositeCoordinator.cs", "Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator+<>c__DisplayClass_0.<ReparentAsync>b__0(System.Threading.CancellationToken): System.Threading.Tasks.Task`1[System.Boolean]", "Harborline.Api.Foundation.Assets.Hierarchy.IHierarchyMutationStore.AddEdgeAsync(Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Hierarchy.EdgeKind,System.DateTimeOffset,System.Text.Json.JsonDocument,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Hierarchy.EntityEdge]", 0, "composite transaction", "reparent adds the replacement edge inside the completely admitted unit of work"),
        new("apps/local-node-host/Data/Entities/NodeHierarchyCompositeCoordinator.cs", "Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator+<>c__DisplayClass_0.<ReparentAsync>b__0(System.Threading.CancellationToken): System.Threading.Tasks.Task`1[System.Boolean]", "Harborline.Api.Foundation.Assets.Hierarchy.IHierarchyMutationStore.InvalidateEdgeAsync(System.Int64,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task", 1, "composite transaction", "reparent retains the displaced edge's finite upper bound"),
        new("apps/local-node-host/Data/Entities/NodeHierarchyCompositeCoordinator.cs", "Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator.ApplyMergeAsync(Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator+CompositeAuthorization,System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Common.EntityId],Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Common.SchemaId,System.Text.Json.JsonDocument,Harborline.Api.Foundation.Assets.Entities.CreateOptions,System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Hierarchy.EntityEdge],System.String,Harborline.Api.Foundation.Assets.Common.ActorId,Harborline.Foundation.Assets.Common.TenantId,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Hierarchy.MergeResult]", "Harborline.Api.Foundation.Assets.Entities.IEntityMutationStore.CreateAsync(Harborline.Api.Foundation.Assets.Common.SchemaId,System.Text.Json.JsonDocument,Harborline.Api.Foundation.Assets.Entities.CreateOptions,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Common.EntityId]", 0, "composite transaction", "merge creates its admitted replacement"),
        new("apps/local-node-host/Data/Entities/NodeHierarchyCompositeCoordinator.cs", "Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator.ApplyMergeAsync(Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator+CompositeAuthorization,System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Common.EntityId],Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Common.SchemaId,System.Text.Json.JsonDocument,Harborline.Api.Foundation.Assets.Entities.CreateOptions,System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Hierarchy.EntityEdge],System.String,Harborline.Api.Foundation.Assets.Common.ActorId,Harborline.Foundation.Assets.Common.TenantId,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Hierarchy.MergeResult]", "Harborline.Api.Foundation.Assets.Hierarchy.IHierarchyMutationStore.InvalidateEdgeAsync(System.Int64,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task", 0, "composite transaction", "merge invalidates a pre-admitted child edge"),
        new("apps/local-node-host/Data/Entities/NodeHierarchyCompositeCoordinator.cs", "Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator.ApplyMergeAsync(Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator+CompositeAuthorization,System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Common.EntityId],Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Common.SchemaId,System.Text.Json.JsonDocument,Harborline.Api.Foundation.Assets.Entities.CreateOptions,System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Hierarchy.EntityEdge],System.String,Harborline.Api.Foundation.Assets.Common.ActorId,Harborline.Foundation.Assets.Common.TenantId,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Hierarchy.MergeResult]", "Harborline.Api.Foundation.Assets.Hierarchy.IHierarchyMutationStore.AddEdgeAsync(Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Hierarchy.EdgeKind,System.DateTimeOffset,System.Text.Json.JsonDocument,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Hierarchy.EntityEdge]", 0, "composite transaction", "merge reparents an admitted child"),
        new("apps/local-node-host/Data/Entities/NodeHierarchyCompositeCoordinator.cs", "Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator.ApplyMergeAsync(Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator+CompositeAuthorization,System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Common.EntityId],Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Common.SchemaId,System.Text.Json.JsonDocument,Harborline.Api.Foundation.Assets.Entities.CreateOptions,System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Hierarchy.EntityEdge],System.String,Harborline.Api.Foundation.Assets.Common.ActorId,Harborline.Foundation.Assets.Common.TenantId,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Hierarchy.MergeResult]", "Harborline.Api.Foundation.Assets.Hierarchy.IHierarchyMutationStore.InvalidateEdgeAsync(System.Int64,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task", 1, "composite transaction", "merge retains the displaced edge's finite upper bound"),
        new("apps/local-node-host/Data/Entities/NodeHierarchyCompositeCoordinator.cs", "Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator.ApplyMergeAsync(Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator+CompositeAuthorization,System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Common.EntityId],Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Common.SchemaId,System.Text.Json.JsonDocument,Harborline.Api.Foundation.Assets.Entities.CreateOptions,System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Hierarchy.EntityEdge],System.String,Harborline.Api.Foundation.Assets.Common.ActorId,Harborline.Foundation.Assets.Common.TenantId,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Hierarchy.MergeResult]", "Harborline.Api.Foundation.Assets.Hierarchy.IHierarchyMutationStore.AddEdgeAsync(Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Hierarchy.EdgeKind,System.DateTimeOffset,System.Text.Json.JsonDocument,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Hierarchy.EntityEdge]", 1, "composite transaction", "merge links an admitted source and replacement"),
        new("apps/local-node-host/Data/Entities/NodeHierarchyCompositeCoordinator.cs", "Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator.ApplyMergeAsync(Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator+CompositeAuthorization,System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Common.EntityId],Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Common.SchemaId,System.Text.Json.JsonDocument,Harborline.Api.Foundation.Assets.Entities.CreateOptions,System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Hierarchy.EntityEdge],System.String,Harborline.Api.Foundation.Assets.Common.ActorId,Harborline.Foundation.Assets.Common.TenantId,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Hierarchy.MergeResult]", "Harborline.Api.Foundation.Assets.Entities.IEntityMutationStore.DeleteAsync(Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Entities.DeleteOptions,System.Threading.CancellationToken): System.Threading.Tasks.Task", 0, "composite transaction", "merge removes an admitted source"),
        new("apps/local-node-host/Data/Entities/NodeHierarchyCompositeCoordinator.cs", "Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator.ApplySplitAsync(Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator+CompositeAuthorization,Harborline.Api.Foundation.Assets.Common.EntityId,System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Hierarchy.SplitTarget],System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Common.EntityId],System.Collections.Generic.IReadOnlyDictionary`2[Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Common.EntityId],System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Hierarchy.EntityEdge],System.String,Harborline.Api.Foundation.Assets.Common.ActorId,Harborline.Foundation.Assets.Common.TenantId,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Hierarchy.SplitResult]", "Harborline.Api.Foundation.Assets.Entities.IEntityMutationStore.CreateAsync(Harborline.Api.Foundation.Assets.Common.SchemaId,System.Text.Json.JsonDocument,Harborline.Api.Foundation.Assets.Entities.CreateOptions,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Common.EntityId]", 0, "composite transaction", "split creates an admitted replacement"),
        new("apps/local-node-host/Data/Entities/NodeHierarchyCompositeCoordinator.cs", "Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator.ApplySplitAsync(Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator+CompositeAuthorization,Harborline.Api.Foundation.Assets.Common.EntityId,System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Hierarchy.SplitTarget],System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Common.EntityId],System.Collections.Generic.IReadOnlyDictionary`2[Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Common.EntityId],System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Hierarchy.EntityEdge],System.String,Harborline.Api.Foundation.Assets.Common.ActorId,Harborline.Foundation.Assets.Common.TenantId,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Hierarchy.SplitResult]", "Harborline.Api.Foundation.Assets.Hierarchy.IHierarchyMutationStore.InvalidateEdgeAsync(System.Int64,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task", 0, "composite transaction", "split invalidates a pre-admitted child edge"),
        new("apps/local-node-host/Data/Entities/NodeHierarchyCompositeCoordinator.cs", "Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator.ApplySplitAsync(Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator+CompositeAuthorization,Harborline.Api.Foundation.Assets.Common.EntityId,System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Hierarchy.SplitTarget],System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Common.EntityId],System.Collections.Generic.IReadOnlyDictionary`2[Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Common.EntityId],System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Hierarchy.EntityEdge],System.String,Harborline.Api.Foundation.Assets.Common.ActorId,Harborline.Foundation.Assets.Common.TenantId,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Hierarchy.SplitResult]", "Harborline.Api.Foundation.Assets.Hierarchy.IHierarchyMutationStore.AddEdgeAsync(Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Hierarchy.EdgeKind,System.DateTimeOffset,System.Text.Json.JsonDocument,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Hierarchy.EntityEdge]", 0, "composite transaction", "split reparents an admitted child"),
        new("apps/local-node-host/Data/Entities/NodeHierarchyCompositeCoordinator.cs", "Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator.ApplySplitAsync(Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator+CompositeAuthorization,Harborline.Api.Foundation.Assets.Common.EntityId,System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Hierarchy.SplitTarget],System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Common.EntityId],System.Collections.Generic.IReadOnlyDictionary`2[Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Common.EntityId],System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Hierarchy.EntityEdge],System.String,Harborline.Api.Foundation.Assets.Common.ActorId,Harborline.Foundation.Assets.Common.TenantId,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Hierarchy.SplitResult]", "Harborline.Api.Foundation.Assets.Hierarchy.IHierarchyMutationStore.InvalidateEdgeAsync(System.Int64,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task", 1, "composite transaction", "split retains the displaced edge's finite upper bound"),
        new("apps/local-node-host/Data/Entities/NodeHierarchyCompositeCoordinator.cs", "Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator.ApplySplitAsync(Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator+CompositeAuthorization,Harborline.Api.Foundation.Assets.Common.EntityId,System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Hierarchy.SplitTarget],System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Common.EntityId],System.Collections.Generic.IReadOnlyDictionary`2[Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Common.EntityId],System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Hierarchy.EntityEdge],System.String,Harborline.Api.Foundation.Assets.Common.ActorId,Harborline.Foundation.Assets.Common.TenantId,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Hierarchy.SplitResult]", "Harborline.Api.Foundation.Assets.Hierarchy.IHierarchyMutationStore.AddEdgeAsync(Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Hierarchy.EdgeKind,System.DateTimeOffset,System.Text.Json.JsonDocument,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Hierarchy.EntityEdge]", 1, "composite transaction", "split links admitted source and replacement"),
        new("apps/local-node-host/Data/Entities/NodeHierarchyCompositeCoordinator.cs", "Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator.ApplySplitAsync(Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator+CompositeAuthorization,Harborline.Api.Foundation.Assets.Common.EntityId,System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Hierarchy.SplitTarget],System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Common.EntityId],System.Collections.Generic.IReadOnlyDictionary`2[Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Common.EntityId],System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Hierarchy.EntityEdge],System.String,Harborline.Api.Foundation.Assets.Common.ActorId,Harborline.Foundation.Assets.Common.TenantId,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Hierarchy.SplitResult]", "Harborline.Api.Foundation.Assets.Entities.IEntityMutationStore.DeleteAsync(Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Entities.DeleteOptions,System.Threading.CancellationToken): System.Threading.Tasks.Task", 0, "composite transaction", "split removes the admitted source"),
        new("apps/local-node-host/Data/Entities/NodeHierarchyCompositeCoordinator.cs", "Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator.MergeAsync(System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Common.EntityId],Harborline.Api.Foundation.Assets.Common.SchemaId,System.Text.Json.JsonDocument,Harborline.Api.Foundation.Assets.Entities.CreateOptions,System.String,Harborline.Api.Foundation.Assets.Common.ActorId,Harborline.Foundation.Assets.Common.TenantId,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Hierarchy.MergeResult]", "Harborline.Api.Foundation.Assets.Hierarchy.IHierarchyCompositeUnitOfWork.ExecuteAtomicAsync``1[Harborline.Api.Foundation.Assets.Hierarchy.MergeResult](System.Func`2[System.Threading.CancellationToken,System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Hierarchy.MergeResult]],System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Hierarchy.MergeResult]", 0, "atomic admission scope", "the merge snapshots and admits its complete target set inside one atomic unit"),
        new("apps/local-node-host/Data/Entities/NodeHierarchyCompositeCoordinator.cs", "Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator.ReparentAsync(Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Common.EntityId,System.String,Harborline.Api.Foundation.Assets.Common.ActorId,Harborline.Foundation.Assets.Common.TenantId,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task", "Harborline.Api.Foundation.Assets.Hierarchy.IHierarchyCompositeUnitOfWork.ExecuteAtomicAsync``1[System.Boolean](System.Func`2[System.Threading.CancellationToken,System.Threading.Tasks.Task`1[System.Boolean]],System.Threading.CancellationToken): System.Threading.Tasks.Task`1[System.Boolean]", 0, "admitted coordinator", "the complete reparent decision set enters one atomic unit"),
        new("apps/local-node-host/Data/Entities/NodeHierarchyCompositeCoordinator.cs", "Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator.SplitAsync(Harborline.Api.Foundation.Assets.Common.EntityId,System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Hierarchy.SplitTarget],System.Collections.Generic.IReadOnlyDictionary`2[Harborline.Api.Foundation.Assets.Common.EntityId,Harborline.Api.Foundation.Assets.Common.EntityId],System.String,Harborline.Api.Foundation.Assets.Common.ActorId,Harborline.Foundation.Assets.Common.TenantId,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Hierarchy.SplitResult]", "Harborline.Api.Foundation.Assets.Hierarchy.IHierarchyCompositeUnitOfWork.ExecuteAtomicAsync``1[Harborline.Api.Foundation.Assets.Hierarchy.SplitResult](System.Func`2[System.Threading.CancellationToken,System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Hierarchy.SplitResult]],System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Hierarchy.SplitResult]", 0, "admitted coordinator", "the complete split decision set enters one atomic unit"),
        new("apps/local-node-host/Data/Financial/NodeBankAccountWriter.cs", "Harborline.Api.LocalNodeHost.Data.Financial.NodeBankAccountWriter.ArchiveAsync(Harborline.Api.Blocks.Banking.Models.BankAccountId,Harborline.Api.Foundation.Authorization.AuthorizationWriteContext,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask`1[Harborline.Api.Blocks.Banking.Models.BankAccount]", "Harborline.Api.Blocks.Banking.Services.IBankAccountMutationRepository.UpdateAsync(Harborline.Api.Blocks.Banking.Models.BankAccount,System.Threading.CancellationToken): System.Threading.Tasks.Task", 0, "admitted coordinator", "bank archive after the carried decision is allowed"),
        new("apps/local-node-host/Data/Financial/NodeBankAccountWriter.cs", "Harborline.Api.LocalNodeHost.Data.Financial.NodeBankAccountWriter.CreateAsync(Harborline.Api.Blocks.Banking.Models.BankAccount,Harborline.Api.Foundation.Authorization.AuthorizationWriteContext,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask`1[Harborline.Api.Blocks.Banking.Models.BankAccount]", "Harborline.Api.Blocks.Banking.Services.IBankAccountMutationRepository.AddAsync(Harborline.Api.Blocks.Banking.Models.BankAccount,System.Threading.CancellationToken): System.Threading.Tasks.Task", 0, "admitted coordinator", "bank create after the carried decision is allowed"),
        new("apps/local-node-host/Data/Financial/NodeBankAccountWriter.cs", "Harborline.Api.LocalNodeHost.Data.Financial.NodeBankAccountWriter.SetOpeningBalanceAsync(Harborline.Api.Blocks.Banking.Models.BankAccountId,System.Decimal,Harborline.Api.Foundation.Assets.Common.Instant,Harborline.Api.Foundation.Authorization.AuthorizationWriteContext,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask`1[Harborline.Api.Blocks.Banking.Models.BankAccount]", "Harborline.Api.Blocks.Banking.Services.IBankAccountMutationRepository.UpdateAsync(Harborline.Api.Blocks.Banking.Models.BankAccount,System.Threading.CancellationToken): System.Threading.Tasks.Task", 0, "admitted coordinator", "bank balance update after the carried decision is allowed"),
        new("apps/local-node-host/Data/Identity/AuthorizedGrantRevocationWriter.cs", "Harborline.Api.LocalNodeHost.Data.Identity.AuthorizedGrantRevocationWriter.RevokeAsync(Harborline.Foundation.Assets.Common.TenantId,Harborline.Api.Blocks.AccessGrant.GrantId,Harborline.Api.Blocks.AccessGrant.GrantRevocation,Harborline.Api.Foundation.Authorization.AuthorizationDecision,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Blocks.AccessGrant.AccessGrant]", "Harborline.Api.Blocks.AccessGrant.IGrantStore.RevokeAsync(Harborline.Foundation.Assets.Common.TenantId,Harborline.Api.Blocks.AccessGrant.GrantId,Harborline.Api.Blocks.AccessGrant.GrantRevocation,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Blocks.AccessGrant.AccessGrant]", 0, "decision-bearing adapter", "admin revocation passes the same allowed decision into its grant writer"),
        new("packages/blocks-workflow/src/durable/AuthorizedWorkflowDefinitionLifecycle.cs", "Harborline.Api.Blocks.Workflow.Durable.AuthorizedWorkflowDefinitionLifecycle+EntityWriterBackend.RegisterAsync(Harborline.Api.Blocks.Workflow.Durable.WorkflowDefinition,System.Text.Json.JsonElement,Harborline.Api.Blocks.Workflow.Durable.WorkflowDefinitionRegistrationOptions,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask`1[Harborline.Api.Blocks.Workflow.Durable.WorkflowDefinitionRecord]", "Harborline.Api.Foundation.Assets.Entities.IEntityMutationStore.CreateAsync(Harborline.Api.Foundation.Assets.Common.SchemaId,System.Text.Json.JsonDocument,Harborline.Api.Foundation.Assets.Entities.CreateOptions,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Common.EntityId]", 0, "persistence adapter", "authorized workflow lifecycle owns its entity persistence backend"),
        new("packages/foundation-forms-engine/IAuthorizedFormEntityWriter.cs", "Harborline.Api.Foundation.Forms.Engine.AuthorizedFormEntityWriter.CreateAsync(Harborline.Api.Foundation.Forms.Models.FormDefinitionId,Harborline.Api.Foundation.Assets.Common.SchemaId,System.Text.Json.JsonDocument,Harborline.Api.Foundation.Assets.Entities.CreateOptions,Harborline.Api.Foundation.Authorization.AuthorizationDecision,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Common.EntityId]", "Harborline.Api.Foundation.Assets.Entities.IEntityMutationStore.CreateAsync(Harborline.Api.Foundation.Assets.Common.SchemaId,System.Text.Json.JsonDocument,Harborline.Api.Foundation.Assets.Entities.CreateOptions,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Common.EntityId]", 0, "decision-bearing adapter", "form engine passes the same allowed decision into its writer"),
        new("packages/foundation-forms/AuthorizedFormDefinitionLifecycle.cs", "Harborline.Api.Foundation.Forms.AuthorizedFormDefinitionLifecycle+EntityWriterBackend.RegisterAsync(Harborline.Api.Foundation.Forms.Models.FormDefinition,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask`1[Harborline.Api.Foundation.Forms.Models.FormDefinition]", "Harborline.Api.Foundation.Assets.Entities.IEntityMutationStore.CreateAsync(Harborline.Api.Foundation.Assets.Common.SchemaId,System.Text.Json.JsonDocument,Harborline.Api.Foundation.Assets.Entities.CreateOptions,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Common.EntityId]", 0, "persistence adapter", "authorized form lifecycle owns its entity persistence backend"),
        new("packages/foundation-packs/Install/PackInstaller.cs", "Harborline.Api.Foundation.Packs.Install.PackInstaller.ActivateCore(Harborline.Foundation.Assets.Common.TenantId,System.String,System.String,System.DateTimeOffset,System.String,System.Collections.Generic.IReadOnlyDictionary`2[System.String,System.String],Harborline.Api.Foundation.Packs.Install.PackProjectionAuthority&): Harborline.Api.Foundation.Packs.Install.PackActivationOutcome", "Harborline.Api.Foundation.Packs.Install.IPackInstallMutationStore.RecordKeyOwnership(Harborline.Foundation.Assets.Common.TenantId,System.String,System.String): System.Void", 0, "admitted coordinator", "activation persists its reviewed ownership choices"),
        new("packages/foundation-packs/Install/PackInstaller.cs", "Harborline.Api.Foundation.Packs.Install.PackInstaller.ActivateCore(Harborline.Foundation.Assets.Common.TenantId,System.String,System.String,System.DateTimeOffset,System.String,System.Collections.Generic.IReadOnlyDictionary`2[System.String,System.String],Harborline.Api.Foundation.Packs.Install.PackProjectionAuthority&): Harborline.Api.Foundation.Packs.Install.PackActivationOutcome", "Harborline.Api.Foundation.Packs.Install.IPackProjectionAdmissionStore.ActivateAndRecordProjectionAdmission(Harborline.Foundation.Assets.Common.TenantId,System.String,System.String,Harborline.Api.Foundation.Packs.Install.PackProjectionAdmission): System.Void", 0, "admitted coordinator", "activation atomically records its admission evidence"),
        new("packages/foundation-packs/Install/PackInstaller.cs", "Harborline.Api.Foundation.Packs.Install.PackInstaller.DeactivateCore(Harborline.Foundation.Assets.Common.TenantId,System.String,System.String,System.DateTimeOffset,System.String,Harborline.Api.Foundation.Packs.Install.PackProjectionAuthority&): Harborline.Api.Foundation.Packs.Install.PackDeactivationOutcome", "Harborline.Api.Foundation.Packs.Install.IPackProjectionAdmissionStore.DeactivateAndRecordProjectionAdmission(Harborline.Foundation.Assets.Common.TenantId,System.String,System.String,Harborline.Api.Foundation.Packs.Install.PackProjectionAdmission): System.Void", 0, "admitted coordinator", "deactivation atomically records its admission evidence"),
        new("packages/foundation-packs/Install/PackInstaller.cs", "Harborline.Api.Foundation.Packs.Install.PackInstaller.Harborline.Api.Foundation.Packs.Install.IPackProjectionReconciler.ReconcilePending(System.Threading.CancellationToken): System.Void", "Harborline.Api.Foundation.Packs.Install.IPackProjectionAdmissionStore.ListIncompleteProjectionAdmissions(): System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Packs.Install.PackProjectionAdmission]", 0, "reconciliation", "reconciler reads incomplete admission evidence"),
        new("packages/foundation-packs/Install/PackInstaller.cs", "Harborline.Api.Foundation.Packs.Install.PackInstaller.Harborline.Api.Foundation.Packs.Install.IPackProjectionReconciler.ReconcilePending(System.Threading.CancellationToken): System.Void", "Harborline.Api.Foundation.Packs.Install.IPackProjectionAdmissionStore.MarkProjectionCompleted(System.Guid): System.Void", 0, "reconciliation", "reconciler completes evidence only after projection"),
        new("packages/foundation-packs/Install/PackInstaller.cs", "Harborline.Api.Foundation.Packs.Install.PackInstaller.Install(System.ReadOnlySpan`1[System.Byte],Harborline.Api.Foundation.Packs.Install.PackInstallContext): Harborline.Api.Foundation.Packs.Install.PackInstallOutcome", "Harborline.Api.Foundation.Packs.Install.IPackInstallMutationStore.Commit(Harborline.Api.Foundation.Packs.Install.PackInstallTransaction): System.Void", 0, "admitted coordinator", "install commits only after all admission stages"),
        new("packages/foundation-packs/Install/PackInstaller.cs", "Harborline.Api.Foundation.Packs.Install.PackInstaller.ProjectAndRetire``1[!!0](!!0,Harborline.Api.Foundation.Packs.Install.PackProjectionAuthority,System.Func`3[!!0,System.Object,!!0],System.Func`3[!!0,System.Exception,!!0]): !!0", "Harborline.Api.Foundation.Packs.Install.IPackProjectionAdmissionStore.MarkProjectionCompleted(System.Guid): System.Void", 0, "admitted coordinator", "projection completion is recorded before authority retirement"),
        new("packages/foundation/Assets/Entities/IEntityStore.cs", "Harborline.Api.Foundation.Assets.Entities.IEntityMutationStore.CreateBatchAsync(System.Collections.Generic.IEnumerable`1[Harborline.Api.Foundation.Assets.Entities.EntityDraft],System.Threading.CancellationToken): System.Threading.Tasks.Task`1[System.Collections.Generic.IReadOnlyList`1[Harborline.Api.Foundation.Assets.Common.EntityId]]", "Harborline.Api.Foundation.Assets.Entities.IEntityMutationStore.CreateAsync(Harborline.Api.Foundation.Assets.Common.SchemaId,System.Text.Json.JsonDocument,Harborline.Api.Foundation.Assets.Entities.CreateOptions,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Common.EntityId]", 0, "raw port implementation", "default batch implementation delegates to the same raw port"),
        new("packages/foundation/Definitions/EntityStoreDefinitionLifecycle.cs", "Harborline.Api.Foundation.Definitions.EntityStoreDefinitionLifecycle`1[!0].TransitionAsync(!0,Harborline.Api.Foundation.Definitions.DefinitionCoordinates,Harborline.Api.Foundation.Definitions.DefinitionLifecycleStatus,System.Collections.Generic.IReadOnlyCollection`1[Harborline.Api.Foundation.Definitions.DefinitionLifecycleStatus],System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask`1[!0]", "Harborline.Api.Foundation.Assets.Entities.IEntityMutationStore.UpdateAsync(Harborline.Api.Foundation.Assets.Common.EntityId,System.Text.Json.JsonDocument,Harborline.Api.Foundation.Assets.Entities.UpdateOptions,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[Harborline.Api.Foundation.Assets.Common.VersionId]", 0, "persistence adapter", "definition lifecycle persists its admitted transition"),
    ];

    [Fact]
    public void CompiledRawMutationCallSitesEqualTheExactReviewedInventory()
    {
        var discovered = Discover(ProductionAssemblies());
        var expected = Allowed
            .Select(row => $"{row.Path}|{row.Symbol}|{row.Target}|{row.Ordinal}");

        Assert.All(Allowed, row =>
        {
            Assert.DoesNotContain('*', row.Path);
            Assert.DoesNotContain('*', row.Symbol);
            Assert.DoesNotContain('*', row.Target);
            Assert.True(row.Ordinal >= 0);
            Assert.False(string.IsNullOrWhiteSpace(row.Classification));
            Assert.False(string.IsNullOrWhiteSpace(row.Reason));
            Assert.False(string.IsNullOrWhiteSpace(row.Target));
        });

        AssertExactInventory(expected, discovered);
    }

    [Fact]
    public void CompiledFenceRejectsNewMethodAndSecondSameTargetCall()
    {
        var found = Discover([typeof(ConcretePlantedOffender).Assembly], type =>
            type == typeof(ConcretePlantedOffender) || type.DeclaringType == typeof(ConcretePlantedOffender));

        Assert.Contains(found, site => site.Target.Contains($"{typeof(InMemoryEntityStore).FullName}.CreateAsync"));
        Assert.Contains(found, site => site.Target.Contains($"{typeof(InMemoryHierarchyService).FullName}.AddEdgeAsync"));
        Assert.Contains(found, site => site.Target.Contains($"{typeof(NodeEfBankAccountRepository).FullName}.AddAsync"));
        Assert.Contains(found, site => site.Target.Contains($"{typeof(DurablePackInstallStore).FullName}.Commit"));
        Assert.Contains(found, site => site.Target.Contains($"{typeof(IGrantStore).FullName}.RevokeAsync"));
        var newMethod = Assert.Single(found, site => site.Symbol.Contains(".Entity(", StringComparison.Ordinal));
        Assert.ThrowsAny<Exception>(() => AssertExactInventory([], [newMethod]));

        var repeated = found.Where(site => site.Symbol.Contains(".TwoCallsToSameTarget(", StringComparison.Ordinal)).ToArray();
        Assert.Equal([0, 1], repeated.Select(site => site.Ordinal));
        Assert.ThrowsAny<Exception>(() => AssertExactInventory([Key(repeated[0])], repeated));
    }

    [Fact]
    public void ExactInventoryKeyExcludesPdbLine()
    {
        var original = new CallSite("packages/example/Writer.cs", "Harborline.Example.Writer.Write",
            40, "Harborline.Example.IRawPort.Mutate", 0);
        var moved = original with { Line = original.Line + 20 };

        AssertExactInventory([Key(original)], [moved]);
    }

    [Fact]
    public void ClosureKeysDropTheEnclosingMethodOrdinalAndKeepEveryReviewedDistinction()
    {
        // The enclosing-method ordinal renumbers when an unrelated member is added to the declaring
        // type — 151 s2 added one primary-constructor parameter to NodeHierarchyCompositeCoordinator
        // and every closure there moved 9 -> 10 — so keying the reviewed inventory on it makes the
        // fence stale for a reason no reviewer chose, on any host that compiles the tree.
        Assert.Equal(
            "N+<>c__DisplayClass_0.<Reparent>b__0(): System.Void",
            MethodSignatureSymbol.StableClosureNames("N+<>c__DisplayClass10_0.<Reparent>b__0(): System.Void"));
        Assert.Equal(
            MethodSignatureSymbol.StableClosureNames("N+<>c__DisplayClass9_0.<Reparent>b__0(): System.Void"),
            MethodSignatureSymbol.StableClosureNames("N+<>c__DisplayClass10_0.<Reparent>b__0(): System.Void"));

        // Scope ordinal, lambda ordinal and the owning method name all survive.
        Assert.NotEqual(
            MethodSignatureSymbol.StableClosureNames("N+<>c__DisplayClass9_0.<M>b__0(): System.Void"),
            MethodSignatureSymbol.StableClosureNames("N+<>c__DisplayClass9_1.<M>b__0(): System.Void"));
        Assert.NotEqual(
            MethodSignatureSymbol.StableClosureNames("N+<>c.<M>b__9_0(): System.Void"),
            MethodSignatureSymbol.StableClosureNames("N+<>c.<M>b__9_1(): System.Void"));
        Assert.NotEqual(
            MethodSignatureSymbol.StableClosureNames("N+<>c.<A>b__9_0(): System.Void"),
            MethodSignatureSymbol.StableClosureNames("N+<>c.<B>b__9_0(): System.Void"));
    }

    [Fact]
    public void SignatureKeysDistinguishCallerOverloadsGenericTargetsAndSiblingLambdas()
    {
        var found = DiscoverSignatureCases();
        var overloads = found.Where(site => site.Symbol.Contains(".OverloadedCaller(", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, overloads.Length);
        Assert.NotEqual(overloads[0].Symbol, overloads[1].Symbol);
        Assert.Equal(overloads[0].Target, overloads[1].Target);

        var genericTargets = found.Where(site => site.Symbol.Contains(".GenericTargets(", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, genericTargets.Length);
        Assert.NotEqual(genericTargets[0].Target, genericTargets[1].Target);

        var lambdas = found.Where(site => site.Symbol.Contains("<TwoLambdas>", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, lambdas.Length);
        Assert.Equal(2, lambdas.Select(site => site.Symbol).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(lambdas[0].Target, lambdas[1].Target);
    }

    [Fact]
    public void SignatureKeysDistinguishMethodsThatDifferOnlyByReturnType()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"ReturnTypeSignatureProbe_{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.Run);
        var typeBuilder = assembly.DefineDynamicModule("Probe").DefineType(
            "Probe.Target", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        EmitRead(typeBuilder, typeof(int), static il => il.Emit(OpCodes.Ldc_I4_0));
        EmitRead(typeBuilder, typeof(string), static il => il.Emit(OpCodes.Ldnull));

        var keys = typeBuilder.CreateType()!
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(MethodSignatureSymbol.Format)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Probe.Target.Read(): System.Int32", "Probe.Target.Read(): System.String"], keys);

        static void EmitRead(TypeBuilder type, Type returnType, Action<ILGenerator> emitValue)
        {
            var method = type.DefineMethod(
                "Read", MethodAttributes.Public | MethodAttributes.Static, returnType, Type.EmptyTypes);
            var il = method.GetILGenerator();
            emitValue(il);
            il.Emit(OpCodes.Ret);
        }
    }

    [Fact]
    public void AsyncStateMachineMapsToItsExactOwnerSignature()
    {
        var site = Assert.Single(DiscoverSignatureCases(), site =>
            site.Symbol.Contains(".AsyncOwner(", StringComparison.Ordinal));
        var owner = typeof(SignatureCases).GetMethod(
            "AsyncOwner", BindingFlags.Static | BindingFlags.NonPublic)!;

        Assert.Equal(MethodSignatureSymbol.Format(owner), site.Symbol);
        Assert.DoesNotContain("MoveNext", site.Symbol, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewedTargetOverloadMatchesTheExactInventory()
    {
        var actual = Assert.Single(DiscoverSignatureCases(), site =>
            site.Symbol.Contains(".OverloadSubstitution(", StringComparison.Ordinal));
        var reviewed = typeof(SignatureTarget).GetMethod(
            nameof(SignatureTarget.Overloaded), BindingFlags.Static | BindingFlags.NonPublic,
            binder: null, types: [typeof(int)], modifiers: null)!;

        AssertExactInventory(
            [Key(actual with { Target = MethodSignatureSymbol.Format(reviewed) })], [actual]);
    }

    private static void AssertExactInventory(IEnumerable<string> expected, IEnumerable<CallSite> discovered)
    {
        var expectedRows = expected.Order(StringComparer.Ordinal).ToArray();
        var actualRows = discovered.Select(Key).Order(StringComparer.Ordinal).ToArray();
        Assert.True(expectedRows.SequenceEqual(actualRows, StringComparer.Ordinal),
            "Raw mutation call-site inventory mismatch:\n" + string.Join("\n", discovered.Select(Describe)));
    }

    private static string Key(CallSite site) => $"{site.Path}|{site.Symbol}|{site.Target}|{site.Ordinal}";
    private static string Describe(CallSite site) => $"{Key(site)} @ {site.Path}:{site.Line}";

    private static Assembly[] ProductionAssemblies() =>
    [
        typeof(IEntityMutationStore).Assembly,
        typeof(IBankAccountMutationRepository).Assembly,
        typeof(PackInstaller).Assembly,
        typeof(Harborline.Api.Foundation.Forms.AuthorizedFormDefinitionLifecycle).Assembly,
        typeof(Harborline.Api.Blocks.Workflow.Durable.AuthorizedWorkflowDefinitionLifecycle).Assembly,
        typeof(Harborline.Api.Foundation.Forms.Engine.FormEngine).Assembly,
        typeof(NodeEntityWriter).Assembly,
    ];

    private static CallSite[] DiscoverSignatureCases() => DiscoverCalls(
        [typeof(SignatureCases).Assembly],
        target => target.DeclaringType == typeof(SignatureTarget),
        type => IsNestedWithin(type, typeof(SignatureCases)));

    private static bool IsNestedWithin(Type candidate, Type owner)
    {
        for (var type = candidate; type is not null; type = type.DeclaringType)
            if (type == owner) return true;
        return false;
    }

    private static CallSite[] Discover(IEnumerable<Assembly> assemblies, Func<Type, bool>? typeFilter = null)
    {
        var ports = new HashSet<Type>
        {
            typeof(IEntityMutationStore),
            typeof(IBankAccountMutationRepository),
            typeof(IPackInstallMutationStore),
            typeof(IPackProjectionAdmissionStore),
            typeof(IHierarchyMutationStore),
            typeof(IHierarchyCompositeUnitOfWork),
        };
        return DiscoverCalls(
            assemblies,
            target => IsRawMutationTarget(target, ports) || IsRawGrantRevocationTarget(target),
            typeFilter);
    }

    private static bool IsRawGrantRevocationTarget(MethodBase target) =>
        target.Name == nameof(IGrantStore.RevokeAsync)
        && target.DeclaringType is { } declaring
        && (declaring == typeof(IGrantStore) || typeof(IGrantStore).IsAssignableFrom(declaring));

    internal static CallSite[] DiscoverCalls(
        IEnumerable<Assembly> assemblies,
        Func<MethodBase, bool> targetPredicate,
        Func<Type, bool>? typeFilter = null)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        ArgumentNullException.ThrowIfNull(targetPredicate);
        var found = new List<CallSite>();
        var ordinals = new Dictionary<(string Caller, string Target), int>();
        foreach (var assembly in assemblies.Distinct())
        {
            foreach (var type in assembly.GetTypes().Where(type => typeFilter?.Invoke(type) ?? true))
            foreach (var caller in DeclaredMethods(type))
            {
                var callerSymbol = MethodSignatureSymbol.ForCaller(caller);
                foreach (var call in CalledMethods(caller))
                {
                    if (!targetPredicate(call.Target))
                        continue;
                    var target = MethodSignatureSymbol.Format(call.Target);
                    var ordinalKey = (callerSymbol, target);
                    ordinals.TryGetValue(ordinalKey, out var ordinal);
                    ordinals[ordinalKey] = ordinal + 1;
                    var source = SourceLocation(caller, call.Offset);
                    found.Add(new CallSite(source.Path, callerSymbol, source.Line, target, ordinal));
                }
            }
        }
        return found.OrderBy(Key, StringComparer.Ordinal).ToArray();
    }

    private static bool IsRawMutationTarget(MethodBase target, IReadOnlySet<Type> ports)
    {
        if (target is not MethodInfo method || method.DeclaringType is not { } declaring)
            return false;
        if (ports.Contains(declaring))
            return true;
        foreach (var port in ports.Where(port => port.IsAssignableFrom(declaring)))
        {
            var map = declaring.GetInterfaceMap(port);
            for (var index = 0; index < map.TargetMethods.Length; index++)
                if (map.TargetMethods[index] == method && map.InterfaceMethods[index].DeclaringType == port)
                    return true;
        }
        return false;
    }

    private static IEnumerable<MethodBase> DeclaredMethods(Type type) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Cast<MethodBase>()
            .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));

    private static (string Path, int Line) SourceLocation(MethodBase method, int ilOffset)
    {
        var pdbPath = Path.ChangeExtension(method.Module.Assembly.Location, ".pdb");
        using var stream = File.OpenRead(pdbPath);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        var reader = provider.GetMetadataReader();
        var handle = (MethodDefinitionHandle)MetadataTokens.Handle(method.MetadataToken);
        var debug = reader.GetMethodDebugInformation(handle);
        SequencePoint? selected = null;
        foreach (var point in debug.GetSequencePoints())
        {
            if (!point.IsHidden && point.Offset <= ilOffset)
                selected = point;
        }
        Assert.True(selected.HasValue, $"No portable-PDB source point for {method} at IL_{ilOffset:x4}.");
        var pointValue = selected!.Value;
        var documentHandle = pointValue.Document.IsNil ? debug.Document : pointValue.Document;
        var relative = Audit.AuditAppendSymbolInventory.NormalizeFile(
            reader.GetString(reader.GetDocument(documentHandle).Name));
        return (relative, pointValue.StartLine);
    }

    private static IEnumerable<(MethodBase Target, int Offset)> CalledMethods(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
            yield break;
        var position = 0;
        while (position < il.Length)
        {
            var instructionOffset = position;
            OpCode opCode;
            var first = il[position++];
            if (first == 0xfe)
                opCode = MultiByteOpCodes[il[position++]];
            else
                opCode = SingleByteOpCodes[first];
            if (opCode.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(il, position);
                MethodBase? target = null;
                try
                {
                    target = method.Module.ResolveMethod(token, method.DeclaringType?.GetGenericArguments(),
                        method is MethodInfo info ? info.GetGenericArguments() : null);
                }
                catch (ArgumentException) { }
                if (target is not null)
                    yield return (target, instructionOffset);
            }
            position += OperandSize(opCode.OperandType, il, position);
        }
    }

    private static int OperandSize(OperandType operandType, byte[] il, int position) => operandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField or
            OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or
            OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(il, position) * 4),
        _ => throw new InvalidOperationException($"Unsupported IL operand {operandType}.")
    };

    private static readonly OpCode[] SingleByteOpCodes = BuildOpCodes(multiByte: false);
    private static readonly OpCode[] MultiByteOpCodes = BuildOpCodes(multiByte: true);

    private static OpCode[] BuildOpCodes(bool multiByte)
    {
        var result = new OpCode[256];
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var opCode = (OpCode)field.GetValue(null)!;
            if ((opCode.Size == 2) == multiByte)
                result[opCode.Value & 0xff] = opCode;
        }
        return result;
    }

    private sealed class ConcretePlantedOffender(
        InMemoryEntityStore entities,
        InMemoryHierarchyService hierarchy,
        NodeEfBankAccountRepository bankAccounts,
        DurablePackInstallStore packs,
        IGrantStore grants)
    {
        public Task<EntityId> Entity(EntityDraft draft, CancellationToken cancellationToken) =>
            entities.CreateAsync(draft.Schema, draft.Body, draft.Options, cancellationToken);

        public Task<EntityEdge> Hierarchy(
            EntityId child,
            EntityId parent,
            DateTimeOffset at,
            CancellationToken cancellationToken) =>
            hierarchy.AddEdgeAsync(child, parent, EdgeKind.ChildOf, at, null, cancellationToken);

        public Task Bank(BankAccount account, CancellationToken cancellationToken) =>
            bankAccounts.AddAsync(account, cancellationToken);

        public void Pack(PackInstallTransaction transaction) => packs.Commit(transaction);

        public Task<AccessGrant?> Grant(
            TenantId tenant,
            GrantId grant,
            GrantRevocation revocation,
            CancellationToken cancellationToken) =>
            grants.RevokeAsync(tenant, grant, revocation, cancellationToken);

        public void TwoCallsToSameTarget(EntityId entity)
        {
            _ = entities.DeleteAsync(entity, new DeleteOptions(new ActorId("actor")), default);
            _ = entities.DeleteAsync(entity, new DeleteOptions(new ActorId("actor")), default);
        }
    }

    private static class SignatureCases
    {
        private static void OverloadedCaller(int value) => SignatureTarget.SameTarget();
        private static void OverloadedCaller(string value) => SignatureTarget.SameTarget();

        private static void GenericTargets()
        {
            SignatureTarget.Generic<int>();
            SignatureTarget.Generic<string>();
        }

        private static void TwoLambdas()
        {
            Action first = () => SignatureTarget.SameTarget();
            Action second = () => SignatureTarget.SameTarget();
            first();
            second();
        }

        private static async Task AsyncOwner()
        {
            await Task.Yield();
            SignatureTarget.SameTarget();
        }

        private static void OverloadSubstitution() => SignatureTarget.Overloaded(1);
    }

    private static class SignatureTarget
    {
        internal static void SameTarget() { }
        internal static void Generic<T>() { }
        internal static void Overloaded(int value) { }
        internal static void Overloaded(string value) { }
    }
}
