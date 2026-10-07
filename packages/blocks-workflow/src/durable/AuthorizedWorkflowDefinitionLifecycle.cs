using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Kernel.Runtime;

namespace Harborline.Api.Blocks.Workflow.Durable;

/// <summary>The unavoidable authorize-stage façade for workflow-definition mutations.</summary>
public sealed class AuthorizedWorkflowDefinitionLifecycle : IPackProjectionParticipant
{
    public void StageProjection(PackProjectionTransaction transaction) => transaction.Enlist(inner);
    private readonly IWorkflowDefinitionStore inner;
    private readonly DefinitionWriter writer;
    private readonly AuthorizationGate gate;
    private readonly IRoleGateAdmission roleGateAdmission;

    private sealed class WriterKey;

    internal AuthorizedWorkflowDefinitionLifecycle(
        EntityStoreWorkflowDefinitionStore inner,
        IEntityMutationStore persistenceStore,
        IWorkflowAdmissionValidator persistenceAdmission,
        TimeProvider persistenceTime,
        AuthorizationGate gate,
        IRoleGateAdmission roleGateAdmission,
        IWritePipelineObserver? pipelineObserver = null)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
        this.roleGateAdmission = roleGateAdmission ?? throw new ArgumentNullException(nameof(roleGateAdmission));
        writer = CreateWriter(new EntityWriterBackend(
            persistenceStore ?? throw new ArgumentNullException(nameof(persistenceStore)),
            persistenceAdmission ?? throw new ArgumentNullException(nameof(persistenceAdmission)),
            persistenceTime ?? throw new ArgumentNullException(nameof(persistenceTime)),
            pipelineObserver));
    }

    internal IRoleGateAdmission RoleGateAdmission => roleGateAdmission;
    private static DefinitionWriter CreateWriter(IWriterBackend backend) =>
        (DefinitionWriter)(Activator.CreateInstance(
            typeof(DefinitionWriter),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [new WriterKey(), backend],
            culture: null)
            ?? throw new InvalidOperationException("The private-key workflow writer could not be constructed."));
    public sealed class WriteAuthority
    {
        internal WriteAuthority(
            AuthorizationDecision decision,
            string definitionId,
            DefinitionAuthorityKind authorityKind)
        {
            Decision = decision;
            DefinitionId = definitionId;
            AuthorityKind = authorityKind;
        }
        internal AuthorizationDecision Decision { get; }
        internal string DefinitionId { get; }
        internal DefinitionAuthorityKind AuthorityKind { get; }
    }

    public ValueTask<WorkflowDefinitionRecord> GetAsync(DefinitionCoordinates coordinates, CancellationToken ct = default) =>
        inner.GetAsync(coordinates, ct);

    public ValueTask<WorkflowDefinitionRecord?> GetCurrentPublishedAsync(DefinitionAddress address, CancellationToken ct = default) =>
        inner.GetCurrentPublishedAsync(address, ct);

    public IAsyncEnumerable<WorkflowDefinitionRecord> ListByTenantAsync(TenantId tenant, CancellationToken ct = default) =>
        inner.ListByTenantAsync(tenant, ct);

    public async ValueTask<WorkflowDefinitionRecord> RegisterAsync(
        JsonElement authored,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        var model = WorkflowDefinitionWireMapper.ToModel(authored);
        if (!string.Equals(model.Tenant, authority.Tenant.Value, StringComparison.Ordinal))
            throw new ArgumentException("The workflow definition tenant does not match the write authority.", nameof(model));
        var provenance = DefinitionAuthorityClassifier.Classify(
            DefinitionAuthorityKind.Tenant, authority.Tenant, model.Envelope.CascadeLayer);
        var decided = await DecideAsync(model.Key, authority, ct).ConfigureAwait(false);
        var candidate = StampLayer(model, provenance.Layer);
        return await RegisterCoreAsync(candidate, authored, new WorkflowDefinitionRegistrationOptions(authority.At),
            Admission(decided), Target(model), provenance.Owner, DefinitionAuthorityKind.Tenant, null, ct).ConfigureAwait(false);
    }

    public ValueTask<WorkflowDefinitionRecord> RegisterAsync(
        JsonElement authored, WriteAuthority authority, CancellationToken ct = default)
    {
        var model = WorkflowDefinitionWireMapper.ToModel(authored);
        var provenance = DefinitionAuthorityClassifier.Classify(
            Kind(authority), new TenantId(model.Tenant), model.Envelope.CascadeLayer);
        var candidate = StampLayer(model, provenance.Layer);
        return RegisterCoreAsync(candidate, authored, new WorkflowDefinitionRegistrationOptions(authority.Decision.Request.At),
            Admission(authority), Target(model), provenance.Owner, authority.AuthorityKind, null, ct);
    }

    public ValueTask<WorkflowDefinitionRecord> RegisterAsync(
        JsonElement authored,
        PackProjectionAuthority authority,
        CancellationToken ct = default)
    {
        var model = WorkflowDefinitionWireMapper.ToModel(authored, CascadeLayer.Pack);
        ArgumentNullException.ThrowIfNull(authority);
        var provenance = DefinitionAuthorityClassifier.Classify(
            DefinitionAuthorityKind.VendorPackage,
            authority.Tenant,
            model.Envelope.CascadeLayer,
            authority.PackId);
        var candidate = StampPackSource(StampLayer(model, provenance.Layer), authority);
        return RegisterCoreAsync(candidate, authored, new WorkflowDefinitionRegistrationOptions(authority.ActivationInstant),
            authority.ToWriteAdmission(), PackTarget(model), provenance.Owner,
            DefinitionAuthorityKind.VendorPackage, authority.PackId, ct);
    }

    public async ValueTask<WorkflowDefinitionRecord> RegisterAndPublishAsync(
        JsonElement authored,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        var model = WorkflowDefinitionWireMapper.ToModel(authored);
        if (!string.Equals(model.Tenant, authority.Tenant.Value, StringComparison.Ordinal))
            throw new ArgumentException("The workflow definition tenant does not match the write authority.", nameof(model));
        var provenance = DefinitionAuthorityClassifier.Classify(
            DefinitionAuthorityKind.Tenant, authority.Tenant, model.Envelope.CascadeLayer);
        var decided = await DecideAsync(model.Key, authority, ct).ConfigureAwait(false);
        var candidate = StampLayer(model, provenance.Layer);
        return await RegisterCoreAsync(candidate, authored,
            new WorkflowDefinitionRegistrationOptions(authority.At, WorkflowDefinitionStatus.Published),
            Admission(decided), Target(model), provenance.Owner, DefinitionAuthorityKind.Tenant, null, ct).ConfigureAwait(false);
    }

    public ValueTask<WorkflowDefinitionRecord> RegisterAndPublishAsync(
        JsonElement authored, WriteAuthority authority, CancellationToken ct = default)
    {
        var model = WorkflowDefinitionWireMapper.ToModel(authored);
        var provenance = DefinitionAuthorityClassifier.Classify(
            Kind(authority), new TenantId(model.Tenant), model.Envelope.CascadeLayer);
        var candidate = StampLayer(model, provenance.Layer);
        return RegisterCoreAsync(candidate, authored,
            new WorkflowDefinitionRegistrationOptions(authority.Decision.Request.At, WorkflowDefinitionStatus.Published),
            Admission(authority), Target(model), provenance.Owner, authority.AuthorityKind, null, ct);
    }

    public ValueTask<WorkflowDefinitionRecord> RegisterAndPublishAsync(
        JsonElement authored,
        PackProjectionAuthority authority,
        CancellationToken ct = default)
    {
        var model = WorkflowDefinitionWireMapper.ToModel(authored, CascadeLayer.Pack);
        ArgumentNullException.ThrowIfNull(authority);
        var provenance = DefinitionAuthorityClassifier.Classify(
            DefinitionAuthorityKind.VendorPackage,
            authority.Tenant,
            model.Envelope.CascadeLayer,
            authority.PackId);
        var candidate = StampPackSource(StampLayer(model, provenance.Layer), authority);
        return RegisterCoreAsync(candidate, authored,
            new WorkflowDefinitionRegistrationOptions(authority.ActivationInstant, WorkflowDefinitionStatus.Published),
            authority.ToWriteAdmission(), PackTarget(model), provenance.Owner,
            DefinitionAuthorityKind.VendorPackage, authority.PackId, ct);
    }

    public async ValueTask<WorkflowDefinitionRecord> PublishAsync(
        DefinitionCoordinates coordinates,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        RequireTenant(coordinates, authority);
        var decided = await DecideAsync(coordinates.Address.Identity.Value, authority, ct).ConfigureAwait(false);
        return await TransitionCoreAsync(coordinates, DefinitionLifecycleTransition.Publish, null,
            Admission(decided), Target(coordinates), async (persisted, token) =>
            {
                var model = PersistedModel(persisted);
                var provenance = DefinitionAuthorityClassifier.Classify(
                    DefinitionAuthorityKind.Tenant, authority.Tenant, persisted.Envelope.CascadeLayer);
                await AdmitAsync(model, provenance.Owner, token).ConfigureAwait(false);
                await ValidateSupersessionAsync(model, DefinitionAuthorityKind.Tenant, null, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
    }

    public ValueTask<WorkflowDefinitionRecord> PublishAsync(
        DefinitionCoordinates coordinates, WriteAuthority authority, CancellationToken ct = default) =>
        TransitionCoreAsync(coordinates, DefinitionLifecycleTransition.Publish, null,
            Admission(authority), Target(coordinates), async (persisted, token) =>
            {
                var model = PersistedModel(persisted);
                var provenance = DefinitionAuthorityClassifier.Classify(
                    authority.AuthorityKind, coordinates.Address.Tenant, persisted.Envelope.CascadeLayer);
                await AdmitAsync(model, provenance.Owner, token).ConfigureAwait(false);
                await ValidateSupersessionAsync(model, authority.AuthorityKind, null, token).ConfigureAwait(false);
            }, ct);

    public ValueTask<WorkflowDefinitionRecord> PublishAsync(
        JsonElement authored,
        PackProjectionAuthority authority,
        CancellationToken ct = default)
    {
        var model = StampPackSource(
            WorkflowDefinitionWireMapper.ToModel(authored, CascadeLayer.Pack), authority);
        return TransitionCoreAsync(Coordinates(model), DefinitionLifecycleTransition.Publish, authority.ActivationInstant,
            authority.ToWriteAdmission(), PackTarget(model), async (persisted, token) =>
            {
                RequirePersistedPackSource(persisted, authority);
                var candidate = PersistedModel(persisted);
                RequirePackWireMatchesPersisted(authored, persisted.Authored);
                var provenance = DefinitionAuthorityClassifier.Classify(
                    DefinitionAuthorityKind.VendorPackage, authority.Tenant,
                    persisted.Envelope.CascadeLayer, authority.PackId);
                await AdmitPersistedAsync(persisted, provenance.Owner, token).ConfigureAwait(false);
                await ValidateSupersessionAsync(candidate, DefinitionAuthorityKind.VendorPackage, authority.PackId, token).ConfigureAwait(false);
            }, ct);
    }

    public async ValueTask<WorkflowDefinitionRecord> WithdrawAsync(
        DefinitionCoordinates coordinates,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        RequireTenant(coordinates, authority);
        var decided = await DecideAsync(coordinates.Address.Identity.Value, authority, ct).ConfigureAwait(false);
        return await TransitionCoreAsync(coordinates, DefinitionLifecycleTransition.Withdraw, null,
            Admission(decided), Target(coordinates), async (persisted, token) =>
            {
                var provenance = DefinitionAuthorityClassifier.Classify(
                    DefinitionAuthorityKind.Tenant, authority.Tenant, persisted.Envelope.CascadeLayer);
                await AdmitAsync(PersistedModel(persisted), provenance.Owner, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
    }

    public ValueTask<WorkflowDefinitionRecord> WithdrawAsync(
        JsonElement authored,
        PackProjectionAuthority authority,
        CancellationToken ct = default)
    {
        var model = StampPackSource(
            WorkflowDefinitionWireMapper.ToModel(authored, CascadeLayer.Pack), authority);
        return TransitionCoreAsync(Coordinates(model), DefinitionLifecycleTransition.Withdraw, authority.ActivationInstant,
            authority.ToWriteAdmission(), PackTarget(model), async (persisted, token) =>
            {
                RequirePersistedPackSource(persisted, authority);
                RequirePackWireMatchesPersisted(authored, persisted.Authored);
                var provenance = DefinitionAuthorityClassifier.Classify(
                    DefinitionAuthorityKind.VendorPackage, authority.Tenant,
                    persisted.Envelope.CascadeLayer, authority.PackId);
                await AdmitPersistedAsync(persisted, provenance.Owner, token).ConfigureAwait(false);
            }, ct);
    }

    public ValueTask<WorkflowDefinitionRecord> RestorePackProjectionAsync(
        JsonElement authored,
        PackProjectionAuthority authority,
        CancellationToken ct = default)
    {
        var model = StampPackSource(
            WorkflowDefinitionWireMapper.ToModel(authored, CascadeLayer.Pack), authority);
        return TransitionCoreAsync(Coordinates(model), DefinitionLifecycleTransition.Restore, authority.ActivationInstant,
            authority.ToWriteAdmission(), PackTarget(model), async (persisted, token) =>
            {
                RequirePersistedPackSource(persisted, authority);
                RequirePackWireMatchesPersisted(authored, persisted.Authored);
                var provenance = DefinitionAuthorityClassifier.Classify(
                    DefinitionAuthorityKind.VendorPackage, authority.Tenant,
                    persisted.Envelope.CascadeLayer, authority.PackId);
                await AdmitPersistedAsync(persisted, provenance.Owner, token).ConfigureAwait(false);
            }, ct);
    }

    public async ValueTask<WriteAuthority> DecideAsync(
        string id,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("A workflow definition id is required.", nameof(id));
        var decision = await gate.DecideAsync(
            authority.Request(AuthorizationOperation.Parse(Permission.SchedulingAuthor), "scheduling", AuthorizationTargetId(id)), ct)
            .ConfigureAwait(false);
        decision.RequireAllowed();
        return new WriteAuthority(decision, id, DefinitionAuthorityKind.Tenant);
    }

    internal ValueTask<WriteAuthority> DecidePlatformSeedAsync(
        string id,
        PlatformBootstrapDecision bootstrap,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        var decision = bootstrap.Decision;
        if (decision.Request.Act.Operation.Value != Permission.SchedulingAuthor
            || decision.Request.Target.RecordKind != "scheduling"
            || decision.Request.Target.RecordId != AuthorizationTargetId(id))
            throw new AuthorizationDeniedException(decision);
        return ValueTask.FromResult(new WriteAuthority(decision, id, DefinitionAuthorityKind.PlatformBootstrap));
    }

    private static WorkflowDefinition StampPackSource(
        WorkflowDefinition model,
        PackProjectionAuthority authority) => new()
        {
            Envelope = model.Envelope,
            Status = model.Status,
            SubjectFormRef = model.SubjectFormRef,
            Mutability = model.Mutability,
            InitialState = model.InitialState,
            States = model.States,
            Transitions = model.Transitions,
            Triggers = model.Triggers,
            Actions = model.Actions,
            GuardRuleIds = model.GuardRuleIds,
            PackSource = new PackProjectionSource(authority.PackId, authority.PackVersion),
        };

    private static WorkflowDefinition CopySource(WorkflowDefinition model, PackProjectionSource? source) => new()
    {
        Envelope = model.Envelope,
        Status = model.Status,
        SubjectFormRef = model.SubjectFormRef,
        Mutability = model.Mutability,
        InitialState = model.InitialState,
        States = model.States,
        Transitions = model.Transitions,
        Triggers = model.Triggers,
        Actions = model.Actions,
        GuardRuleIds = model.GuardRuleIds,
        PackSource = source,
    };

    private static void RequirePersistedPackSource(
        WorkflowDefinitionRecord persisted,
        PackProjectionAuthority authority)
    {
        if (!string.Equals(persisted.Tenant, authority.Tenant.Value, StringComparison.Ordinal))
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.TenantMismatch);
        if (persisted.PackSource is not { } source
            || !string.Equals(source.PackId, authority.PackId, StringComparison.Ordinal))
        {
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.SourceMismatch);
        }
    }

    private static WorkflowDefinition PersistedModel(WorkflowDefinitionRecord persisted) =>
        CopySource(
            WorkflowDefinitionWireMapper.ToModel(
                persisted.Authored,
                persisted.Tenant,
                persisted.Key,
                persisted.Version,
                persisted.Envelope.CascadeLayer),
            persisted.PackSource);

    private static void RequirePackWireMatchesPersisted(
        JsonElement supplied,
        JsonElement persisted)
    {
        if (!JsonNode.DeepEquals(
                WorkflowDefinitionWireMapper.CanonicalizeAuthoredWire(supplied),
                WorkflowDefinitionWireMapper.CanonicalizeAuthoredWire(persisted)))
        {
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.SourceMismatch);
        }
    }

    private static DefinitionCoordinates Coordinates(WorkflowDefinition model) =>
        new(new TenantId(model.Tenant), model.Key, model.Version);

    private ValueTask AdmitAsync(
        WorkflowDefinition model,
        RoleGatedDefinitionOwner owner,
        CancellationToken ct) => roleGateAdmission.AdmitAsync(ToRoleGated(model, owner), ct);

    private ValueTask AdmitPersistedAsync(
        WorkflowDefinitionRecord persisted,
        RoleGatedDefinitionOwner owner,
        CancellationToken ct) => roleGateAdmission.AdmitAsync(ToRoleGated(persisted, owner), ct);

    private ValueTask AdmitStoredAsync(WorkflowDefinition model, CancellationToken ct) =>
        roleGateAdmission.AdmitAsync(ToRoleGated(model), ct);

    public static RoleGatedDefinition ToRoleGated(WorkflowDefinitionRecord record)
    {
        var model = WorkflowDefinitionWireMapper.ToModel(
            record.Authored,
            record.Tenant,
            record.Key,
            record.Version,
            record.Envelope.CascadeLayer);
        return ToRoleGated(CopySource(model, record.PackSource));
    }

    private static RoleGatedDefinition ToRoleGated(
        WorkflowDefinitionRecord record,
        RoleGatedDefinitionOwner owner)
    {
        var model = WorkflowDefinitionWireMapper.ToModel(
            record.Authored,
            record.Tenant,
            record.Key,
            record.Version,
            record.Envelope.CascadeLayer);
        return ToRoleGated(CopySource(model, record.PackSource), owner);
    }

    internal static RoleGatedDefinition ToRoleGated(WorkflowDefinition model)
    {
        var owner = DefinitionAuthorityClassifier.FromStored(
            new TenantId(model.Tenant),
            model.Envelope.CascadeLayer,
            model.PackSource?.PackId).Owner;
        return ToRoleGated(model, owner);
    }

    private static RoleGatedDefinition ToRoleGated(
        WorkflowDefinition model,
        RoleGatedDefinitionOwner owner)
    {
        var gates = new List<DeclarativeGateReference>();
        foreach (var transition in model.Transitions)
        {
            AddGate(gates, $"transition:{transition.Id}", transition.RequiredRoles, transition.RequiredStandings);
        }
        foreach (var action in model.Actions)
        {
            AddGate(gates, $"action:{action.Id}", action.RequiredRoles, action.RequiredStandings);
        }
        return new RoleGatedDefinition(
            "workflow", model.Key, model.Version, owner, gates);
    }

    private static void AddGate(
        List<DeclarativeGateReference> gates,
        string gate,
        IEnumerable<string> roles,
        IEnumerable<RecordStandingReference> standings)
    {
        gates.AddRange(roles.Select(role => DeclarativeGateReference.ForRole(gate, role)));
        gates.AddRange(standings.Select(standing => DeclarativeGateReference.ForStanding(gate, standing)));
    }

    private static WorkflowDefinition StampLayer(WorkflowDefinition model, CascadeLayer layer) => new()
    {
        Envelope = model.Envelope with { CascadeLayer = layer },
        Status = model.Status,
        SubjectFormRef = model.SubjectFormRef,
        Mutability = model.Mutability,
        InitialState = model.InitialState,
        States = model.States,
        Transitions = model.Transitions,
        Triggers = model.Triggers,
        Actions = model.Actions,
        GuardRuleIds = model.GuardRuleIds,
        PackSource = model.PackSource,
    };

    private async ValueTask ValidateSupersessionAsync(
        WorkflowDefinition candidate,
        DefinitionAuthorityKind authorityKind,
        string? authorityPackageId,
        CancellationToken ct)
    {
        var current = await inner.GetCurrentPublishedAsync(
            new DefinitionAddress(new TenantId(candidate.Tenant), candidate.Key), ct).ConfigureAwait(false);
        if (current is null
            || DefinitionLifecycleVersion.Parse(candidate.Version)
                .CompareTo(DefinitionLifecycleVersion.Parse(current.Version)) <= 0)
        {
            return;
        }

        if (authorityKind == DefinitionAuthorityKind.VendorPackage
            && !string.Equals(current.PackSource?.PackId, authorityPackageId, StringComparison.Ordinal))
        {
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.SourceMismatch);
        }
        _ = DefinitionAuthorityClassifier.Classify(
            authorityKind, new TenantId(candidate.Tenant), current.Envelope.CascadeLayer, authorityPackageId);
    }

    internal static string AuthorizationTargetId(string definitionId) => Uri.EscapeDataString(definitionId);

    private static void RequireTenant(DefinitionCoordinates coordinates, AuthorizationWriteContext authority)
    {
        if (coordinates.Address.Tenant != authority.Tenant)
            throw new ArgumentException("The workflow coordinates tenant does not match the write authority.", nameof(coordinates));
    }

    /// <summary>
    /// One register act. The writer runs it through <c>WritePipeline.RunAsync</c>: authorize checks the carried
    /// admission against this definition, bind reads the store, mutate settles the stored status and instant,
    /// validate runs this façade's role-gate admission and supersession and then the workflow admission and
    /// conflict checks, commit creates, react returns.
    /// </summary>
    private ValueTask<WorkflowDefinitionRecord> RegisterCoreAsync(
        WorkflowDefinition candidate,
        JsonElement authored,
        WorkflowDefinitionRegistrationOptions options,
        DefinitionWriteAdmission admission,
        DefinitionWriteTarget target,
        RoleGatedDefinitionOwner owner,
        DefinitionAuthorityKind authorityKind,
        string? authorityPackageId,
        CancellationToken ct) =>
        writer.RegisterAsync(admission, target, candidate, authored, options,
            async (model, token) =>
            {
                await AdmitAsync(model, owner, token).ConfigureAwait(false);
                await ValidateSupersessionAsync(model, authorityKind, authorityPackageId, token).ConfigureAwait(false);
            },
            ct);

    /// <summary>One lifecycle transition act; <paramref name="validate"/> runs at validate on the bound record.</summary>
    private ValueTask<WorkflowDefinitionRecord> TransitionCoreAsync(
        DefinitionCoordinates coordinates,
        DefinitionLifecycleTransition transition,
        DateTimeOffset? at,
        DefinitionWriteAdmission admission,
        DefinitionWriteTarget target,
        Func<WorkflowDefinitionRecord, CancellationToken, ValueTask> validate,
        CancellationToken ct) =>
        writer.TransitionAsync(admission, target, coordinates, transition, at, validate, ct);

    private static DefinitionWriteAdmission Admission(WriteAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        return DefinitionWriteAdmission.Decide(authority.Decision);
    }

    private static DefinitionAuthorityKind Kind(WriteAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        return authority.AuthorityKind;
    }

    private static DefinitionWriteTarget Target(WorkflowDefinition model) =>
        new(new TenantId(model.Tenant), "scheduling", Permission.SchedulingAuthor, model.Key);

    private static DefinitionWriteTarget Target(DefinitionCoordinates coordinates) =>
        new(coordinates.Address.Tenant, "scheduling", Permission.SchedulingAuthor, coordinates.Address.Identity.Value);

    /// <summary>A pack write's target: the caller's declared source.</summary>
    private static DefinitionWriteTarget PackTarget(WorkflowDefinition model) =>
        Target(model) with { DeclaredPackSource = model.PackSource };

    private interface IWriterBackend
    {
        ValueTask<WorkflowDefinitionRecord> RegisterAsync(
            DefinitionWriteAdmission admission, DefinitionWriteTarget target,
            WorkflowDefinition model, JsonElement authored, WorkflowDefinitionRegistrationOptions? options,
            Func<WorkflowDefinition, CancellationToken, ValueTask> validate, CancellationToken ct);
        ValueTask<WorkflowDefinitionRecord> TransitionAsync(
            DefinitionWriteAdmission admission, DefinitionWriteTarget target, DefinitionCoordinates coordinates,
            DefinitionLifecycleTransition transition, DateTimeOffset? at,
            Func<WorkflowDefinitionRecord, CancellationToken, ValueTask> validate, CancellationToken ct);
    }

    /// <summary>The non-resolvable workflow writer. Its constructor requires the lifecycle's private key.</summary>
    internal sealed class DefinitionWriter
    {
        private readonly IWriterBackend backend;

        private DefinitionWriter(WriterKey key, IWriterBackend backend)
        {
            ArgumentNullException.ThrowIfNull(key);
            this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        internal ValueTask<WorkflowDefinitionRecord> RegisterAsync(
            DefinitionWriteAdmission admission, DefinitionWriteTarget target,
            WorkflowDefinition model, JsonElement authored, WorkflowDefinitionRegistrationOptions? options,
            Func<WorkflowDefinition, CancellationToken, ValueTask> validate, CancellationToken ct) =>
            backend.RegisterAsync(admission, target, model, authored, options, validate, ct);

        internal ValueTask<WorkflowDefinitionRecord> TransitionAsync(
            DefinitionWriteAdmission admission, DefinitionWriteTarget target, DefinitionCoordinates c,
            DefinitionLifecycleTransition transition, DateTimeOffset? at,
            Func<WorkflowDefinitionRecord, CancellationToken, ValueTask> validate, CancellationToken ct) =>
            backend.TransitionAsync(admission, target, c, transition, at, validate, ct);
    }

    private sealed class EntityWriterBackend
        : EntityStoreDefinitionLifecycle<WorkflowDefinitionRecord>, IWriterBackend
    {
        private const string EnvelopeKind = "workflow-definition";
        private const string EntityScheme = "workflowdef";
        private const string EntityAuthority = "workflows";
        private readonly IWorkflowAdmissionValidator admission;
        private readonly TimeProvider time;
        private readonly IWritePipelineObserver? observer;

        internal EntityWriterBackend(
            IEntityMutationStore store, IWorkflowAdmissionValidator admission, TimeProvider time,
            IWritePipelineObserver? observer)
            : base(store, store, time, EntityStoreWorkflowDefinitionStore.DefinitionSchema,
                EnvelopeKind, "key", EntityScheme, EntityAuthority)
        {
            this.admission = admission;
            this.time = time;
            this.observer = observer;
        }

        public async ValueTask<WorkflowDefinitionRecord> RegisterAsync(
            DefinitionWriteAdmission writeAdmission, DefinitionWriteTarget target,
            WorkflowDefinition model, JsonElement authored, WorkflowDefinitionRegistrationOptions? options,
            Func<WorkflowDefinition, CancellationToken, ValueTask> validate, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(writeAdmission);
            ArgumentNullException.ThrowIfNull(model);
            return (await WritePipeline.RunAsync(
                new Register(this, writeAdmission, target, model, authored, options, validate), observer, ct)
                .ConfigureAwait(false))!;
        }

        public async ValueTask<WorkflowDefinitionRecord> TransitionAsync(
            DefinitionWriteAdmission writeAdmission, DefinitionWriteTarget target, DefinitionCoordinates coordinates,
            DefinitionLifecycleTransition transition, DateTimeOffset? at,
            Func<WorkflowDefinitionRecord, CancellationToken, ValueTask> validate, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(writeAdmission);
            return (await WritePipeline.RunAsync(
                new Transition(this, writeAdmission, target, coordinates, transition, at, validate), observer, ct)
                .ConfigureAwait(false))!;
        }

        protected override DefinitionCoordinates CoordinatesOf(WorkflowDefinitionRecord d) =>
            new(d.Envelope.Tenant, d.Key, d.Version);
        protected override DefinitionLifecycleStatus StatusOf(WorkflowDefinitionRecord d) => (DefinitionLifecycleStatus)d.Status;
        protected override WorkflowDefinitionRecord WithStatus(
            WorkflowDefinitionRecord d, DefinitionLifecycleStatus status, DateTimeOffset at) =>
            d with { Status = (WorkflowDefinitionStatus)status, UpdatedAt = at };
        protected override JsonDocument Serialize(WorkflowDefinitionRecord d) =>
            EntityStoreWorkflowDefinitionStore.SerializeEnvelope(
                d.Envelope, d.Status, d.Authored, d.PackSource, d.UpdatedAt);
        protected override WorkflowDefinitionRecord Deserialize(JsonDocument body) =>
            EntityStoreWorkflowDefinitionStore.ToRecord(body);
        protected override ActorId TransitionActor(WorkflowDefinitionRecord d) =>
            EntityStoreWorkflowDefinitionStore.DefinitionAuthor;
        protected override Exception CreateNotFoundException(DefinitionCoordinates c) =>
            new WorkflowDefinitionNotFoundException(
                c.Address.Identity.Value, c.Version.ToString(), c.Address.Tenant.Value);
        protected override Exception CreateInvalidTransitionException(
            WorkflowDefinitionRecord d, DefinitionLifecycleStatus target,
            IReadOnlyCollection<DefinitionLifecycleStatus> allowed) =>
            new InvalidOperationException(
                $"WorkflowDefinition '{d.Key}' v{d.Version} cannot transition from {d.Status} to {target}.");
        protected override ValueTask ValidatePackRestoreAsync(
            WorkflowDefinitionRecord d, CancellationToken ct)
        {
            if (!EntityStoreWorkflowDefinitionStore.IsSystemPackProjection(d.Authored))
                throw new InvalidOperationException(
                    $"Only a System-owned, Pack-provenance workflow projection can be restored; '{d.Key}' v{d.Version} is not one.");
            if (d.Status is not (WorkflowDefinitionStatus.Withdrawn or WorkflowDefinitionStatus.Published))
                throw new InvalidOperationException(
                    $"WorkflowDefinition '{d.Key}' v{d.Version} cannot be restored from {d.Status}.");
            _ = new WorkflowDefinitionLoadValidator(admission).ReadAdmissibleOrThrow(
                d.Authored, d.Tenant, d.Key, d.Version);
            return ValueTask.CompletedTask;
        }

        /// <summary>What register binds: whether the revision is already stored.</summary>
        private sealed record RegisterBound(bool Stored);

        /// <summary>The record as it will be stored: its status and its admitted instant.</summary>
        private sealed record RegisterSealed(WorkflowDefinitionStatus Status, DateTimeOffset EffectiveAt);

        /// <summary>ck-10 S3b: one workflow-definition registration as its six ADR-0038 stages.</summary>
        private sealed class Register(
            EntityWriterBackend backend,
            DefinitionWriteAdmission writeAdmission,
            DefinitionWriteTarget target,
            WorkflowDefinition model,
            JsonElement authored,
            WorkflowDefinitionRegistrationOptions? options,
            Func<WorkflowDefinition, CancellationToken, ValueTask> validate)
            : KernelWrite<RegisterBound, RegisterSealed, RegisterSealed, WorkflowDefinitionRecord>
        {
            private DefinitionCoordinates Coordinates => new(model.Envelope.Tenant, model.Key, model.Version);

            protected override ValueTask AuthorizeAsync(CancellationToken ct)
            {
                writeAdmission.Authorize(target);
                return ValueTask.CompletedTask;
            }

            protected override async ValueTask<RegisterBound?> BindAsync(CancellationToken ct) =>
                new(await backend.Store.GetAsync(
                    backend.EntityIdFor(Coordinates), VersionSelector.Latest, ct).ConfigureAwait(false) is not null);

            protected override ValueTask<RegisterSealed> MutateAsync(RegisterBound bound, CancellationToken ct) =>
                ValueTask.FromResult(new RegisterSealed(
                    options?.Status ?? model.Status, options?.EffectiveAt ?? backend.time.GetUtcNow()));

            protected override async ValueTask<RegisterSealed> ValidateAsync(
                RegisterBound bound, RegisterSealed mutation, CancellationToken ct)
            {
                await validate(model, ct).ConfigureAwait(false);
                backend.admission.EnsureAdmissible(model);
                if (bound.Stored)
                    throw new WorkflowDefinitionConflictException(model.Key, model.Version, model.Tenant);
                return mutation;
            }

            protected override async ValueTask CommitAsync(RegisterSealed validated, CancellationToken ct)
            {
                var coordinates = Coordinates;
                var entityId = backend.EntityIdFor(coordinates);
                using var body = EntityStoreWorkflowDefinitionStore.SerializeEnvelope(
                    model.Envelope, validated.Status, authored, model.PackSource, validated.EffectiveAt);
                var create = new CreateOptions(
                    EntityScheme, EntityAuthority, NonceFor(coordinates),
                    EntityStoreWorkflowDefinitionStore.DefinitionAuthor, new TenantId(model.Tenant),
                    validated.EffectiveAt, ExplicitLocalPart: entityId.LocalPart);
                try
                {
                    await backend.Mutations.CreateAsync(
                        EntityStoreWorkflowDefinitionStore.DefinitionSchema, body, create, ct).ConfigureAwait(false);
                }
                catch (IdempotencyConflictException)
                {
                    throw new WorkflowDefinitionConflictException(model.Key, model.Version, model.Tenant);
                }
            }

            protected override ValueTask<WorkflowDefinitionRecord> ReactAsync(RegisterSealed validated, CancellationToken ct) =>
                ValueTask.FromResult(new WorkflowDefinitionRecord(model.Envelope, validated.Status, authored.Clone())
                {
                    PackSource = model.PackSource,
                    UpdatedAt = validated.EffectiveAt,
                });
        }

        /// <summary>The sealed transition: the record as it will be stored, and whether its status changes.</summary>
        private sealed record TransitionSealed(WorkflowDefinitionRecord Revision, bool Changed);

        /// <summary>ck-10 S3b: one workflow-definition lifecycle transition as its six ADR-0038 stages.</summary>
        private sealed class Transition(
            EntityWriterBackend backend,
            DefinitionWriteAdmission writeAdmission,
            DefinitionWriteTarget target,
            DefinitionCoordinates coordinates,
            DefinitionLifecycleTransition transition,
            DateTimeOffset? at,
            Func<WorkflowDefinitionRecord, CancellationToken, ValueTask> validate)
            : KernelWrite<WorkflowDefinitionRecord, WorkflowDefinitionRecord, TransitionSealed, WorkflowDefinitionRecord>
        {
            protected override ValueTask AuthorizeAsync(CancellationToken ct)
            {
                writeAdmission.Authorize(target);
                return ValueTask.CompletedTask;
            }

            protected override async ValueTask<WorkflowDefinitionRecord?> BindAsync(CancellationToken ct) =>
                await backend.GetAsync(coordinates, ct).ConfigureAwait(false);

            protected override ValueTask<WorkflowDefinitionRecord> MutateAsync(
                WorkflowDefinitionRecord existing, CancellationToken ct) =>
                ValueTask.FromResult(backend.WithStatus(existing, RuleFor(transition).Target, at ?? backend.Now()));

            protected override async ValueTask<TransitionSealed> ValidateAsync(
                WorkflowDefinitionRecord existing, WorkflowDefinitionRecord transitioned, CancellationToken ct)
            {
                await validate(existing, ct).ConfigureAwait(false);
                if (transition == DefinitionLifecycleTransition.Restore)
                    await backend.ValidatePackRestoreAsync(existing, ct).ConfigureAwait(false);
                var (status, allowedFrom) = RuleFor(transition);
                return backend.RequireAllowedTransition(existing, status, allowedFrom)
                    ? new TransitionSealed(transitioned, Changed: true)
                    : new TransitionSealed(existing, Changed: false);
            }

            protected override async ValueTask CommitAsync(TransitionSealed validated, CancellationToken ct)
            {
                if (!validated.Changed) return;
                using var body = backend.Serialize(validated.Revision);
                await backend.Mutations.UpdateAsync(
                    backend.EntityIdFor(coordinates),
                    body,
                    new UpdateOptions(backend.TransitionActor(validated.Revision)),
                    ct).ConfigureAwait(false);
            }

            protected override ValueTask<WorkflowDefinitionRecord> ReactAsync(TransitionSealed validated, CancellationToken ct) =>
                ValueTask.FromResult(validated.Revision);
        }
    }

}
