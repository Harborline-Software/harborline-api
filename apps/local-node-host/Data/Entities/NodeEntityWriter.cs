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
            await validator.ValidateAsync(Health.EntityRoutes.LegalEntitySchema, candidate, ct).ConfigureAwait(false);
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
        // The new body is validated against the record's OWN activated schema. A missing entity is
        // the store's refusal to raise, and it cannot persist anything.
        if (await entities.GetAsync(id, VersionSelector.Latest, ct).ConfigureAwait(false) is { } existing)
            await validator.ValidateAsync(existing.Schema, body, ct).ConfigureAwait(false);
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
