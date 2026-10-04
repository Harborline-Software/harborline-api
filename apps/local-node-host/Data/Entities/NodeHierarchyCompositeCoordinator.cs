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
        var at = AdmittedInstant.Read(timeProvider);
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
        AdmittedInstant instant)
        : KernelWrite<IReadOnlyList<EntityEdge>, IReadOnlyList<SplitTarget>, IReadOnlyList<(ValidatedRecordBody Body, CreateOptions Options)>, SplitResult>
    {
        private DateTimeOffset at => instant.Value;
        private readonly EntityId[] replacementIds = newEntities
            .Select(target => InMemoryEntityStore.DeriveEntityId(target.Schema, target.Options))
            .ToArray();
        private CompositeAuthorization authorization = null!;
        private IReadOnlyList<EntityEdge> displaced = [];
        private SplitResult result = null!;

        protected override async ValueTask AuthorizeAsync(CancellationToken ct) =>
            authorization = await coordinator.DecideAllAsync(
                [oldEntity, .. replacementIds, .. childReassignments.Keys, .. childReassignments.Values],
                actor, tenant, instant, ct).ConfigureAwait(false);

        protected override async ValueTask<IReadOnlyList<EntityEdge>?> BindAsync(CancellationToken ct) =>
            displaced = await coordinator.ReadChildrenNotEndedAsync(
                [oldEntity], at, ct, edge => childReassignments.ContainsKey(edge.From)).ConfigureAwait(false);

        protected override ValueTask<IReadOnlyList<SplitTarget>> MutateAsync(IReadOnlyList<EntityEdge> bound, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<SplitTarget>>(
                [.. newEntities.Select(target => target with { Options = target.Options with { ValidFrom = at } })]);

        /// <summary>Ticket 151 (L1418) / ticket 366: every replacement is a record, admitted against its schema
        /// from the decision that admitted it, and all are admitted before any is written.</summary>
        protected override async ValueTask<IReadOnlyList<(ValidatedRecordBody Body, CreateOptions Options)>> ValidateAsync(
            IReadOnlyList<EntityEdge> bound, IReadOnlyList<SplitTarget> mutation, CancellationToken ct)
        {
            ValidateTargetTenants(mutation, tenant);
            await RefuseInvalidReassignmentsAsync(newEntities, childReassignments, ct).ConfigureAwait(false);
            var admitted = new List<(ValidatedRecordBody, CreateOptions)>(mutation.Count);
            for (var index = 0; index < mutation.Count; index++)
            {
                var target = mutation[index];
                admitted.Add((await ValidatedRecordBody.AdmitAsync(
                    coordinator.Validator, authorization.Require(replacementIds[index]), target.Schema, target.Body,
                    tenant, target.Options.Binding, ct).ConfigureAwait(false), target.Options));
            }
            return admitted;
        }

        private static void ValidateTargetTenants(IReadOnlyList<SplitTarget> newEntities, TenantId tenant)
        {
            if (newEntities.Any(target => target.Options.Tenant != tenant))
                throw new ArgumentException("A split target tenant does not match the admitted composite.", nameof(newEntities));
        }

        private async ValueTask RefuseInvalidReassignmentsAsync(
            IReadOnlyList<SplitTarget> newEntities,
            IReadOnlyDictionary<EntityId, EntityId> childReassignments, CancellationToken ct)
        {
            if (newEntities.Any(target => InMemoryEntityStore.DeriveEntityId(target.Schema, target.Options) == oldEntity))
                throw new ArgumentException("A split replacement cannot be the entity being deleted.", nameof(newEntities));
            if (childReassignments.Values.Contains(oldEntity))
                throw new ArgumentException("A split child cannot retain the entity being deleted as parent.", nameof(childReassignments));
            var proposed = displaced.Select(edge => new ProposedChildEdge(
                edge.From, childReassignments[edge.From],
                edge.Validity.ValidFrom > at ? edge.Validity.ValidFrom : at, edge.Validity.ValidTo)).ToArray();
            if (await coordinator.HasTemporalCycleAsync(proposed, displaced, ct).ConfigureAwait(false))
                throw new ArgumentException("A split cannot place an entity under itself or its descendant.", nameof(childReassignments));
        }

        protected override async ValueTask CommitAsync(
            IReadOnlyList<(ValidatedRecordBody Body, CreateOptions Options)> validated, CancellationToken ct)
        {
            var store = coordinator.Store;
            result = await store.ExecuteAtomicAsync(async transactionCt =>
            {
                authorization.Require(oldEntity);
                var current = await coordinator.ReadChildrenNotEndedAsync(
                    [oldEntity], at, transactionCt, edge => childReassignments.ContainsKey(edge.From)).ConfigureAwait(false);
                if (!SameEdgeState(current, displaced))
                    throw new InvalidOperationException("The displaced edges changed between bind and commit.");
                await RefuseInvalidReassignmentsAsync(newEntities, childReassignments, transactionCt).ConfigureAwait(false);
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
                    // Requested scheduled children are part of the split even before their edge starts.
                    // Close the old edge at its start and retain that start and its original finite end.
                    var start = edge.Validity.ValidFrom > at ? edge.Validity.ValidFrom : at;
                    await store.InvalidateEdgeAsync(edge.Id, start, transactionCt).ConfigureAwait(false);
                    var replacementEdge = await store.AddEdgeAsync(
                        edge.From, newParent, EdgeKind.ChildOf, start, null, transactionCt).ConfigureAwait(false);
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
        var at = AdmittedInstant.Read(timeProvider);
        // Ticket 216 (review round 7): the merge reads every edge not ended by the act instant inside the unit,
        // including future-start edges committed while it waited, so the whole pipeline runs inside the unit
        // and the displaced set it decides is the set it writes.
        return (await unitOfWork.ExecuteAtomicAsync(
            async transactionCt => (await WritePipeline.RunAsync(
                new Merge(this, oldEntities, newSchema, newBody, newOptions, justification, actor, tenant, at),
                pipelineObserver, transactionCt).ConfigureAwait(false))!,
            ct).ConfigureAwait(false));
    }

    /// <summary>
    /// ck-10 S3 (DES-0029, ADR 0038): a merge as its six stages inside its atomic unit. Authorize decides the
    /// records the caller named; validate decides the displaced children bind found and admits the replacement
    /// record, so nothing is written until every decision and the admission have passed.
    /// </summary>
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
        var at = AdmittedInstant.Read(timeProvider);
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
        AdmittedInstant instant)
        : KernelWrite<IReadOnlyList<EntityEdge>, DateTimeOffset?, DateTimeOffset?, bool>
    {
        private DateTimeOffset at => instant.Value;
        private CompositeAuthorization authorization = null!;
        private IReadOnlyList<EntityEdge> displaced = [];

        protected override async ValueTask AuthorizeAsync(CancellationToken ct) =>
            authorization = await coordinator.DecideAllAsync(
                [child, oldParent, newParent], actor, tenant, instant, ct).ConfigureAwait(false);

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
            await RefuseCycleAsync(newParent, mutation, ct).ConfigureAwait(false);
            return mutation;
        }

        /// <summary>
        /// Refuses a new parent that is the child or one of its descendants. Validate asks first; commit asks again
        /// inside the atomic unit, where the answer is authoritative, because an opposing reparent can commit
        /// between the two (each moving one entity under the other) and both would otherwise pass validate.
        /// </summary>
        private async ValueTask RefuseCycleAsync(EntityId newParent, DateTimeOffset? validTo, CancellationToken ct)
        {
            if (await coordinator.HasTemporalCycleAsync(
                [new ProposedChildEdge(child, newParent, at, validTo)], displaced, ct).ConfigureAwait(false))
                throw new ArgumentException("An entity cannot be placed under itself or its descendant.", nameof(newParent));
        }

        protected override async ValueTask CommitAsync(DateTimeOffset? validated, CancellationToken ct)
        {
            var store = coordinator.Store;
            await store.ExecuteAtomicAsync(async transactionCt =>
            {
                authorization.Require(child);
                authorization.Require(oldParent);
                authorization.Require(newParent);
                await RefuseCycleAsync(newParent, validated, transactionCt).ConfigureAwait(false);
                var current = await coordinator.ReadAffectedChildrenAsync(
                    [oldParent], edge => edge.From == child, at, transactionCt).ConfigureAwait(false);
                if (!SameEdgeState(current, displaced))
                    throw new InvalidOperationException("The displaced edges changed between bind and commit.");
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

    private sealed record ProposedChildEdge(EntityId Child, EntityId Parent, DateTimeOffset From, DateTimeOffset? To);

    // Validate the prospective graph as a whole: displaced edges close at the replacement start,
    // and every replacement participates in traversal, including cycles introduced jointly by a split.
    private async ValueTask<bool> HasTemporalCycleAsync(
        IReadOnlyList<ProposedChildEdge> proposed, IReadOnlyList<EntityEdge> displaced, CancellationToken ct)
    {
        var removed = displaced.Select(edge => edge.Id).ToHashSet();
        foreach (var candidate in proposed)
        {
            var pending = new Stack<(EntityId Parent, DateTimeOffset From, DateTimeOffset? To)>();
            var visited = new HashSet<(EntityId Parent, DateTimeOffset From, DateTimeOffset? To)>();
            pending.Push((candidate.Child, candidate.From, candidate.To));
            while (pending.TryPop(out var interval))
            {
                if (interval.To is { } end && interval.From >= end) continue;
                if (!visited.Add(interval)) continue;
                if (interval.Parent == candidate.Parent) return true;
                var children = new List<ProposedChildEdge>();
                await foreach (var edge in Store.GetChildrenNotEndedAsync(interval.Parent, interval.From, ct).ConfigureAwait(false))
                    if (!removed.Contains(edge.Id))
                        children.Add(new(edge.From, edge.To, edge.Validity.ValidFrom, edge.Validity.ValidTo));
                children.AddRange(proposed.Where(edge => edge.Parent == interval.Parent));
                foreach (var edge in children)
                {
                    var from = edge.From > interval.From ? edge.From : interval.From;
                    var to = interval.To;
                    if (edge.To is { } edgeEnd && (to is null || edgeEnd < to.Value)) to = edgeEnd;
                    pending.Push((edge.Child, from, to));
                }
            }
        }
        return false;
    }

    // Invalidation retains an edge id but changes its interval. A later-admitted competing move can
    // therefore remain visible at this act's earlier instant: id equality alone is not a concurrency check.
    private static bool SameEdgeState(IReadOnlyList<EntityEdge> current, IReadOnlyList<EntityEdge> bound) =>
        current.Select(edge => (edge.Id, edge.From, edge.To, edge.Kind, edge.Validity.ValidFrom, edge.Validity.ValidTo))
            .ToHashSet().SetEquals(bound.Select(edge =>
                (edge.Id, edge.From, edge.To, edge.Kind, edge.Validity.ValidFrom, edge.Validity.ValidTo)));

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
