using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Kernel.Runtime;

namespace Harborline.Api.Blocks.AccessGrant;

/// <summary>
/// The one authorization configuration write path, in ADR 0038 stage order.
/// </summary>
public sealed class AuthorizationDefinitionWriter : IPackProjectionParticipant
{
    private readonly IAuthorizationConfigurationStore store;
    private readonly AuthorizationConfigurationStateReader states;
    private readonly AuthorizationDefinitionAdmission definitionAdmission;
    private readonly AuthorizationCapabilityBindingAdmission bindingAdmission;
    private readonly AuthorizationGate gate;
    private readonly IGrantStore grants;
    private readonly IWritePipelineObserver? pipelineObserver;

    public void StageProjection(PackProjectionTransaction transaction) => transaction.Enlist(store);

    public AuthorizationDefinitionWriter(
        IAuthorizationConfigurationStore store,
        AuthorizationConfigurationStateReader states,
        AuthorizationDefinitionAdmission definitionAdmission,
        AuthorizationCapabilityBindingAdmission bindingAdmission,
        AuthorizationGate gate,
        IGrantStore grants,
        IWritePipelineObserver? pipelineObserver = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.states = states ?? throw new ArgumentNullException(nameof(states));
        this.definitionAdmission = definitionAdmission ?? throw new ArgumentNullException(nameof(definitionAdmission));
        this.bindingAdmission = bindingAdmission ?? throw new ArgumentNullException(nameof(bindingAdmission));
        this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
        this.grants = grants ?? throw new ArgumentNullException(nameof(grants));
        this.pipelineObserver = pipelineObserver;
    }

    /// <summary>
    /// ck-10 (DES-0029): an admission conferral runs the same six ADR 0038 stages, inside the caller's open
    /// transaction. Authorize checks the carried admission authority before anything is read; bind reads
    /// through <paramref name="unit"/>, so it sees the caller's uncommitted state; mutate derives the
    /// per-admission definitions and the grant; validate admits each definition and seals it; commit hands
    /// the seals and the grant to <paramref name="unit"/>, which stages them with their audit and saves them
    /// in the caller's transaction; react returns the grant. A refusal at any stage throws inside that
    /// transaction, so nothing is persisted. Returns null when this admission's grant already exists.
    /// </summary>
    internal static async ValueTask<AccessGrant?> ConferAdmissionAsync(
        AdmissionConferral conferral,
        AdmissionConferralAuthority authority,
        IAdmissionConferralUnit unit,
        IWritePipelineObserver? observer,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conferral);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(unit);
        IReadOnlyDictionary<AuthorizationCapabilityDefinitionId, long>? bound = null;
        (AccessGrant Grant, AuthorizationCapabilityDefinition[] Definitions)? mutation = null;
        ValidatedAdmissionConferral? validated = null;
        foreach (var stage in WritePipeline.Order)
        {
            observer?.OnStage(stage);
            ct.ThrowIfCancellationRequested();
            switch (stage)
            {
                case WritePipelineStage.Authorize:
                    authority.Authorize(conferral);
                    break;
                case WritePipelineStage.Bind:
                    if (await unit.GrantExistsAsync(new GrantId(conferral.GrantId), ct).ConfigureAwait(false))
                        return null;
                    var revisions = new Dictionary<AuthorizationCapabilityDefinitionId, long>();
                    foreach (var permission in conferral.Permissions.Permissions)
                    {
                        var id = conferral.DefinitionIdFor(permission);
                        revisions[id] = await unit.DefinitionRevisionAsync(id, ct).ConfigureAwait(false);
                    }
                    bound = revisions;
                    break;
                case WritePipelineStage.Mutate:
                    mutation = MutateConferral(conferral);
                    break;
                case WritePipelineStage.Validate:
                    validated = await ValidateConferralAsync(conferral, authority,
                            bound ?? throw new InvalidOperationException("Bind must precede validate."),
                            mutation ?? throw new InvalidOperationException("Mutate must precede validate."), ct)
                        .ConfigureAwait(false);
                    break;
                case WritePipelineStage.Commit:
                    await unit.CommitAsync(
                            validated ?? throw new InvalidOperationException("Validate must precede commit."), ct)
                        .ConfigureAwait(false);
                    break;
                case WritePipelineStage.React:
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported kernel write stage '{stage}'.");
            }
        }

        return (validated ?? throw new InvalidOperationException("Kernel write pipeline did not react.")).Grant;
    }

    private static (AccessGrant, AuthorizationCapabilityDefinition[]) MutateConferral(AdmissionConferral conferral)
    {
        var role = conferral.Role;
        var definitions = conferral.Permissions.Permissions.Select(permission =>
        {
            var operation = AuthorizationOperation.Parse(permission);
            return new AuthorizationCapabilityDefinition(conferral.DefinitionIdFor(permission),
                AccessGrantAuthorizationSeed.PackageId, 1, operation,
                new PermissionAtom(operation, ScopeExpression.Parse("/")), RoleBindingSet.From([role]));
        }).ToArray();
        var granter = new ActorId(conferral.AdmittedByPartyId);
        var grant = new AccessGrant(new GrantId(conferral.GrantId), conferral.Tenant,
            new ActorId(conferral.AdmittedPartyId), role, ScopeExpression.Parse("/"), GrantResidency.Cache,
            new GrantValidity(conferral.IssuedAt), GranterKind.Person, granter, conferral.IssuedAt,
            new GrantProvenance(GrantSourceKind.Manual,
                new GrantReason(GrantReasonCodes.Manual, conferral.Nonce.ToString("D")), granter),
            conferral.IssuedAt, GrantStatus.Active, null);
        return (grant, definitions);
    }

    private static async ValueTask<ValidatedAdmissionConferral> ValidateConferralAsync(
        AdmissionConferral conferral,
        AdmissionConferralAuthority authority,
        IReadOnlyDictionary<AuthorizationCapabilityDefinitionId, long> bound,
        (AccessGrant Grant, AuthorizationCapabilityDefinition[] Definitions) mutation,
        CancellationToken ct)
    {
        var roles = new InMemoryRoleVocabulary(
            [AccessGrantAuthorizationSeed.AdmissionMigrationRole(conferral.GrantId, conferral.Tenant)]);
        var admission = new AuthorizationDefinitionAdmission(roles);
        var seals = new List<ValidatedAuthorizationConfigurationWrite>(mutation.Definitions.Length);
        foreach (var definition in mutation.Definitions)
        {
            if (!bound.TryGetValue(definition.DefinitionId, out var revision) || revision != 0)
                throw new InvalidOperationException("The admission's authorization definition is already installed.");
            await admission.AdmitAsync(definition, conferral.Tenant, previous: null, ct: ct).ConfigureAwait(false);
            seals.Add(new ValidatedAuthorizationConfigurationWrite(
                AuthorizationConfigurationWriteKind.InstallDefinition, definition, null, 0, 0,
                conferral.IssuedAt, conferral.Tenant, authority.Decision,
                new ActorId(conferral.AdmittedByPartyId), conferral.Tenant));
        }

        return new ValidatedAdmissionConferral(mutation.Grant, conferral.SourceReference, seals, roles, authority.Decision);
    }

    /// <summary>Runs authorize → bind → mutate → validate → commit → react.</summary>
    public async ValueTask<AuthorizationConfigurationWriteResult> WriteAsync(
        AuthorizationConfigurationCommand command,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
        => await WriteCoreAsync(command, authority, bootstrapDecision: null, additiveSeedRevision: false,
                packAuthority: null, ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Installs one checked-in additive system definition through the ordinary admitted write stages and
    /// ordinary configuration commit. This is deliberately not the founding bootstrap path: a package
    /// revision remains installable after Administrator history exists.
    /// </summary>
    internal async ValueTask<AuthorizationConfigurationWriteResult> WriteAdditiveSeedRevisionAsync(
        InstallAuthorizationDefinition command,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        if (authority.Principal != AccessGrantAuthorizationSeed.AdditiveSeedPrincipal ||
            command.DeclaringTenantId is not null ||
            !AccessGrantAuthorizationSeed.IsAdditiveSystemDefinition(command.Definition))
        {
            throw new ArgumentException(
                "Only the server-derived authorization seed may install a checked-in additive system definition.",
                nameof(command));
        }

        return await WriteCoreAsync(command, authority, bootstrapDecision: null, additiveSeedRevision: true,
                packAuthority: null, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// (L675) Installs or advances the authorization capability definition a pack declares, carrying
    /// the pack's OWN projection authority as the decision rather than re-deciding one: the installer
    /// already decided <c>packages:operate</c> for this exact activated version, and the projector runs
    /// inside that admission. Every later stage is the ordinary one — the same
    /// <see cref="AuthorizationDefinitionAdmission"/> a platform seed and a tenant replacement pass —
    /// so a pack's offered roles ARE the publisher ceiling and are bounded by every rule that admission
    /// holds. Re-projecting an identical definition is a no-op, so a boot-time re-projection is
    /// idempotent. A pack can express no grant here at all: this path writes definitions only.
    /// </summary>
    /// <returns>The write result, or null when the definition was already projected unchanged.</returns>
    public async ValueTask<AuthorizationConfigurationWriteResult?> WritePackDefinitionAsync(
        AuthorizationCapabilityDefinition definition,
        PackProjectionAuthority authority,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(authority);
        // ck-10: authorize precedes every read. The no-op check below reads state before the pipeline
        // runs, so the carried authority is refused here first, exactly as the authorize stage would.
        authority.EnsureUsable();
        if (!string.Equals(definition.PublisherPackageId, authority.PackId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A pack may publish an authorization definition only under its own pack key.",
                nameof(definition));
        }

        var context = new AuthorizationWriteContext(
            new ActorId($"pack:{authority.PackId}"), authority.Tenant, authority.ActivationInstant);
        // (L675) The DECLARING TENANT is the tenant the pack is installed into. Ticket 204's store reads
        // a null declaring tenant as "visible in every tenant", so omitting it would make one tenant's
        // pack the ceiling for tenants that never installed it -- and leave WithdrawPackDefinitionAsync,
        // which narrows one tenant's binding, unable to take it back anywhere else.
        var bound = await states.ReadStateAsync(definition.DefinitionId, authority.Tenant, ct)
            .ConfigureAwait(false);
        if (bound.Definition is { } current)
        {
            if (current == definition) return null;
            definition = definition with { Revision = current.Revision + 1 };
        }

        AuthorizationConfigurationCommand command = bound.Definition is null
            ? new InstallAuthorizationDefinition(definition, authority.Tenant)
            : new ReplaceAuthorizationDefinition(definition, authority.Tenant);
        return await WriteCoreAsync(command, context, bootstrapDecision: null,
            additiveSeedRevision: false, packAuthority: authority, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// (L675) Reverse projection for a pack capability binding: the tenant selection is narrowed to
    /// EMPTY, so the withdrawn pack's capability offers nothing to anyone. The definition revision
    /// itself is retained — this store is append-only and narrow-only, exactly as a taxonomy version is
    /// retired rather than deleted — and the binding is what makes it effective.
    /// </summary>
    /// <returns>Whether a binding was narrowed (false when the definition was never installed).</returns>
    public async ValueTask<bool> WithdrawPackDefinitionAsync(
        AuthorizationCapabilityDefinitionId definitionId,
        PackProjectionAuthority authority,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        authority.EnsureUsable(); // ck-10: refuse before the pre-pipeline read, as in WritePackDefinitionAsync.
        var bound = await states.ReadStateAsync(definitionId, authority.Tenant, ct).ConfigureAwait(false);
        if (bound.Definition is null || bound.EffectiveBinding.Equals(RoleBindingSet.Empty)) return false;
        var context = new AuthorizationWriteContext(
            new ActorId($"pack:{authority.PackId}"), authority.Tenant, authority.ActivationInstant);
        await WriteCoreAsync(
            new NarrowCapabilityRoleBinding(
                authority.Tenant, definitionId, RoleBindingSet.Empty, context.Principal,
                authority.ActivationInstant, new BindingChangeReason("pack-withdrawn")),
            context, bootstrapDecision: null, additiveSeedRevision: false, packAuthority: authority, ct)
            .ConfigureAwait(false);
        return true;
    }

    internal async ValueTask<AuthorizationConfigurationWriteResult> WriteBootstrapAsync(
        AuthorizationConfigurationCommand command,
        PlatformBootstrapDecision bootstrap,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        var bootstrapDecision = bootstrap.Decision;
        return await WriteCoreAsync(command, bootstrapDecision.Request is { } request
            ? new AuthorizationWriteContext(request.Principal, request.Tenant, request.At)
            : throw new ArgumentException("A bootstrap decision requires a request.", nameof(bootstrapDecision)),
            bootstrapDecision,
            additiveSeedRevision: false,
            packAuthority: null,
            ct).ConfigureAwait(false);
    }

    private async ValueTask<AuthorizationConfigurationWriteResult> WriteCoreAsync(
        AuthorizationConfigurationCommand command,
        AuthorizationWriteContext authority,
        AuthorizationDecision? bootstrapDecision,
        bool additiveSeedRevision,
        PackProjectionAuthority? packAuthority,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var stages = new List<string>(6);
        AuthorizationDecision? decision = null;
        AuthorizationConfigurationState? bound = null;
        AuthorizationMutation? mutation = null;
        ValidatedAuthorizationConfigurationWrite? validated = null;
        AuthorizationConfigurationWriteResult? result = null;

        foreach (var stage in WritePipeline.Order)
        {
            switch (stage)
            {
                case WritePipelineStage.Authorize:
                    decision = await AuthorizeAsync(command, authority, bootstrapDecision, additiveSeedRevision,
                            packAuthority, stages, ct)
                        .ConfigureAwait(false);
                    break;
                case WritePipelineStage.Bind:
                    bound = await BindAsync(command, stages, ct).ConfigureAwait(false);
                    break;
                case WritePipelineStage.Mutate:
                    mutation = await MutateAsync(command,
                            bound ?? throw new InvalidOperationException("Bind must precede mutate."), stages, ct)
                        .ConfigureAwait(false);
                    break;
                case WritePipelineStage.Validate:
                    validated = await ValidateAsync(command,
                            bound ?? throw new InvalidOperationException("Bind must precede validate."),
                            mutation ?? throw new InvalidOperationException("Mutate must precede validate."),
                            authority, decision, packAuthority is not null, stages, ct)
                        .ConfigureAwait(false);
                    break;
                case WritePipelineStage.Commit:
                    await CommitAsync(validated ?? throw new InvalidOperationException("Validate must precede commit."),
                            authority, bootstrapDecision, stages, ct)
                        .ConfigureAwait(false);
                    break;
                case WritePipelineStage.React:
                    result = await ReactAsync(
                            validated ?? throw new InvalidOperationException("Validate must precede react."), stages, ct)
                        .ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported kernel write stage '{stage}'.");
            }
        }

        return (result ?? throw new InvalidOperationException("Kernel write pipeline did not react.")) with
        {
            Decision = decision,
            AuditId = store is IAuditingAuthorizationConfigurationStore ? validated?.AuditId : null,
        };
    }

    private async ValueTask<AuthorizationDecision?> AuthorizeAsync(
        AuthorizationConfigurationCommand command,
        AuthorizationWriteContext authority,
        AuthorizationDecision? bootstrapDecision,
        bool additiveSeedRevision,
        PackProjectionAuthority? packAuthority,
        List<string> stages,
        CancellationToken ct)
    {
        RecordStage(WritePipelineStage.Authorize, stages);
        ct.ThrowIfCancellationRequested();
        if (bootstrapDecision is not null)
        {
            bootstrapDecision.RequireAllowed();
            if (bootstrapDecision.Resolution.All(step => step.Stage != AuthorizationResolutionStage.Bootstrap))
                throw new ArgumentException("The carried bootstrap decision lacks bootstrap derivation evidence.", nameof(bootstrapDecision));
            return null;
        }
        if (packAuthority is not null)
        {
            // The pack installer already decided packages:operate for this exact activated version and
            // minted the authority the projector is running under; re-deciding here would be a SECOND
            // decision over the same act. The remaining five stages are the ordinary ones.
            packAuthority.EnsureUsable();
            return null;
        }
        if (additiveSeedRevision)
        {
            // WriteAdditiveSeedRevisionAsync already proved the exact checked-in definition and the
            // server-derived seed principal. The remaining five stages are identical to an ordinary write,
            // including definition admission and the non-bootstrap CommitAsync path.
            return null;
        }
        (TenantId? Tenant, string Id) target = command switch
        {
            InstallAuthorizationDefinition install => (install.DeclaringTenantId, install.Definition.DefinitionId.Value.ToString()),
            ReplaceAuthorizationDefinition replace => (replace.DeclaringTenantId, replace.Definition.DefinitionId.Value.ToString()),
            NarrowCapabilityRoleBinding narrow => (narrow.TenantId, narrow.DefinitionId.Value.ToString()),
            _ => throw new InvalidOperationException($"Unsupported authorization command '{command.GetType().Name}'."),
        };
        if (target.Tenant is { } declaredTenant && declaredTenant != authority.Tenant)
            throw new ArgumentException("The authorization command tenant does not match the write authority.", nameof(command));
        var decision = await gate.DecideAsync(
            authority.Request(AuthorizationOperation.Parse(Permission.GrantPermissions), "grant", target.Id), ct)
            .ConfigureAwait(false);
        decision.RequireAllowed();
        return decision;
    }

    private async ValueTask<AuthorizationConfigurationState> BindAsync(
        AuthorizationConfigurationCommand command,
        List<string> stages,
        CancellationToken ct)
    {
        RecordStage(WritePipelineStage.Bind, stages);
        return command switch
        {
            InstallAuthorizationDefinition install =>
                await states.ReadStateAsync(install.Definition.DefinitionId, ct: ct).ConfigureAwait(false),
            ReplaceAuthorizationDefinition replace =>
                await states.ReadStateAsync(
                    replace.Definition.DefinitionId,
                    replace.DeclaringTenantId,
                    ct).ConfigureAwait(false),
            NarrowCapabilityRoleBinding narrow =>
                await states.ReadStateAsync(narrow.DefinitionId, narrow.TenantId, ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported authorization command '{command.GetType().Name}'."),
        };
    }

    private ValueTask<AuthorizationMutation> MutateAsync(
        AuthorizationConfigurationCommand command,
        AuthorizationConfigurationState bound,
        List<string> stages,
        CancellationToken ct)
    {
        RecordStage(WritePipelineStage.Mutate, stages);
        ct.ThrowIfCancellationRequested();
        var mutation = command switch
        {
            InstallAuthorizationDefinition install =>
                AuthorizationMutation.ForDefinition(install.Definition),
            ReplaceAuthorizationDefinition replace =>
                AuthorizationMutation.ForDefinition(replace.Definition),
            NarrowCapabilityRoleBinding narrow =>
                AuthorizationMutation.ForBinding(new CapabilityRoleBindingRevision(
                    narrow.TenantId,
                    narrow.DefinitionId,
                    bound.BindingRevision + 1,
                    narrow.SelectedRoles,
                    narrow.ChangedBy,
                    narrow.ChangedAt,
                    narrow.Reason)),
            _ => throw new InvalidOperationException($"Unsupported authorization command '{command.GetType().Name}'."),
        };
        return ValueTask.FromResult(mutation);
    }

    private async ValueTask<ValidatedAuthorizationConfigurationWrite> ValidateAsync(
        AuthorizationConfigurationCommand command,
        AuthorizationConfigurationState bound,
        AuthorizationMutation mutation,
        AuthorizationWriteContext authority,
        AuthorizationDecision? decision,
        bool packPublished,
        List<string> stages,
        CancellationToken ct)
    {
        RecordStage(WritePipelineStage.Validate, stages);
        var at = authority.At;
        switch (command)
        {
            case InstallAuthorizationDefinition install:
                if (bound.Definition is not null)
                {
                    throw new InvalidOperationException("The authorization definition is already installed.");
                }

                // ck-10: admit the exact mutate output that is sealed below, so the persisted payload is
                // the validated one by construction rather than because mutate happens to copy the command.
                var installed = mutation.Definition
                    ?? throw new InvalidOperationException("Mutate produced no definition.");
                await definitionAdmission.AdmitAsync(
                    installed,
                    install.DeclaringTenantId,
                    previous: null,
                    packPublished,
                    ct).ConfigureAwait(false);
                return new ValidatedAuthorizationConfigurationWrite(
                    AuthorizationConfigurationWriteKind.InstallDefinition,
                    installed,
                    bindingRevision: null,
                    expectedDefinitionRevision: 0,
                    expectedBindingRevision: 0,
                    definitionEffectiveAt: at,
                    declaringTenantId: install.DeclaringTenantId,
                    decision, authority.Principal, authority.Tenant);

            case ReplaceAuthorizationDefinition replace:
                var previous = bound.Definition
                    ?? throw new InvalidOperationException("The authorization definition is not installed.");
                var replacement = mutation.Definition
                    ?? throw new InvalidOperationException("Mutate produced no definition.");
                await definitionAdmission.AdmitAsync(
                    replacement,
                    replace.DeclaringTenantId,
                    previous,
                    packPublished,
                    ct).ConfigureAwait(false);
                return new ValidatedAuthorizationConfigurationWrite(
                    AuthorizationConfigurationWriteKind.ReplaceDefinition,
                    replacement,
                    bindingRevision: null,
                    expectedDefinitionRevision: previous.Revision,
                    expectedBindingRevision: 0,
                    definitionEffectiveAt: at,
                    declaringTenantId: replace.DeclaringTenantId,
                    decision, authority.Principal, authority.Tenant);

            case NarrowCapabilityRoleBinding narrow:
                var definition = bound.Definition
                    ?? throw new InvalidOperationException("The authorization definition is not installed.");
                var revision = mutation.BindingRevision
                    ?? throw new InvalidOperationException("Mutate produced no binding revision.");
                bindingAdmission.Admit(
                    definition.OfferedRoles,
                    bound.EffectiveBinding,
                    revision.SelectedRoles);
                return new ValidatedAuthorizationConfigurationWrite(
                    AuthorizationConfigurationWriteKind.NarrowBinding,
                    definition: null,
                    revision,
                    expectedDefinitionRevision: definition.Revision,
                    expectedBindingRevision: bound.BindingRevision,
                    definitionEffectiveAt: null,
                    declaringTenantId: null,
                    decision, authority.Principal, authority.Tenant);

            default:
                throw new InvalidOperationException($"Unsupported authorization command '{command.GetType().Name}'.");
        }
    }

    private async ValueTask CommitAsync(
        ValidatedAuthorizationConfigurationWrite write,
        AuthorizationWriteContext authority,
        AuthorizationDecision? bootstrapDecision,
        List<string> stages,
        CancellationToken ct)
    {
        RecordStage(WritePipelineStage.Commit, stages);
        if (bootstrapDecision is null)
            await store.CommitAsync(write, ct).ConfigureAwait(false);
        else
            await store.CommitBootstrapAsync(write, authority.Tenant, grants, ct).ConfigureAwait(false);
    }

    private ValueTask<AuthorizationConfigurationWriteResult> ReactAsync(
        ValidatedAuthorizationConfigurationWrite write,
        List<string> stages,
        CancellationToken ct)
    {
        RecordStage(WritePipelineStage.React, stages);
        ct.ThrowIfCancellationRequested();
        var completedStages = stages.ToArray();
        if (write.BindingRevision is not { } revision)
        {
            return ValueTask.FromResult(new AuthorizationConfigurationWriteResult(
                write.Definition, null, completedStages));
        }

        BindingWarningCode? warning = revision.SelectedRoles.Equals(
            Harborline.Api.Foundation.IdentityAtlas.Permissions.RoleBindingSet.Empty)
            ? BindingWarningCode.EmptyBinding
            : null;
        return ValueTask.FromResult(new AuthorizationConfigurationWriteResult(
            null,
            new BindingChangeResult(revision, revision.SelectedRoles, warning),
            completedStages));
    }

    private sealed record AuthorizationMutation(
        AuthorizationCapabilityDefinition? Definition,
        CapabilityRoleBindingRevision? BindingRevision)
    {
        public static AuthorizationMutation ForDefinition(AuthorizationCapabilityDefinition definition) =>
            new(definition, null);

        public static AuthorizationMutation ForBinding(CapabilityRoleBindingRevision bindingRevision) =>
            new(null, bindingRevision);
    }

    private void RecordStage(WritePipelineStage stage, List<string> stages)
    {
        stages.Add(WritePipeline.NameOf(stage));
        pipelineObserver?.OnStage(stage);
    }
}
