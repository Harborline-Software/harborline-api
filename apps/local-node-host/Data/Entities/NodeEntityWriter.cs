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
    IEntityValidator validator,
    AuthorizationGate gate) : IEntityWriteCoordinator
{
    internal NodeEntityWriter(
        IDbContextFactory<LocalNodeDbContext> factory,
        IEntityValidator validator,
        AuthorizationGate gate)
        : this(factory, null!, validator, gate)
    {
    }

    // An absent property must reach the validator as ABSENT, not as null: the DTO binder cannot tell
    // "omitted" from "null", and a schema's `required` is the authority's word on which is which.
    private static readonly JsonSerializerOptions CandidateJson = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

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

        // Stage two, unconditionally (ticket 151, L1418): the authority's validator decides the body
        // BEFORE any persistence work, and before the parses below — the ReferenceEquals escape hatch
        // that skipped a null-object validator is gone, because the registered validator is real.
        using (var candidate = JsonSerializer.SerializeToDocument(new
        {
            legalName = command.LegalName,
            kind = command.Kind,
            taxClassification = command.TaxClassification,
            commonControlGroupId = command.CommonControlGroupId,
        }, CandidateJson))
        {
            await validator.ValidateAsync(Health.EntityRoutes.LegalEntitySchema, candidate, ct).ConfigureAwait(false);
        }

        // The schema admitted the body; these parses only turn admitted text into the domain enums.
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
        // Gate, then validator, then persistence (ADR 0065 clause 4). Validating HERE rather than
        // relying on the store's pre-commit hook is what makes the records path carry stage two on
        // every caller — the hook is shared with platform-definition writes whose envelopes are
        // admitted by their own lifecycle.
        await validator.ValidateAsync(schema, body, ct).ConfigureAwait(false);
        return await entities.CreateAsync(schema, body, options with { ValidFrom = authority.At }, ct)
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
        var current = await entities.GetAsync(id, default, ct).ConfigureAwait(false)
            ?? throw new ArgumentException($"Entity '{id}' does not exist.", nameof(id));
        await validator.ValidateAsync(current.Schema, body, ct).ConfigureAwait(false);
        return await entities.UpdateAsync(id, body, options with { ValidFrom = authority.At }, ct)
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
