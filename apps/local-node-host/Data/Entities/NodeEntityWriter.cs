using System.Text.Json;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime;
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

/// <summary>A generic persisted record and the accepted decision audit that authorized it.</summary>
public sealed record EntityWritten(EntityId Entity, Guid? AuditId);

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
    Health.AuthorizedActAudit? accepted = null,
    IWritePipelineObserver? pipelineObserver = null) : IEntityWriteCoordinator
{
    /// <summary>The event type an accepted record write is recorded under (ticket 331 slice 2).</summary>
    public static readonly Kernel.Audit.AuditEventType RecordWrittenEventType = new("RecordWritten");

    /// <summary>The event type an accepted record delete is recorded under (DES-0029 ck-10 S2).</summary>
    public static readonly Kernel.Audit.AuditEventType RecordDeletedEventType = new("RecordDeleted");

    /// <summary>
    /// Records the accepted write against the ONE decision that permitted it and returns that entry's
    /// audit id. Every accepted write on this coordinator goes through here, so no write path is
    /// addressable while a sibling is silent. It carries the record's identity and schema and never a
    /// member of its body.
    /// </summary>
    private ValueTask<Guid?> RecordAcceptedAsync(
        AuthorizationDecision decision, Kernel.Audit.AuditEventType eventType, SchemaId schema, string recordId,
        CancellationToken ct)
        => accepted is null
            ? ValueTask.FromResult<Guid?>(null)
            : accepted.RecordAsync(
                eventType,
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
        SchemaId schema, JsonDocument body, TenantId tenant, EntityBinding? binding,
        AuthorizationWriteContext authority, CancellationToken ct)
    {
        try
        {
            return await ValidatedRecordBody.AdmitAsync(
                validator, decision, schema, body, tenant, binding, ct).ConfigureAwait(false);
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

    // The bind guard update and delete share, kept as a method whose parameter it names (CA2208).
    private static Entity RequireAuthorityTenant(EntityId id, Entity existing, TenantId authorityTenant) =>
        existing.Tenant == authorityTenant
            ? existing
            : throw new ArgumentException("The entity tenant does not match the write authority.", nameof(id));

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

    private async ValueTask<AuthorizationDecision> DecideAsync(
        AuthorizationWriteContext authority, string recordId, CancellationToken ct)
    {
        var decision = await gate.DecideAsync(authority.Request(RecordsWrite, "record", recordId), ct)
            .ConfigureAwait(false);
        decision.RequireAllowed();
        return decision;
    }

    // ck-10 S2 (DES-0029): every write on this coordinator runs the six ADR 0038 stages through the kernel
    // executor. None of them settles at bind, so the executor always reaches react and returns its result.
    private async ValueTask<TResult> RunAsync<TBound, TMutation, TSealed, TResult>(
        KernelWrite<TBound, TMutation, TSealed, TResult> write, CancellationToken ct)
        where TBound : class
        => (await WritePipeline.RunAsync(write, pipelineObserver, ct).ConfigureAwait(false))!;

    public ValueTask<LegalEntityWritten> CreateLegalEntityAsync(
        CreateLegalEntityCommand command,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return RunAsync(new LegalEntityCreate(this, factory, command, authority), ct);
    }

    public async ValueTask<EntityId> CreateAsync(
        SchemaId schema,
        JsonDocument body,
        CreateOptions options,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
        => (await CreateWithReceiptAsync(schema, body, options, authority, ct).ConfigureAwait(false)).Entity;

    /// <summary>
    /// Creates a generic record through the canonical gate-then-validator pipeline and returns the audit
    /// id emitted from that same authorization decision. Callers that only need the entity id continue to
    /// use <see cref="CreateAsync(SchemaId, JsonDocument, CreateOptions, AuthorizationWriteContext, CancellationToken)"/>.
    /// </summary>
    public async ValueTask<EntityWritten> CreateWithReceiptAsync(
        SchemaId schema,
        JsonDocument body,
        CreateOptions options,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        if (options.Tenant != authority.Tenant)
            throw new ArgumentException("The entity tenant does not match the write authority.", nameof(options));
        // DES-0029 ck-3: authorize the id the store will write. Without an explicit local part that is the
        // schema-derived id, not the nonce (the same derivation NodeHierarchyCompositeCoordinator uses).
        var recordId = InMemoryEntityStore.DeriveEntityId(schema, options).LocalPart;
        return await CreateWithReceiptAsync(body, recordId, authority,
            _ => ValueTask.FromResult((schema, options)), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a bound schema and its create options only after the canonical write decision allows
    /// this record. Preparation cannot supply a decision or change the admitted tenant or record id.
    /// </summary>
    internal ValueTask<EntityWritten> CreateWithReceiptAsync(
        JsonDocument body,
        string recordId,
        AuthorizationWriteContext authority,
        Func<CancellationToken, ValueTask<(SchemaId Schema, CreateOptions Options)>> prepare,
        CancellationToken ct = default)
        => RunAsync(new RecordCreate(this, entities, body, recordId, authority, prepare), ct);

    public ValueTask<VersionId> UpdateAsync(
        EntityId id,
        JsonDocument body,
        UpdateOptions options,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
        => RunAsync(new RecordUpdate(this, entities, id, body, options, authority), ct);

    public async ValueTask DeleteAsync(
        EntityId id,
        DeleteOptions options,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
        => await RunAsync(new RecordDelete(this, entities, id, options, authority), ct).ConfigureAwait(false);

    /// <summary>The legal-entity record as mutate derived it; a kind is null when it is not a declared name.</summary>
    private sealed record LegalEntityDraft(CreateLegalEntityCommand Stored, EntityKind? Kind, TaxClassification? Tax);

    /// <summary>
    /// A legal-entity create as its six ADR 0038 stages. Mutate derives the record as stored (the trimmed
    /// name, each kind parsed from its declared name only), so validate admits exactly what commit stores.
    /// This path persists through EF, not the entity store.
    /// </summary>
    private sealed class LegalEntityCreate(
        NodeEntityWriter writer,
        IDbContextFactory<LocalNodeDbContext> factory,
        CreateLegalEntityCommand command,
        AuthorizationWriteContext authority)
        : KernelWrite<CreateLegalEntityCommand, LegalEntityDraft, LegalEntity, LegalEntityWritten>
    {
        private AuthorizationDecision decision = null!;

        protected override async ValueTask AuthorizeAsync(CancellationToken ct) =>
            decision = await writer.DecideAsync(authority, command.Id.Value, ct).ConfigureAwait(false);

        /// <summary>A new record: there is no prior state to read.</summary>
        protected override ValueTask<CreateLegalEntityCommand?> BindAsync(CancellationToken ct) =>
            ValueTask.FromResult<CreateLegalEntityCommand?>(command);

        protected override ValueTask<LegalEntityDraft> MutateAsync(CreateLegalEntityCommand bound, CancellationToken ct) =>
            ValueTask.FromResult(new LegalEntityDraft(
                bound with { LegalName = bound.LegalName?.Trim() },
                Declared<EntityKind>(bound.Kind),
                Declared<TaxClassification>(bound.TaxClassification)));

        protected override async ValueTask<LegalEntity> ValidateAsync(
            CreateLegalEntityCommand bound, LegalEntityDraft mutation, CancellationToken ct)
        {
            var stored = mutation.Stored;
            // Stage two (ADR 0065 clause 4): the authority's schema judges the record as it will be stored,
            // so its refusal is the one a caller sees, identical on every path that writes this record type.
            // The token is discarded: this path persists through EF, not the entity store. It is minted
            // anyway so the refusal recording is the one place every record write validates.
            using (var candidate = JsonSerializer.SerializeToDocument(new
            {
                legalName = stored.LegalName,
                kind = stored.Kind,
                taxClassification = stored.TaxClassification,
                commonControlGroupId = stored.CommonControlGroupId,
            }))
            {
                _ = await writer.AdmitAsync(decision, Health.EntityRoutes.LegalEntitySchema, candidate, authority.Tenant,
                    null, authority, ct).ConfigureAwait(false);
            }

            // The local guard for an embedder that composes its own schema: a value the authority admitted
            // but that mutate could not derive is refused, never stored as something validate did not see.
            var (legalName, kind, taxClass) = RequireDerived(bound, mutation);

            var instant = (Instant)authority.At;
            return new LegalEntity(
                stored.Id, authority.Tenant, legalName, kind, taxClass, stored.CommonControlGroupId, instant, instant);
        }

        protected override async ValueTask CommitAsync(LegalEntity validated, CancellationToken ct)
        {
            await using var context = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            context.Set<LegalEntity>().Add(validated);
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        protected override async ValueTask<LegalEntityWritten> ReactAsync(LegalEntity validated, CancellationToken ct) =>
            new(validated, await writer.RecordAcceptedAsync(
                decision, RecordWrittenEventType, Health.EntityRoutes.LegalEntitySchema, validated.Id.Value, ct).ConfigureAwait(false));

        // Validate's local guard, kept as a method whose parameter it names (CA2208): the command is refused.
        private static (string LegalName, EntityKind Kind, TaxClassification Tax) RequireDerived(
            CreateLegalEntityCommand command, LegalEntityDraft mutation)
        {
            if (mutation.Stored.LegalName is not { } legalName || string.IsNullOrWhiteSpace(legalName))
                throw new ArgumentException("legalName is required.", nameof(command));
            if (mutation.Kind is not { } kind)
                throw new ArgumentException($"kind must be one of: {string.Join(", ", Enum.GetNames<EntityKind>())}.", nameof(command));
            if (mutation.Tax is not { } taxClass)
                throw new ArgumentException($"taxClassification must be one of: {string.Join(", ", Enum.GetNames<TaxClassification>())}.", nameof(command));
            return (legalName, kind, taxClass);
        }

        /// <summary>The enum member named exactly <paramref name="value"/>: the schema's own vocabulary, no case folding or numbers.</summary>
        private static T? Declared<T>(string? value) where T : struct, Enum =>
            value is not null && Enum.GetNames<T>().Contains(value, StringComparer.Ordinal) ? Enum.Parse<T>(value) : null;
    }

    private sealed record PreparedRecord(SchemaId Schema, CreateOptions Options);

    /// <summary>A generic record create as its six ADR 0038 stages.</summary>
    private sealed class RecordCreate(
        NodeEntityWriter writer,
        IEntityMutationStore entities,
        JsonDocument body,
        string recordId,
        AuthorizationWriteContext authority,
        Func<CancellationToken, ValueTask<(SchemaId Schema, CreateOptions Options)>> prepare)
        : KernelWrite<PreparedRecord, PreparedRecord, (ValidatedRecordBody Body, CreateOptions Options), EntityWritten>
    {
        private AuthorizationDecision decision = null!;
        private EntityId created;

        // holds RW-1 · closes RW-H4: the gate decides first; validation runs only after RequireAllowed,
        // so an unauthorized caller learns nothing about the schema.
        protected override async ValueTask AuthorizeAsync(CancellationToken ct) =>
            decision = await writer.DecideAsync(authority, recordId, ct).ConfigureAwait(false);

        protected override async ValueTask<PreparedRecord?> BindAsync(CancellationToken ct)
        {
            var (schema, options) = await prepare(ct).ConfigureAwait(false);
            RequirePreparedFor(prepare, options.Tenant == authority.Tenant
                && InMemoryEntityStore.DeriveEntityId(schema, options).LocalPart == recordId);
            return new PreparedRecord(schema, options);
        }

        // Bind's guard, kept as a method whose parameter it names (CA2208): the prepare callback is refused.
        private static void RequirePreparedFor(
            Func<CancellationToken, ValueTask<(SchemaId Schema, CreateOptions Options)>> prepare, bool matchesAuthority)
        {
            if (!matchesAuthority)
                throw new ArgumentException("The prepared entity does not match the admitted tenant and record id.", nameof(prepare));
        }

        protected override ValueTask<PreparedRecord> MutateAsync(PreparedRecord bound, CancellationToken ct) =>
            ValueTask.FromResult(bound with { Options = bound.Options with { ValidFrom = authority.At } });

        // RW-9 is held by the signature: the store's record seam takes a ValidatedRecordBody, which only
        // AdmitAsync can mint.
        protected override async ValueTask<(ValidatedRecordBody Body, CreateOptions Options)> ValidateAsync(
            PreparedRecord bound, PreparedRecord mutation, CancellationToken ct) =>
            (await writer.AdmitAsync(decision, mutation.Schema, body, mutation.Options.Tenant, mutation.Options.Binding,
                authority, ct).ConfigureAwait(false), mutation.Options);

        protected override async ValueTask CommitAsync((ValidatedRecordBody Body, CreateOptions Options) validated, CancellationToken ct) =>
            created = await entities.CreateAsync(validated.Body, validated.Options, ct).ConfigureAwait(false);

        protected override async ValueTask<EntityWritten> ReactAsync(
            (ValidatedRecordBody Body, CreateOptions Options) validated, CancellationToken ct) =>
            new(created, await writer.RecordAcceptedAsync(
                decision, RecordWrittenEventType, validated.Body.Schema, created.LocalPart, ct).ConfigureAwait(false));
    }

    /// <summary>A generic record update as its six ADR 0038 stages.</summary>
    private sealed class RecordUpdate(
        NodeEntityWriter writer,
        IEntityMutationStore entities,
        EntityId id,
        JsonDocument body,
        UpdateOptions options,
        AuthorizationWriteContext authority)
        : KernelWrite<Entity, UpdateOptions, (ValidatedRecordBody Body, UpdateOptions Options), VersionId>
    {
        private AuthorizationDecision decision = null!;
        private VersionId version;

        protected override async ValueTask AuthorizeAsync(CancellationToken ct) =>
            decision = await writer.DecideAsync(authority, id.LocalPart, ct).ConfigureAwait(false);

        // A missing or foreign record is refused here, as delete does (T-1003): the store looks records up
        // by id only, so bind is where the stored tenant meets the write authority's.
        protected override async ValueTask<Entity?> BindAsync(CancellationToken ct)
        {
            var existing = await entities.GetAsync(id, VersionSelector.Latest, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Entity '{id}' not found.");
            return RequireAuthorityTenant(id, existing, authority.Tenant);
        }

        protected override ValueTask<UpdateOptions> MutateAsync(Entity bound, CancellationToken ct) =>
            ValueTask.FromResult(options with { ValidFrom = authority.At });

        // The new body is validated against the record's OWN activated schema, so the token the store's
        // record seam receives carries that schema and its schema-match guard passes by construction.
        protected override async ValueTask<(ValidatedRecordBody Body, UpdateOptions Options)> ValidateAsync(
            Entity bound, UpdateOptions mutation, CancellationToken ct) =>
            (await writer.AdmitAsync(decision, bound.Schema, body, bound.Tenant, bound.Binding, authority, ct)
                .ConfigureAwait(false), mutation);

        protected override async ValueTask CommitAsync((ValidatedRecordBody Body, UpdateOptions Options) validated, CancellationToken ct) =>
            version = await entities.UpdateAsync(id, validated.Body, validated.Options, ct).ConfigureAwait(false);

        protected override async ValueTask<VersionId> ReactAsync(
            (ValidatedRecordBody Body, UpdateOptions Options) validated, CancellationToken ct)
        {
            await writer.RecordAcceptedAsync(decision, RecordWrittenEventType, validated.Body.Schema, id.LocalPart, ct)
                .ConfigureAwait(false);
            return version;
        }
    }

    /// <summary>
    /// A generic record delete as its six ADR 0038 stages: bind reads the record (it must exist and belong to
    /// the write authority's tenant) and react records the accepted act, as every other write here does.
    /// </summary>
    private sealed class RecordDelete(
        NodeEntityWriter writer,
        IEntityMutationStore entities,
        EntityId id,
        DeleteOptions options,
        AuthorizationWriteContext authority)
        : KernelWrite<Entity, DeleteOptions, (Entity Record, DeleteOptions Options), EntityId>
    {
        private AuthorizationDecision decision = null!;

        protected override async ValueTask AuthorizeAsync(CancellationToken ct) =>
            decision = await writer.DecideAsync(authority, id.LocalPart, ct).ConfigureAwait(false);

        protected override async ValueTask<Entity?> BindAsync(CancellationToken ct)
        {
            var existing = await entities.GetAsync(id, VersionSelector.Latest, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Entity '{id}' not found.");
            return RequireAuthorityTenant(id, existing, authority.Tenant);
        }

        protected override ValueTask<DeleteOptions> MutateAsync(Entity bound, CancellationToken ct) =>
            ValueTask.FromResult(options with { ValidFrom = authority.At });

        /// <summary>A tombstone has no body to admit: bind already refused a missing or foreign record.</summary>
        protected override ValueTask<(Entity Record, DeleteOptions Options)> ValidateAsync(
            Entity bound, DeleteOptions mutation, CancellationToken ct) =>
            ValueTask.FromResult((bound, mutation));

        protected override async ValueTask CommitAsync((Entity Record, DeleteOptions Options) validated, CancellationToken ct) =>
            await entities.DeleteAsync(id, validated.Options, ct).ConfigureAwait(false);

        protected override async ValueTask<EntityId> ReactAsync((Entity Record, DeleteOptions Options) validated, CancellationToken ct)
        {
            await writer.RecordAcceptedAsync(decision, RecordDeletedEventType, validated.Record.Schema, id.LocalPart, ct)
                .ConfigureAwait(false);
            return id;
        }
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
