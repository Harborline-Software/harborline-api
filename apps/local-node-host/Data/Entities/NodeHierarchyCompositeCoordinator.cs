using System.Text.Json;
using Harborline.Api.Foundation.Assets.Audit;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Assets.Hierarchy;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.Kernel.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Data.Entities;

/// <summary>Decision-bearing audit seam owned by the hierarchy composite.</summary>
public interface IHierarchyAuthorizedAuditWriter
{
    Task<AuditId> AppendAsync(
        AuditAppend append,
        AuthorizationDecision decision,
        CancellationToken ct = default);
}

/// <summary>Validates the carried decision immediately before appending the hierarchy audit row.</summary>
public sealed class HierarchyAuthorizedAuditWriter(IAuditLog audit) : IHierarchyAuthorizedAuditWriter
{
    private static readonly AuthorizationOperation RecordsWrite =
        AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite);

    public Task<AuditId> AppendAsync(
        AuditAppend append,
        AuthorizationDecision decision,
        CancellationToken ct = default)
    {
        decision.RequireAllowedReaction(
            RecordsWrite, append.Tenant, "record", append.EntityId.LocalPart);
        if (decision.Request.Principal != append.Actor)
            throw new AuthorizationDeniedException(decision);
        return audit.AppendAsync(append, ct);
    }
}

/// <summary>Complete-target admission followed by one atomic hierarchy transaction.</summary>
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

    public async Task<SplitResult> SplitAsync(
        EntityId oldEntity,
        IReadOnlyList<SplitTarget> newEntities,
        IReadOnlyDictionary<EntityId, EntityId> childReassignments,
        string justification,
        ActorId actor,
        TenantId tenant,
        DateTimeOffset effectiveAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(newEntities);
        ArgumentNullException.ThrowIfNull(childReassignments);
        var at = timeProvider.GetUtcNow();
        return (await WritePipeline.RunAsync(
            new Split(this, oldEntity, newEntities, childReassignments, justification, actor, tenant, at),
            pipelineObserver, ct).ConfigureAwait(false))!;
    }

    /// <summary>
    /// ck-10 S3 (DES-0029, ADR 0038): a split as its six stages. Every decision and every replacement's record
    /// admission happens before the atomic unit opens, so a refused split takes no unit and writes nothing;
    /// commit opens the unit and mints, reassigns, supersedes, deletes and audits in it.
    /// </summary>
    private sealed class Split(
        NodeHierarchyCompositeCoordinator coordinator,
        EntityId oldEntity,
        IReadOnlyList<SplitTarget> newEntities,
        IReadOnlyDictionary<EntityId, EntityId> childReassignments,
        string justification,
        ActorId actor,
        TenantId tenant,
        DateTimeOffset at)
        : KernelWrite<IReadOnlyList<EntityEdge>, IReadOnlyList<SplitTarget>, IReadOnlyList<(ValidatedRecordBody Body, CreateOptions Options)>, SplitResult>
    {
        private readonly EntityId[] replacementIds = newEntities
            .Select(target => InMemoryEntityStore.DeriveEntityId(target.Schema, target.Options))
            .ToArray();
        private CompositeAuthorization authorization = null!;
        private IReadOnlyList<EntityEdge> displaced = [];
        private SplitResult result = null!;

        protected override async ValueTask AuthorizeAsync(CancellationToken ct) =>
            authorization = await coordinator.DecideAllAsync(
                [oldEntity, .. replacementIds, .. childReassignments.Keys, .. childReassignments.Values],
                actor, tenant, at, ct).ConfigureAwait(false);

        protected override async ValueTask<IReadOnlyList<EntityEdge>?> BindAsync(CancellationToken ct) =>
            displaced = await coordinator.ReadAffectedChildrenAsync(
                [oldEntity], edge => childReassignments.ContainsKey(edge.From), at, ct).ConfigureAwait(false);

        protected override ValueTask<IReadOnlyList<SplitTarget>> MutateAsync(IReadOnlyList<EntityEdge> bound, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<SplitTarget>>(
                [.. newEntities.Select(target => target with { Options = target.Options with { ValidFrom = at } })]);

        /// <summary>Ticket 151 (L1418) / ticket 366: every replacement is a record, admitted against its schema
        /// from the decision that admitted it, and all are admitted before any is written.</summary>
        protected override async ValueTask<IReadOnlyList<(ValidatedRecordBody Body, CreateOptions Options)>> ValidateAsync(
            IReadOnlyList<EntityEdge> bound, IReadOnlyList<SplitTarget> mutation, CancellationToken ct)
        {
            var admitted = new List<(ValidatedRecordBody, CreateOptions)>(mutation.Count);
            for (var index = 0; index < mutation.Count; index++)
            {
                var target = mutation[index];
                if (target.Options.Tenant != tenant)
                    throw new ArgumentException("A split target tenant does not match the admitted composite.", nameof(newEntities));
                admitted.Add((await ValidatedRecordBody.AdmitAsync(
                    coordinator.Validator, authorization.Require(replacementIds[index]), target.Schema, target.Body,
                    tenant, target.Options.Binding, ct).ConfigureAwait(false), target.Options));
            }
            return admitted;
        }

        protected override async ValueTask CommitAsync(
            IReadOnlyList<(ValidatedRecordBody Body, CreateOptions Options)> validated, CancellationToken ct)
        {
            var store = coordinator.Store;
            result = await store.ExecuteAtomicAsync(async transactionCt =>
            {
                authorization.Require(oldEntity);
                var minted = new List<EntityId>(validated.Count);
                foreach (var (body, options) in validated)
                    minted.Add(await coordinator.Entities.CreateAsync(body, options, transactionCt).ConfigureAwait(false));

                var reassigned = new List<EntityId>();
                foreach (var edge in displaced)
                {
                    var newParent = childReassignments[edge.From];
                    authorization.Require(edge.From);
                    authorization.Require(oldEntity);
                    authorization.Require(newParent);
                    await store.InvalidateEdgeAsync(edge.Id, at, transactionCt).ConfigureAwait(false);
                    var replacementEdge = await store.AddEdgeAsync(
                        edge.From, newParent, EdgeKind.ChildOf, at, null, transactionCt).ConfigureAwait(false);
                    if (edge.Validity.ValidTo is { } validTo)
                        await store.InvalidateEdgeAsync(replacementEdge.Id, validTo, transactionCt).ConfigureAwait(false);
                    reassigned.Add(edge.From);
                }
                foreach (var newId in minted)
                {
                    authorization.Require(oldEntity);
                    authorization.Require(newId);
                    await store.AddEdgeAsync(
                        oldEntity, newId, EdgeKind.SupersededBy, at, null, transactionCt).ConfigureAwait(false);
                }
                authorization.Require(oldEntity);
                await coordinator.Entities.DeleteAsync(
                    oldEntity, new DeleteOptions(actor, at, justification), transactionCt).ConfigureAwait(false);

                using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    op = "split",
                    old = oldEntity.ToString(),
                    newIds = minted.Select(id => id.ToString()).ToArray(),
                    reassigned = reassigned.Select(id => id.ToString()).ToArray(),
                }));
                await coordinator.AuditWriter.AppendAsync(new AuditAppend(
                    oldEntity, null, Op.Split, actor, tenant, at, payload, justification),
                    authorization.Require(oldEntity), transactionCt)
                    .ConfigureAwait(false);
                return new SplitResult(oldEntity, minted, reassigned);
            }, ct).ConfigureAwait(false);
        }

        protected override ValueTask<SplitResult> ReactAsync(
            IReadOnlyList<(ValidatedRecordBody Body, CreateOptions Options)> validated, CancellationToken ct) =>
            ValueTask.FromResult(result);
    }

    public async Task<MergeResult> MergeAsync(
        IReadOnlyList<EntityId> oldEntities,
        SchemaId newSchema,
        JsonDocument newBody,
        CreateOptions newOptions,
        string justification,
        ActorId actor,
        TenantId tenant,
        DateTimeOffset effectiveAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(oldEntities);
        ArgumentNullException.ThrowIfNull(newBody);
        var at = timeProvider.GetUtcNow();
        var newId = InMemoryEntityStore.DeriveEntityId(newSchema, newOptions);
        return await unitOfWork.ExecuteAtomicAsync(async transactionCt =>
        {
            // Read every edge not ended by the act instant, including future-start edges committed
            // while this merge waited for the atomic scope. `at` remains the single act instant for
            // decisions and write stamps while its end boundary prevents already-ended history from
            // entering the merge target set (ticket 216, review round 7).
            var affectedEdges = await ReadChildrenNotEndedAsync(
                oldEntities, at, transactionCt).ConfigureAwait(false);
            var authorization = await DecideAllAsync(
                [newId, .. oldEntities, .. affectedEdges.Select(edge => edge.From)],
                actor, tenant, at, transactionCt).ConfigureAwait(false);
            return await ApplyMergeAsync(
                authorization, oldEntities, newId, newSchema, newBody, newOptions, affectedEdges,
                justification, actor, tenant, at, transactionCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task ReparentAsync(
        EntityId child,
        EntityId oldParent,
        EntityId newParent,
        string justification,
        ActorId actor,
        TenantId tenant,
        DateTimeOffset effectiveAt,
        CancellationToken ct = default)
    {
        var at = timeProvider.GetUtcNow();
        await WritePipeline.RunAsync(
            new Reparent(this, child, oldParent, newParent, justification, actor, tenant, at),
            pipelineObserver, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// ck-10 S3 (DES-0029, ADR 0038): a reparent as its six stages. Every decision is made before the atomic unit
    /// opens, so a refused caller takes no unit; commit opens it and writes the edges and the audit row together.
    /// Validate refuses a new parent that is the child or one of its descendants, which would close a cycle in
    /// the closure table.
    /// </summary>
    private sealed class Reparent(
        NodeHierarchyCompositeCoordinator coordinator,
        EntityId child,
        EntityId oldParent,
        EntityId newParent,
        string justification,
        ActorId actor,
        TenantId tenant,
        DateTimeOffset at)
        : KernelWrite<IReadOnlyList<EntityEdge>, DateTimeOffset?, DateTimeOffset?, bool>
    {
        private CompositeAuthorization authorization = null!;
        private IReadOnlyList<EntityEdge> displaced = [];

        protected override async ValueTask AuthorizeAsync(CancellationToken ct) =>
            authorization = await coordinator.DecideAllAsync(
                [child, oldParent, newParent], actor, tenant, at, ct).ConfigureAwait(false);

        protected override async ValueTask<IReadOnlyList<EntityEdge>?> BindAsync(CancellationToken ct) =>
            displaced = await coordinator.ReadAffectedChildrenAsync(
                [oldParent], edge => edge.From == child, at, ct).ConfigureAwait(false);

        /// <summary>Several displaced edges: the replacement outlives the longest (review round 9).</summary>
        protected override ValueTask<DateTimeOffset?> MutateAsync(IReadOnlyList<EntityEdge> bound, CancellationToken ct) =>
            ValueTask.FromResult(bound.Count == 0 || bound.Any(edge => edge.Validity.ValidTo is null)
                ? null
                : bound.Max(edge => edge.Validity.ValidTo));

        protected override async ValueTask<DateTimeOffset?> ValidateAsync(
            IReadOnlyList<EntityEdge> bound, DateTimeOffset? mutation, CancellationToken ct)
        {
            if (newParent == child)
                throw new ArgumentException("An entity cannot be its own parent.", nameof(newParent));
            await foreach (var ancestor in coordinator.Store.GetAncestorsAsync(newParent, at, ct).ConfigureAwait(false))
            {
                if (ancestor.Ancestor == child)
                    throw new ArgumentException("An entity cannot be placed under its own descendant.", nameof(newParent));
            }
            return mutation;
        }

        protected override async ValueTask CommitAsync(DateTimeOffset? validated, CancellationToken ct)
        {
            var store = coordinator.Store;
            await store.ExecuteAtomicAsync(async transactionCt =>
            {
                authorization.Require(child);
                authorization.Require(oldParent);
                authorization.Require(newParent);
                foreach (var edge in displaced)
                    await store.InvalidateEdgeAsync(edge.Id, at, transactionCt).ConfigureAwait(false);
                var replacementEdge = await store.AddEdgeAsync(
                    child, newParent, EdgeKind.ChildOf, at, null, transactionCt).ConfigureAwait(false);
                if (validated is { } validTo)
                    await store.InvalidateEdgeAsync(replacementEdge.Id, validTo, transactionCt).ConfigureAwait(false);
                using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    op = "reparent",
                    child = child.ToString(),
                    oldParent = oldParent.ToString(),
                    newParent = newParent.ToString(),
                }));
                await coordinator.AuditWriter.AppendAsync(new AuditAppend(
                    child, null, Op.Reparent, actor, tenant, at, payload, justification),
                    authorization.Require(child), transactionCt)
                    .ConfigureAwait(false);
                return true;
            }, ct).ConfigureAwait(false);
        }

        protected override ValueTask<bool> ReactAsync(DateTimeOffset? validated, CancellationToken ct) =>
            ValueTask.FromResult(true);
    }

    private async Task<CompositeAuthorization> DecideAllAsync(
        IEnumerable<EntityId> targets,
        ActorId actor,
        TenantId tenant,
        DateTimeOffset at,
        CancellationToken ct)
    {
        var decisions = new Dictionary<string, AuthorizationDecision>(StringComparer.Ordinal);
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

    private async Task<IReadOnlyList<EntityEdge>> ReadAffectedChildrenAsync(
        IEnumerable<EntityId> parents,
        Func<EntityEdge, bool> include,
        DateTimeOffset effectiveAt,
        CancellationToken ct)
    {
        var edges = new List<EntityEdge>();
        foreach (var parent in parents.Distinct())
        await foreach (var edge in unitOfWork.GetChildrenAsync(parent, effectiveAt, ct).ConfigureAwait(false))
            if (include(edge))
                edges.Add(edge);
        return edges;
    }

    private async Task<MergeResult> ApplyMergeAsync(
        CompositeAuthorization authorization,
        IReadOnlyList<EntityId> oldEntities,
        EntityId expectedNewId,
        SchemaId newSchema,
        JsonDocument newBody,
        CreateOptions newOptions,
        IReadOnlyList<EntityEdge> affectedEdges,
        string justification,
        ActorId actor,
        TenantId tenant,
        DateTimeOffset effectiveAt,
        CancellationToken ct)
    {
        var admission = authorization.Require(expectedNewId);
        if (newOptions.Tenant != tenant)
            throw new ArgumentException("The merge target tenant does not match the admitted composite.", nameof(newOptions));
        // Ticket 366: the merge target is a RECORD, minted from the same decision the admission returned.
        var admitted = await ValidatedRecordBody.AdmitAsync(
            validator, admission, newSchema, newBody, tenant, newOptions.Binding, ct).ConfigureAwait(false);
        var newId = await entities.CreateAsync(
            admitted, newOptions with { ValidFrom = effectiveAt }, ct).ConfigureAwait(false);
        if (newId != expectedNewId)
            throw new InvalidOperationException("The entity store minted an id different from the pre-authorized merge target.");
        var reassigned = new List<EntityId>();
        foreach (var oldId in oldEntities)
        {
            authorization.Require(oldId);
            foreach (var edge in affectedEdges.Where(edge => edge.To == oldId))
            {
                authorization.Require(edge.From);
                authorization.Require(newId);
                await unitOfWork.InvalidateEdgeAsync(edge.Id, effectiveAt, ct).ConfigureAwait(false);
                var replacementEdge = await unitOfWork.AddEdgeAsync(
                    edge.From, newId, EdgeKind.ChildOf, effectiveAt, null, ct).ConfigureAwait(false);
                if (edge.Validity.ValidTo is { } validTo)
                    await unitOfWork.InvalidateEdgeAsync(replacementEdge.Id, validTo, ct).ConfigureAwait(false);
                reassigned.Add(edge.From);
            }
            authorization.Require(oldId);
            authorization.Require(newId);
            await unitOfWork.AddEdgeAsync(
                oldId, newId, EdgeKind.SupersededBy, effectiveAt, null, ct).ConfigureAwait(false);
            await entities.DeleteAsync(
                oldId, new DeleteOptions(actor, effectiveAt, justification), ct).ConfigureAwait(false);
        }
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            op = "merge",
            newId = newId.ToString(),
            oldIds = oldEntities.Select(id => id.ToString()).ToArray(),
            reassigned = reassigned.Select(id => id.ToString()).ToArray(),
        }));
        await audit.AppendAsync(new AuditAppend(
            newId, null, Op.Merge, actor, tenant, effectiveAt, payload, justification),
            authorization.Require(newId), ct)
            .ConfigureAwait(false);
        return new MergeResult(newId, oldEntities, reassigned);
    }

    private async Task<IReadOnlyList<EntityEdge>> ReadChildrenNotEndedAsync(
        IEnumerable<EntityId> parents,
        DateTimeOffset asOf,
        CancellationToken ct)
    {
        var edges = new List<EntityEdge>();
        foreach (var parent in parents.Distinct())
        await foreach (var edge in unitOfWork.GetChildrenNotEndedAsync(parent, asOf, ct).ConfigureAwait(false))
            edges.Add(edge);
        return edges;
    }

    private sealed class CompositeAuthorization(IReadOnlyDictionary<string, AuthorizationDecision> decisions)
    {
        internal AuthorizationDecision Require(EntityId target)
        {
            if (!decisions.TryGetValue(target.LocalPart, out var decision))
                throw new InvalidOperationException($"The hierarchy target '{target}' was not authorized before the unit of work.");
            return decision.RequireAllowedReaction(
                RecordsWrite, decision.Request.Tenant, "record", target.LocalPart);
        }

    }
}
