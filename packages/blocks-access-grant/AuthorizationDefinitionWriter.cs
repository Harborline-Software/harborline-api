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
    /// ck-10 (DES-0029): an admission conferral runs the six ADR 0038 stages through the kernel executor
    /// (<see cref="WritePipeline.RunAsync"/>), inside the caller's open transaction. Authorize checks the
    /// carried admission authority before anything is read; bind reads
    /// through <paramref name="unit"/>, so it sees the caller's uncommitted state; mutate derives the
    /// per-admission definitions and the grant; validate admits each definition and seals it; commit hands
    /// the seals and the grant to <paramref name="unit"/>, which stages them with their audit and saves them
    /// in the caller's transaction; react returns the grant. A refusal at any stage throws inside that
    /// transaction, so nothing is persisted. Returns null when this admission's grant already exists.
    /// </summary>
    internal static ValueTask<AccessGrant?> ConferAdmissionAsync(
        AdmissionConferral conferral,
        AdmissionConferralAuthority authority,
        IAdmissionConferralUnit unit,
        IWritePipelineObserver? observer,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conferral);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(unit);
        return WritePipeline.RunAsync(new ConferralWrite(conferral, authority, unit), observer, ct);
    }

    /// <summary>One admission conferral as its six ADR 0038 stages, run by the kernel executor.</summary>
    private sealed class ConferralWrite(
        AdmissionConferral conferral,
        AdmissionConferralAuthority authority,
        IAdmissionConferralUnit unit)
        : KernelWrite<IReadOnlyDictionary<AuthorizationCapabilityDefinitionId, long>,
            (AccessGrant Grant, AuthorizationCapabilityDefinition[] Definitions), ValidatedAdmissionConferral, AccessGrant>
    {
        protected override ValueTask AuthorizeAsync(CancellationToken ct)
        {
            authority.Authorize(conferral);
            return ValueTask.CompletedTask;
        }

        /// <summary>Null (settled) when this admission's grant already exists.</summary>
        protected override async ValueTask<IReadOnlyDictionary<AuthorizationCapabilityDefinitionId, long>?> BindAsync(
            CancellationToken ct)
        {
            if (await unit.GrantExistsAsync(new GrantId(conferral.GrantId), ct).ConfigureAwait(false))
                return null;
            var revisions = new Dictionary<AuthorizationCapabilityDefinitionId, long>();
            foreach (var permission in conferral.Permissions.Permissions)
            {
                var id = conferral.DefinitionIdFor(permission);
                revisions[id] = await unit.DefinitionRevisionAsync(id, ct).ConfigureAwait(false);
            }
            return revisions;
        }

        protected override ValueTask<(AccessGrant Grant, AuthorizationCapabilityDefinition[] Definitions)> MutateAsync(
            IReadOnlyDictionary<AuthorizationCapabilityDefinitionId, long> bound, CancellationToken ct)
            => ValueTask.FromResult(MutateConferral(conferral));

        protected override ValueTask<ValidatedAdmissionConferral> ValidateAsync(
            IReadOnlyDictionary<AuthorizationCapabilityDefinitionId, long> bound,
            (AccessGrant Grant, AuthorizationCapabilityDefinition[] Definitions) mutation,
            CancellationToken ct)
            => ValidateConferralAsync(conferral, authority, bound, mutation, ct);

        protected override ValueTask CommitAsync(ValidatedAdmissionConferral validated, CancellationToken ct)
            => unit.CommitAsync(validated, ct);

        protected override ValueTask<AccessGrant> ReactAsync(ValidatedAdmissionConferral validated, CancellationToken ct)
            => ValueTask.FromResult(validated.Grant);
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

    /// <summary>Runs authorize → bind → mutate → validate → commit → react through the kernel executor.</summary>
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
            new ActorId($"pack:{authority.PackId}"), authority.Tenant, authority.Admitted);
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
            new ActorId($"pack:{authority.PackId}"), authority.Tenant, authority.Admitted);
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
            ? new AuthorizationWriteContext(request.Principal, request.Tenant, request.Instant)
            : throw new ArgumentException("A bootstrap decision requires a request.", nameof(bootstrap)),
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
        var write = new ConfigurationWrite(this, command, authority, bootstrapDecision, additiveSeedRevision, packAuthority);
        var result = await WritePipeline.RunAsync(write, pipelineObserver, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Kernel write pipeline did not react.");
        return result with
        {
            Decision = write.Decision,
            AuditId = store is IAuditingAuthorizationConfigurationStore ? write.Sealed?.AuditId : null,
        };
    }

    /// <summary>One authorization configuration command as its six ADR 0038 stages, run by the kernel executor.</summary>
    private sealed class ConfigurationWrite(
        AuthorizationDefinitionWriter writer,
        AuthorizationConfigurationCommand command,
        AuthorizationWriteContext authority,
        AuthorizationDecision? bootstrapDecision,
        bool additiveSeedRevision,
        PackProjectionAuthority? packAuthority)
        : KernelWrite<AuthorizationConfigurationState, AuthorizationMutation, ValidatedAuthorizationConfigurationWrite,
            AuthorizationConfigurationWriteResult>
    {
        public AuthorizationDecision? Decision { get; private set; }

        public ValidatedAuthorizationConfigurationWrite? Sealed { get; private set; }

        // The two argument guards the Authorize stage runs, kept as methods whose parameters they name.
        private static void RequireBootstrapEvidence(AuthorizationDecision bootstrapDecision)
        {
            bootstrapDecision.RequireAllowed();
            if (bootstrapDecision.Resolution.All(step => step.Stage != AuthorizationResolutionStage.Bootstrap))
                throw new ArgumentException("The carried bootstrap decision lacks bootstrap derivation evidence.", nameof(bootstrapDecision));
        }

        private static void RequireAuthorityTenant(AuthorizationConfigurationCommand command, TenantId? declaredTenant, TenantId authorityTenant)
        {
            if (declaredTenant is { } tenant && tenant != authorityTenant)
                throw new ArgumentException("The authorization command tenant does not match the write authority.", nameof(command));
        }

        protected override async ValueTask AuthorizeAsync(CancellationToken ct)
        {
            if (bootstrapDecision is not null)
            {
                RequireBootstrapEvidence(bootstrapDecision);
                return;
            }
            if (packAuthority is not null)
            {
                // The pack installer already decided packages:operate for this exact activated version and
                // minted the authority the projector is running under; re-deciding here would be a SECOND
                // decision over the same act. The remaining five stages are the ordinary ones.
                packAuthority.EnsureUsable();
                return;
            }
            if (additiveSeedRevision)
            {
                // WriteAdditiveSeedRevisionAsync already proved the exact checked-in definition and the
                // server-derived seed principal. The remaining five stages are identical to an ordinary write,
                // including definition admission and the non-bootstrap CommitAsync path.
                return;
            }
            (TenantId? Tenant, string Id) target = command switch
            {
                InstallAuthorizationDefinition install => (install.DeclaringTenantId, install.Definition.DefinitionId.Value.ToString()),
                ReplaceAuthorizationDefinition replace => (replace.DeclaringTenantId, replace.Definition.DefinitionId.Value.ToString()),
                NarrowCapabilityRoleBinding narrow => (narrow.TenantId, narrow.DefinitionId.Value.ToString()),
                _ => throw new InvalidOperationException($"Unsupported authorization command '{command.GetType().Name}'."),
            };
            RequireAuthorityTenant(command, target.Tenant, authority.Tenant);
            var decision = await writer.gate.DecideAsync(
                authority.Request(AuthorizationOperation.Parse(Permission.GrantPermissions), "grant", target.Id), ct)
                .ConfigureAwait(false);
            decision.RequireAllowed();
            Decision = decision;
        }

        protected override async ValueTask<AuthorizationConfigurationState?> BindAsync(CancellationToken ct) => command switch
        {
            InstallAuthorizationDefinition install =>
                await writer.states.ReadStateAsync(install.Definition.DefinitionId, ct: ct).ConfigureAwait(false),
            ReplaceAuthorizationDefinition replace =>
                await writer.states.ReadStateAsync(
                    replace.Definition.DefinitionId,
                    replace.DeclaringTenantId,
                    ct).ConfigureAwait(false),
            NarrowCapabilityRoleBinding narrow =>
                await writer.states.ReadStateAsync(narrow.DefinitionId, narrow.TenantId, ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported authorization command '{command.GetType().Name}'."),
        };

        protected override ValueTask<AuthorizationMutation> MutateAsync(
            AuthorizationConfigurationState bound,
            CancellationToken ct)
        {
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

        protected override async ValueTask<ValidatedAuthorizationConfigurationWrite> ValidateAsync(
            AuthorizationConfigurationState bound,
            AuthorizationMutation mutation,
            CancellationToken ct)
        {
            var at = authority.At;
            var packPublished = packAuthority is not null;
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
                    await writer.definitionAdmission.AdmitAsync(
                        installed,
                        install.DeclaringTenantId,
                        previous: null,
                        packPublished,
                        ct).ConfigureAwait(false);
                    return Sealed = new ValidatedAuthorizationConfigurationWrite(
                        AuthorizationConfigurationWriteKind.InstallDefinition,
                        installed,
                        bindingRevision: null,
                        expectedDefinitionRevision: 0,
                        expectedBindingRevision: 0,
                        definitionEffectiveAt: at,
                        declaringTenantId: install.DeclaringTenantId,
                        Decision, authority.Principal, authority.Tenant);

                case ReplaceAuthorizationDefinition replace:
                    var previous = bound.Definition
                        ?? throw new InvalidOperationException("The authorization definition is not installed.");
                    var replacement = mutation.Definition
                        ?? throw new InvalidOperationException("Mutate produced no definition.");
                    await writer.definitionAdmission.AdmitAsync(
                        replacement,
                        replace.DeclaringTenantId,
                        previous,
                        packPublished,
                        ct).ConfigureAwait(false);
                    return Sealed = new ValidatedAuthorizationConfigurationWrite(
                        AuthorizationConfigurationWriteKind.ReplaceDefinition,
                        replacement,
                        bindingRevision: null,
                        expectedDefinitionRevision: previous.Revision,
                        expectedBindingRevision: 0,
                        definitionEffectiveAt: at,
                        declaringTenantId: replace.DeclaringTenantId,
                        Decision, authority.Principal, authority.Tenant);

                case NarrowCapabilityRoleBinding:
                    var definition = bound.Definition
                        ?? throw new InvalidOperationException("The authorization definition is not installed.");
                    var revision = mutation.BindingRevision
                        ?? throw new InvalidOperationException("Mutate produced no binding revision.");
                    writer.bindingAdmission.Admit(
                        definition.OfferedRoles,
                        bound.EffectiveBinding,
                        revision.SelectedRoles);
                    return Sealed = new ValidatedAuthorizationConfigurationWrite(
                        AuthorizationConfigurationWriteKind.NarrowBinding,
                        definition: null,
                        revision,
                        expectedDefinitionRevision: definition.Revision,
                        expectedBindingRevision: bound.BindingRevision,
                        definitionEffectiveAt: null,
                        declaringTenantId: null,
                        Decision, authority.Principal, authority.Tenant);

                default:
                    throw new InvalidOperationException($"Unsupported authorization command '{command.GetType().Name}'.");
            }
        }

        protected override async ValueTask CommitAsync(ValidatedAuthorizationConfigurationWrite validated, CancellationToken ct)
        {
            if (bootstrapDecision is null)
                await writer.store.CommitAsync(validated, ct).ConfigureAwait(false);
            else
                await writer.store.CommitBootstrapAsync(validated, authority.Tenant, writer.grants, ct).ConfigureAwait(false);
        }

        protected override ValueTask<AuthorizationConfigurationWriteResult> ReactAsync(
            ValidatedAuthorizationConfigurationWrite validated,
            CancellationToken ct)
        {
            // React runs only once every earlier stage has completed, so the completed stages are the whole order.
            if (validated.BindingRevision is not { } revision)
            {
                return ValueTask.FromResult(new AuthorizationConfigurationWriteResult(
                    validated.Definition, null, WritePipeline.Names));
            }

            BindingWarningCode? warning = revision.SelectedRoles.Equals(
                Harborline.Api.Foundation.IdentityAtlas.Permissions.RoleBindingSet.Empty)
                ? BindingWarningCode.EmptyBinding
                : null;
            return ValueTask.FromResult(new AuthorizationConfigurationWriteResult(
                null,
                new BindingChangeResult(revision, revision.SelectedRoles, warning),
                WritePipeline.Names));
        }
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
}
