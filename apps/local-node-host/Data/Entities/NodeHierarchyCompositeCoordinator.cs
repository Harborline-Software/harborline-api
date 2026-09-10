using System.Text.Json;
using Harborline.Api.Foundation.Assets.Audit;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Assets.Hierarchy;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

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
    IEntityValidator? validator = null,
    Health.AuthorizationRefusalAudit? refusalAudit = null) : IHierarchyCompositeCoordinator
{
    private static readonly AuthorizationOperation RecordsWrite =
        AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite);

    // Stage two for the composite records path (ticket 151): every minted target is validated after its
    // admission and before persistence. Null keeps the embedders that rely on the store's own pre-commit
    // hook (the hook still runs underneath); the node passes the registered real validator.
    private readonly IEntityValidator _validator = validator ?? NullEntityValidator.Instance;

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
        var replacementIds = newEntities
            .Select(target => InMemoryEntityStore.DeriveEntityId(target.Schema, target.Options))
            .ToArray();
        var authorization = await DecideAllAsync(
            [oldEntity, .. replacementIds, .. childReassignments.Keys, .. childReassignments.Values],
            actor, tenant, at, ct).ConfigureAwait(false);
        var affectedEdges = await ReadAffectedChildrenAsync(
            [oldEntity], edge => childReassignments.ContainsKey(edge.From), at, ct).ConfigureAwait(false);
        return await unitOfWork.ExecuteAtomicAsync(
            transactionCt => ApplySplitAsync(
                authorization, oldEntity, newEntities, replacementIds, childReassignments, affectedEdges,
                justification, actor, tenant, at, transactionCt),
            ct).ConfigureAwait(false);
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
        var authorization = await DecideAllAsync(
            [child, oldParent, newParent], actor, tenant, at, ct).ConfigureAwait(false);
        var affectedEdges = await ReadAffectedChildrenAsync(
            [oldParent], edge => edge.From == child, at, ct).ConfigureAwait(false);
        await unitOfWork.ExecuteAtomicAsync(async transactionCt =>
        {
            authorization.Require(child);
            authorization.Require(oldParent);
            authorization.Require(newParent);
            var originalValidTo = affectedEdges.Count == 0 || affectedEdges.Any(edge => edge.Validity.ValidTo is null) ? null : affectedEdges.Max(edge => edge.Validity.ValidTo); // several displaced edges: the replacement outlives the longest (review round 9)
            foreach (var edge in affectedEdges)
                await unitOfWork.InvalidateEdgeAsync(edge.Id, at, transactionCt).ConfigureAwait(false);
            var replacementEdge = await unitOfWork.AddEdgeAsync(
                child, newParent, EdgeKind.ChildOf, at, null, transactionCt).ConfigureAwait(false);
            if (originalValidTo is { } validTo)
                await unitOfWork.InvalidateEdgeAsync(replacementEdge.Id, validTo, transactionCt).ConfigureAwait(false);
            using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                op = "reparent",
                child = child.ToString(),
                oldParent = oldParent.ToString(),
                newParent = newParent.ToString(),
            }));
            await audit.AppendAsync(new AuditAppend(
                child, null, Op.Reparent, actor, tenant, at, payload, justification),
                authorization.Require(child), transactionCt)
                .ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
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

    private async Task<SplitResult> ApplySplitAsync(
        CompositeAuthorization authorization,
        EntityId oldEntity,
        IReadOnlyList<SplitTarget> newEntities,
        IReadOnlyList<EntityId> replacementIds,
        IReadOnlyDictionary<EntityId, EntityId> childReassignments,
        IReadOnlyList<EntityEdge> affectedEdges,
        string justification,
        ActorId actor,
        TenantId tenant,
        DateTimeOffset effectiveAt,
        CancellationToken ct)
    {
        authorization.Require(oldEntity);
        var minted = new List<EntityId>(newEntities.Count);
        for (var index = 0; index < newEntities.Count; index++)
        {
            var target = newEntities[index];
            if (target.Options.Tenant != tenant)
                throw new ArgumentException("A split target tenant does not match the admitted composite.", nameof(newEntities));
            authorization.Require(replacementIds[index]);
            await RecordWriteValidation.ValidateAsync(
                _validator, refusalAudit, target.Schema, target.Body, actor, tenant, effectiveAt, ct)
                .ConfigureAwait(false);
            minted.Add(await entities.CreateAsync(
                target.Schema, target.Body, target.Options with { ValidFrom = effectiveAt }, ct).ConfigureAwait(false));
        }

        var reassigned = new List<EntityId>();
        foreach (var edge in affectedEdges)
        {
            var newParent = childReassignments[edge.From];
            authorization.Require(edge.From);
            authorization.Require(oldEntity);
            authorization.Require(newParent);
            await unitOfWork.InvalidateEdgeAsync(edge.Id, effectiveAt, ct).ConfigureAwait(false);
            var replacementEdge = await unitOfWork.AddEdgeAsync(
                edge.From, newParent, EdgeKind.ChildOf, effectiveAt, null, ct).ConfigureAwait(false);
            if (edge.Validity.ValidTo is { } validTo)
                await unitOfWork.InvalidateEdgeAsync(replacementEdge.Id, validTo, ct).ConfigureAwait(false);
            reassigned.Add(edge.From);
        }
        foreach (var newId in minted)
        {
            authorization.Require(oldEntity);
            authorization.Require(newId);
            await unitOfWork.AddEdgeAsync(
                oldEntity, newId, EdgeKind.SupersededBy, effectiveAt, null, ct).ConfigureAwait(false);
        }
        authorization.Require(oldEntity);
        await entities.DeleteAsync(
            oldEntity, new DeleteOptions(actor, effectiveAt, justification), ct).ConfigureAwait(false);

        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            op = "split",
            old = oldEntity.ToString(),
            newIds = minted.Select(id => id.ToString()).ToArray(),
            reassigned = reassigned.Select(id => id.ToString()).ToArray(),
        }));
        await audit.AppendAsync(new AuditAppend(
            oldEntity, null, Op.Split, actor, tenant, effectiveAt, payload, justification),
            authorization.Require(oldEntity), ct)
            .ConfigureAwait(false);
        return new SplitResult(oldEntity, minted, reassigned);
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
        authorization.Require(expectedNewId);
        if (newOptions.Tenant != tenant)
            throw new ArgumentException("The merge target tenant does not match the admitted composite.", nameof(newOptions));
        await RecordWriteValidation.ValidateAsync(
            _validator, refusalAudit, newSchema, newBody, actor, tenant, effectiveAt, ct).ConfigureAwait(false);
        var newId = await entities.CreateAsync(
            newSchema, newBody, newOptions with { ValidFrom = effectiveAt }, ct).ConfigureAwait(false);
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
