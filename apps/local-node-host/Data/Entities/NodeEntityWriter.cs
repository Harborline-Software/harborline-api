using System.Text.Json;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Entities;

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
    EntityBodyAdmission admission,
    NodeRecordSchemas schemas,
    AuthorizationGate gate,
    EntityValidationRefusalAudit? refusals = null) : IEntityWriteCoordinator
{
    internal NodeEntityWriter(
        IDbContextFactory<LocalNodeDbContext> factory,
        EntityBodyAdmission admission,
        NodeRecordSchemas schemas,
        AuthorizationGate gate)
        : this(factory, null!, admission, schemas, gate, null)
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

    public async ValueTask<LegalEntity> CreateLegalEntityAsync(
        CreateLegalEntityCommand command,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var decision = await gate.DecideAsync(authority.Request(RecordsWrite, "record", command.Id.Value), ct)
            .ConfigureAwait(false);
        decision.RequireAllowed();

        // Stage two: the registered legal-entity schema is the authority on the body's shape, so the
        // refusal carries a reason code and the failing pointers instead of a hand-rolled message.
        using var candidate = JsonSerializer.SerializeToDocument(new
        {
            legalName = command.LegalName,
            kind = command.Kind,
            taxClassification = command.TaxClassification,
            commonControlGroupId = command.CommonControlGroupId,
        });
        var validated = await admission.AdmitAuditedAsync(
            refusals,
            decision,
            await schemas.LegalEntityAsync().ConfigureAwait(false),
            candidate,
            authority,
            command.Id.Value,
            ct).ConfigureAwait(false);

        // The schema's enums are generated from these two enum types, so a validated body parses.
        var kind = Enum.Parse<EntityKind>(validated.Body.RootElement.GetProperty("kind").GetString()!, true);
        var taxClass = Enum.Parse<TaxClassification>(
            validated.Body.RootElement.GetProperty("taxClassification").GetString()!, true);

        var instant = (Instant)authority.At;
        var entity = new LegalEntity(
            command.Id,
            authority.Tenant,
            command.LegalName!.Trim(),
            kind,
            taxClass,
            command.CommonControlGroupId,
            instant,
            instant);

        await using var context = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        context.Set<LegalEntity>().Add(entity);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        return entity;
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
        var decision = await gate.DecideAsync(authority.Request(RecordsWrite, "record", recordId), ct)
            .ConfigureAwait(false);
        decision.RequireAllowed();
        var validated = await admission.AdmitAuditedAsync(
            refusals, decision, schema, body, authority, recordId, ct).ConfigureAwait(false);
        return await entities.CreateAsync(validated, options with { ValidFrom = authority.At }, ct)
            .ConfigureAwait(false);
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
        // An update is validated against the schema the STORED record declares, not one the caller
        // names; the store re-checks the token's schema against the record before appending.
        var stored = await entities.GetAsync(id, default, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Entity '{id}' not found.");
        var validated = await admission.AdmitAuditedAsync(
            refusals, decision, stored.Schema, body, authority, id.LocalPart, ct).ConfigureAwait(false);
        return await entities.UpdateAsync(id, validated, options with { ValidFrom = authority.At }, ct)
            .ConfigureAwait(false);
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
