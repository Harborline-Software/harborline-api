using System.Text.Json;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Data.Entities;

/// <summary>
/// Ticket 331 slice 2 -- a persisted legal entity and the audit id of the decision that permitted the
/// write, so the route can answer "why was this allowed?" with an addressable id. The id is null when no
/// audit sink is composed or its append faulted; the write itself stands either way.
/// </summary>
public sealed record LegalEntityWritten(LegalEntity Entity, Guid? AuditId);

public sealed record CreateLegalEntityCommand(
    LegalEntityId Id,
    string? LegalName,
    string? Kind,
    string? TaxClassification,
    string? CommonControlGroupId);

/// <summary>The node's admitted coordinator for legal-entity and generic entity writes.</summary>
public sealed class NodeEntityWriter(
    IDbContextFactory<LocalNodeDbContext> factory,
    IEntityMutationStore entities,
    [FromKeyedServices(CompiledSchemaEntityValidator.RecordWriteKey)] IEntityValidator validator,
    AuthorizationGate gate,
    Health.AuthorizationRefusalAudit? refusals = null,
    Health.AuthorizedActAudit? accepted = null) : IEntityWriteCoordinator
{
    /// <summary>The event type an accepted record write is recorded under (ticket 331 slice 2).</summary>
    public static readonly Kernel.Audit.AuditEventType RecordWrittenEventType = new("RecordWritten");

    /// <summary>
    /// Records the accepted write against the ONE decision that permitted it and returns that entry's
    /// audit id. Every accepted write on this coordinator goes through here, so no write path is
    /// addressable while a sibling is silent. It carries the record's identity and schema and never a
    /// member of its body.
    /// </summary>
    private ValueTask<Guid?> RecordAcceptedAsync(
        AuthorizationDecision decision, SchemaId schema, string recordId, CancellationToken ct)
        => accepted is null
            ? ValueTask.FromResult<Guid?>(null)
            : accepted.RecordAsync(
                RecordWrittenEventType,
                decision,
                new Dictionary<string, object?> { ["recordId"] = recordId, ["schema"] = schema.Value },
                ct);
    /// <summary>
    /// Stage two (ADR 0065 clause 4), with its refusal recorded where the decision trace reads it
    /// (ticket 151). Every record write on this coordinator goes through here so the trace entry cannot
    /// be present on one path and missing on another. The sink is optional because an embedder may
    /// compose the coordinator without an audit trail; the REFUSAL is not optional either way.
    /// </summary>
    // holds RW-2, RW-5 · closes RW-H1, RW-H2, RW-H7: the ONE place this writer validates, so no record
    // path reaches persistence unvalidated and a validator fault propagates instead of passing.
    private async Task<ValidatedRecordBody> AdmitAsync(
        AuthorizationDecision decision,
        SchemaId schema, JsonDocument body, AuthorizationWriteContext authority, CancellationToken ct)
    {
        try
        {
            return await ValidatedRecordBody.AdmitAsync(validator, decision, schema, body, ct).ConfigureAwait(false);
        }
        catch (EntityValidationException refusal)
        {
            if (refusals is not null)
                refusal.AuditId = await refusals.RecordValidationRefusalAsync(
                    refusal.ReasonCode, refusal.Pointers, TeamRolePermissions.RecordsWrite,
                    authority.Principal, authority.Tenant, authority.At, ct).ConfigureAwait(false);
            throw;
        }
    }

    internal NodeEntityWriter(
        IDbContextFactory<LocalNodeDbContext> factory,
        IEntityValidator validator,
        AuthorizationGate gate)
        : this(factory, null!, validator, gate)
    {
    }

    private static readonly AuthorizationOperation RecordsWrite =
        AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite);

    public async ValueTask AuthorizeAsync(
        string recordId,
        ActorId principal,
        TenantId tenant,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        var scope = ScopeExpression.Parse($"/records/{recordId}");
        var decision = await gate.DecideAsync(
            new AuthorizationGateRequest(
                new PermissionAtom(RecordsWrite, scope),
                principal,
                tenant,
                new AuthorizationTarget("record", recordId, scope),
                at),
            ct).ConfigureAwait(false);
        decision.RequireAllowed();
    }

    public async ValueTask<LegalEntityWritten> CreateLegalEntityAsync(
        CreateLegalEntityCommand command,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var decision = await gate.DecideAsync(authority.Request(RecordsWrite, "record", command.Id.Value), ct)
            .ConfigureAwait(false);
        decision.RequireAllowed();

        // Stage two (ADR 0065 clause 4): authority validation runs after the gate and BEFORE any
        // device-local parsing, so the refusal a caller sees is the authority's — named, pointed at
        // the failing member, and identical on every path that writes this record type. The null
        // object is gone: whatever is composed here is a real validator (ticket 151, L1418).
        using (var candidate = JsonSerializer.SerializeToDocument(new
        {
            legalName = command.LegalName,
            kind = command.Kind,
            taxClassification = command.TaxClassification,
            commonControlGroupId = command.CommonControlGroupId,
        }))
        {
            // The token is discarded: this path persists through EF, not the entity store. It is minted
            // anyway so the refusal recording below is the one place every record write validates.
            _ = await AdmitAsync(decision, Health.EntityRoutes.LegalEntitySchema, candidate, authority, ct)
                .ConfigureAwait(false);
        }

        // Device-local parsing of the already-validated body. Unreachable for a body the authority
        // accepted; kept as the local guard for an embedder that composes its own schema.
        if (string.IsNullOrWhiteSpace(command.LegalName))
            throw new ArgumentException("legalName is required.", nameof(command));
        if (!Enum.TryParse<EntityKind>(command.Kind, true, out var kind))
            throw new ArgumentException($"kind must be one of: {string.Join(", ", Enum.GetNames<EntityKind>())}.", nameof(command));
        if (!Enum.TryParse<TaxClassification>(command.TaxClassification, true, out var taxClass))
            throw new ArgumentException($"taxClassification must be one of: {string.Join(", ", Enum.GetNames<TaxClassification>())}.", nameof(command));

        var instant = (Instant)authority.At;
        var entity = new LegalEntity(
            command.Id,
            authority.Tenant,
            command.LegalName.Trim(),
            kind,
            taxClass,
            command.CommonControlGroupId,
            instant,
            instant);

        await using var context = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        context.Set<LegalEntity>().Add(entity);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        return new LegalEntityWritten(
            entity,
            await RecordAcceptedAsync(decision, Health.EntityRoutes.LegalEntitySchema, command.Id.Value, ct)
                .ConfigureAwait(false));
    }

    public async ValueTask<EntityId> CreateAsync(
        SchemaId schema,
        JsonDocument body,
        CreateOptions options,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        if (options.Tenant != authority.Tenant)
            throw new ArgumentException("The entity tenant does not match the write authority.", nameof(options));
        var recordId = options.ExplicitLocalPart ?? options.Nonce;
        // holds RW-1 · closes RW-H4: the gate decides first; validation runs only after RequireAllowed,
        // so an unauthorized caller learns nothing about the schema. RW-9 is held by the signature now:
        // the store's record seam takes a ValidatedRecordBody, which only AdmitAsync below can mint.
        var decision = await gate.DecideAsync(authority.Request(RecordsWrite, "record", recordId), ct)
            .ConfigureAwait(false);
        decision.RequireAllowed();
        var admitted = await AdmitAsync(decision, schema, body, authority, ct).ConfigureAwait(false);
        var created = await entities.CreateAsync(admitted, options with { ValidFrom = authority.At }, ct)
            .ConfigureAwait(false);
        await RecordAcceptedAsync(decision, schema, created.LocalPart, ct).ConfigureAwait(false);
        return created;
    }

    public async ValueTask<VersionId> UpdateAsync(
        EntityId id,
        JsonDocument body,
        UpdateOptions options,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        var decision = await gate.DecideAsync(authority.Request(RecordsWrite, "record", id.LocalPart), ct)
            .ConfigureAwait(false);
        decision.RequireAllowed();
        // The new body is validated against the record's OWN activated schema, so the token the store's
        // record seam receives carries that schema and its schema-match guard passes by construction. A
        // missing entity is refused here with the store's own message; nothing is persisted either way.
        var existing = await entities.GetAsync(id, VersionSelector.Latest, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Entity '{id}' not found.");
        var admitted = await AdmitAsync(decision, existing.Schema, body, authority, ct).ConfigureAwait(false);
        var version = await entities.UpdateAsync(id, admitted, options with { ValidFrom = authority.At }, ct)
            .ConfigureAwait(false);
        await RecordAcceptedAsync(decision, existing.Schema, id.LocalPart, ct).ConfigureAwait(false);
        return version;
    }

    public async ValueTask DeleteAsync(
        EntityId id,
        DeleteOptions options,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        var decision = await gate.DecideAsync(authority.Request(RecordsWrite, "record", id.LocalPart), ct)
            .ConfigureAwait(false);
        decision.RequireAllowed();
        await entities.DeleteAsync(id, options with { ValidFrom = authority.At }, ct).ConfigureAwait(false);
    }

    ValueTask<EntityId> IEntityWriteCoordinator.CreateAsync(
        SchemaId schema,
        JsonDocument body,
        CreateOptions options,
        ActorId principal,
        TenantId tenant,
        DateTimeOffset at,
        CancellationToken ct)
    {
        var recordId = options.ExplicitLocalPart ?? options.Nonce;
        var scope = ScopeExpression.Parse($"/records/{recordId}");
        return CreateAsync(
            schema,
            body,
            options,
            new AuthorizationWriteContext(principal, tenant, at),
            ct);
    }

    ValueTask IEntityWriteCoordinator.AuthorizeAsync(
        string recordId,
        ActorId principal,
        TenantId tenant,
        DateTimeOffset at,
        CancellationToken ct) => AuthorizeAsync(recordId, principal, tenant, at, ct);

    ValueTask IEntityWriteCoordinator.DeleteAsync(
        EntityId id,
        DeleteOptions options,
        ActorId principal,
        TenantId tenant,
        DateTimeOffset at,
        CancellationToken ct)
    {
        return DeleteAsync(
            id,
            options,
            new AuthorizationWriteContext(principal, tenant, at),
            ct);
    }
}
