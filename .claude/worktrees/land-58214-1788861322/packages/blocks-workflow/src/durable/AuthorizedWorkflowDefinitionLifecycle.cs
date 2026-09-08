using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Install;

namespace Harborline.Api.Blocks.Workflow.Durable;

/// <summary>The unavoidable authorize-stage façade for workflow-definition mutations.</summary>
public sealed class AuthorizedWorkflowDefinitionLifecycle
{
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
        IRoleGateAdmission roleGateAdmission)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
        this.roleGateAdmission = roleGateAdmission ?? throw new ArgumentNullException(nameof(roleGateAdmission));
        writer = CreateWriter(new EntityWriterBackend(
            persistenceStore ?? throw new ArgumentNullException(nameof(persistenceStore)),
            persistenceAdmission ?? throw new ArgumentNullException(nameof(persistenceAdmission)),
            persistenceTime ?? throw new ArgumentNullException(nameof(persistenceTime))));
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
        await DecideAsync(model.Key, authority, ct).ConfigureAwait(false);
        var candidate = StampLayer(model, provenance.Layer);
        await AdmitAsync(candidate, provenance.Owner, ct).ConfigureAwait(false);
        await ValidateSupersessionAsync(candidate, DefinitionAuthorityKind.Tenant, null, ct).ConfigureAwait(false);
        var stored = await writer.RegisterAsync(
            candidate, authored, new WorkflowDefinitionRegistrationOptions(authority.At), ct).ConfigureAwait(false);
        return stored;
    }

    public async ValueTask<WorkflowDefinitionRecord> RegisterAsync(
        JsonElement authored, WriteAuthority authority, CancellationToken ct = default)
    {
        var model = WorkflowDefinitionWireMapper.ToModel(authored);
        RequireWriteAuthority(new TenantId(model.Tenant), model.Key, authority);
        var provenance = DefinitionAuthorityClassifier.Classify(
            authority.AuthorityKind, new TenantId(model.Tenant), model.Envelope.CascadeLayer);
        var candidate = StampLayer(model, provenance.Layer);
        await AdmitAsync(candidate, provenance.Owner, ct).ConfigureAwait(false);
        await ValidateSupersessionAsync(candidate, authority.AuthorityKind, null, ct).ConfigureAwait(false);
        var stored = await writer.RegisterAsync(
            candidate, authored, new WorkflowDefinitionRegistrationOptions(authority.Decision.Request.At), ct)
            .ConfigureAwait(false);
        return stored;
    }

    public async ValueTask<WorkflowDefinitionRecord> RegisterAsync(
        JsonElement authored,
        PackProjectionAuthority authority,
        CancellationToken ct = default)
    {
        var model = WorkflowDefinitionWireMapper.ToModel(authored, CascadeLayer.Pack);
        RequirePackAuthority(model, authority);
        var provenance = DefinitionAuthorityClassifier.Classify(
            DefinitionAuthorityKind.VendorPackage,
            authority.Tenant,
            model.Envelope.CascadeLayer,
            authority.PackId);
        var candidate = StampPackSource(StampLayer(model, provenance.Layer), authority);
        await AdmitAsync(candidate, provenance.Owner, ct).ConfigureAwait(false);
        await ValidateSupersessionAsync(candidate, DefinitionAuthorityKind.VendorPackage, authority.PackId, ct).ConfigureAwait(false);
        var stored = await writer.RegisterAsync(
            candidate, authored,
            new WorkflowDefinitionRegistrationOptions(authority.ActivationInstant), ct)
            .ConfigureAwait(false);
        return stored;
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
        await DecideAsync(model.Key, authority, ct).ConfigureAwait(false);
        var candidate = StampLayer(model, provenance.Layer);
        await AdmitAsync(candidate, provenance.Owner, ct).ConfigureAwait(false);
        await ValidateSupersessionAsync(candidate, DefinitionAuthorityKind.Tenant, null, ct).ConfigureAwait(false);
        var stored = await writer.RegisterAsync(
            candidate,
            authored,
            new WorkflowDefinitionRegistrationOptions(authority.At, WorkflowDefinitionStatus.Published),
            ct)
            .ConfigureAwait(false);
        return stored;
    }

    public async ValueTask<WorkflowDefinitionRecord> RegisterAndPublishAsync(
        JsonElement authored, WriteAuthority authority, CancellationToken ct = default)
    {
        var model = WorkflowDefinitionWireMapper.ToModel(authored);
        RequireWriteAuthority(new TenantId(model.Tenant), model.Key, authority);
        var provenance = DefinitionAuthorityClassifier.Classify(
            authority.AuthorityKind, new TenantId(model.Tenant), model.Envelope.CascadeLayer);
        var candidate = StampLayer(model, provenance.Layer);
        await AdmitAsync(candidate, provenance.Owner, ct).ConfigureAwait(false);
        await ValidateSupersessionAsync(candidate, authority.AuthorityKind, null, ct).ConfigureAwait(false);
        var stored = await writer.RegisterAsync(
            candidate,
            authored,
            new WorkflowDefinitionRegistrationOptions(
                authority.Decision.Request.At,
                WorkflowDefinitionStatus.Published),
            ct)
            .ConfigureAwait(false);
        return stored;
    }

    public async ValueTask<WorkflowDefinitionRecord> RegisterAndPublishAsync(
        JsonElement authored,
        PackProjectionAuthority authority,
        CancellationToken ct = default)
    {
        var model = WorkflowDefinitionWireMapper.ToModel(authored, CascadeLayer.Pack);
        RequirePackAuthority(model, authority);
        var provenance = DefinitionAuthorityClassifier.Classify(
            DefinitionAuthorityKind.VendorPackage,
            authority.Tenant,
            model.Envelope.CascadeLayer,
            authority.PackId);
        var candidate = StampPackSource(StampLayer(model, provenance.Layer), authority);
        await AdmitAsync(candidate, provenance.Owner, ct).ConfigureAwait(false);
        await ValidateSupersessionAsync(candidate, DefinitionAuthorityKind.VendorPackage, authority.PackId, ct).ConfigureAwait(false);
        var stored = await writer.RegisterAsync(
            candidate,
            authored,
            new WorkflowDefinitionRegistrationOptions(
                authority.ActivationInstant,
                WorkflowDefinitionStatus.Published),
            ct)
            .ConfigureAwait(false);
        return stored;
    }

    public async ValueTask<WorkflowDefinitionRecord> PublishAsync(
        DefinitionCoordinates coordinates,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        RequireTenant(coordinates, authority);
        await DecideAsync(coordinates.Address.Identity.Value, authority, ct).ConfigureAwait(false);
        var persisted = await inner.GetAsync(coordinates, ct).ConfigureAwait(false);
        var model = WorkflowDefinitionWireMapper.ToModel(
            persisted.Authored, persisted.Tenant, persisted.Key, persisted.Version, persisted.Envelope.CascadeLayer);
        model = CopySource(model, persisted.PackSource);
        var provenance = DefinitionAuthorityClassifier.Classify(
            DefinitionAuthorityKind.Tenant, authority.Tenant, persisted.Envelope.CascadeLayer);
        await AdmitAsync(model, provenance.Owner, ct).ConfigureAwait(false);
        await ValidateSupersessionAsync(model, DefinitionAuthorityKind.Tenant, null, ct).ConfigureAwait(false);
        var stored = await writer.PublishAsync(coordinates, ct).ConfigureAwait(false);
        return stored;
    }

    public async ValueTask<WorkflowDefinitionRecord> PublishAsync(
        DefinitionCoordinates coordinates, WriteAuthority authority, CancellationToken ct = default)
    {
        RequireWriteAuthority(coordinates.Address.Tenant, coordinates.Address.Identity.Value, authority);
        var persisted = await inner.GetAsync(coordinates, ct).ConfigureAwait(false);
        var model = WorkflowDefinitionWireMapper.ToModel(
            persisted.Authored, persisted.Tenant, persisted.Key, persisted.Version, persisted.Envelope.CascadeLayer);
        model = CopySource(model, persisted.PackSource);
        var provenance = DefinitionAuthorityClassifier.Classify(
            authority.AuthorityKind, coordinates.Address.Tenant, persisted.Envelope.CascadeLayer);
        await AdmitAsync(model, provenance.Owner, ct).ConfigureAwait(false);
        await ValidateSupersessionAsync(model, authority.AuthorityKind, null, ct).ConfigureAwait(false);
        var stored = await writer.PublishAsync(coordinates, ct).ConfigureAwait(false);
        return stored;
    }

    public async ValueTask<WorkflowDefinitionRecord> PublishAsync(
        JsonElement authored,
        PackProjectionAuthority authority,
        CancellationToken ct = default)
    {
        var model = StampPackSource(
            WorkflowDefinitionWireMapper.ToModel(authored, CascadeLayer.Pack), authority);
        RequirePackAuthority(model, authority);
        var coordinates = Coordinates(model);
        var persisted = await inner.GetAsync(coordinates, ct).ConfigureAwait(false);
        RequirePersistedPackSource(persisted, authority);
        var candidate = PersistedModel(persisted);
        RequirePackWireMatchesPersisted(authored, persisted.Authored);
        var provenance = DefinitionAuthorityClassifier.Classify(
            DefinitionAuthorityKind.VendorPackage, authority.Tenant,
            persisted.Envelope.CascadeLayer, authority.PackId);
        await AdmitPersistedAsync(persisted, provenance.Owner, ct).ConfigureAwait(false);
        await ValidateSupersessionAsync(candidate, DefinitionAuthorityKind.VendorPackage, authority.PackId, ct).ConfigureAwait(false);
        var stored = await writer.PublishPackAsync(
            coordinates, authority.ActivationInstant, ct).ConfigureAwait(false);
        return stored;
    }

    public async ValueTask<WorkflowDefinitionRecord> WithdrawAsync(
        DefinitionCoordinates coordinates,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        RequireTenant(coordinates, authority);
        await DecideAsync(coordinates.Address.Identity.Value, authority, ct).ConfigureAwait(false);
        var persisted = await inner.GetAsync(coordinates, ct).ConfigureAwait(false);
        var provenance = DefinitionAuthorityClassifier.Classify(
            DefinitionAuthorityKind.Tenant, authority.Tenant, persisted.Envelope.CascadeLayer);
        var candidate = WorkflowDefinitionWireMapper.ToModel(
            persisted.Authored, persisted.Tenant, persisted.Key, persisted.Version, persisted.Envelope.CascadeLayer);
        await AdmitAsync(CopySource(candidate, persisted.PackSource), provenance.Owner, ct).ConfigureAwait(false);
        var stored = await writer.WithdrawAsync(coordinates, ct).ConfigureAwait(false);
        return stored;
    }

    public async ValueTask<WorkflowDefinitionRecord> WithdrawAsync(
        JsonElement authored,
        PackProjectionAuthority authority,
        CancellationToken ct = default)
    {
        var model = StampPackSource(
            WorkflowDefinitionWireMapper.ToModel(authored, CascadeLayer.Pack), authority);
        RequirePackAuthority(model, authority);
        var coordinates = Coordinates(model);
        var persisted = await inner.GetAsync(coordinates, ct).ConfigureAwait(false);
        RequirePersistedPackSource(persisted, authority);
        var candidate = PersistedModel(persisted);
        RequirePackWireMatchesPersisted(authored, persisted.Authored);
        var provenance = DefinitionAuthorityClassifier.Classify(
            DefinitionAuthorityKind.VendorPackage, authority.Tenant,
            persisted.Envelope.CascadeLayer, authority.PackId);
        await AdmitPersistedAsync(persisted, provenance.Owner, ct).ConfigureAwait(false);
        var stored = await writer.WithdrawPackAsync(
            coordinates, authority.ActivationInstant, ct).ConfigureAwait(false);
        return stored;
    }

    public async ValueTask<WorkflowDefinitionRecord> RestorePackProjectionAsync(
        JsonElement authored,
        PackProjectionAuthority authority,
        CancellationToken ct = default)
    {
        var model = StampPackSource(
            WorkflowDefinitionWireMapper.ToModel(authored, CascadeLayer.Pack), authority);
        RequirePackAuthority(model, authority);
        var coordinates = Coordinates(model);
        var persisted = await inner.GetAsync(coordinates, ct).ConfigureAwait(false);
        RequirePersistedPackSource(persisted, authority);
        var candidate = PersistedModel(persisted);
        RequirePackWireMatchesPersisted(authored, persisted.Authored);
        var provenance = DefinitionAuthorityClassifier.Classify(
            DefinitionAuthorityKind.VendorPackage, authority.Tenant,
            persisted.Envelope.CascadeLayer, authority.PackId);
        await AdmitPersistedAsync(persisted, provenance.Owner, ct).ConfigureAwait(false);
        var stored = await writer.RestorePackAsync(
            coordinates, authority.ActivationInstant, ct).ConfigureAwait(false);
        return stored;
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

    private static void RequireWriteAuthority(TenantId tenant, string id, WriteAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var decision = authority.Decision;
        if (decision.Request.Tenant != tenant
            || !string.Equals(authority.DefinitionId, id, StringComparison.Ordinal)
            || decision.Request.Act.Operation.Value != Permission.SchedulingAuthor
            || decision.Request.Target.RecordKind != "scheduling"
            || decision.Request.Target.RecordId != AuthorizationTargetId(id))
            throw new AuthorizationDeniedException(decision);
    }

    private static void RequirePackAuthority(WorkflowDefinition model, PackProjectionAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(authority);
        authority.EnsureUsable();
        if (!string.Equals(model.Tenant, authority.Tenant.Value, StringComparison.Ordinal))
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.TenantMismatch);
        if (model.PackSource is { } source
            && (!string.Equals(source.PackId, authority.PackId, StringComparison.Ordinal)
                || !string.Equals(source.PackVersion, authority.PackVersion, StringComparison.Ordinal)))
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.SourceMismatch);
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

    private interface IWriterBackend
    {
        ValueTask<WorkflowDefinitionRecord> RegisterAsync(
            WorkflowDefinition model, JsonElement authored,
            WorkflowDefinitionRegistrationOptions? options, CancellationToken ct);
        ValueTask<WorkflowDefinitionRecord> PublishAsync(DefinitionCoordinates coordinates, CancellationToken ct);
        ValueTask<WorkflowDefinitionRecord> WithdrawAsync(DefinitionCoordinates coordinates, CancellationToken ct);
        ValueTask<WorkflowDefinitionRecord> PublishPackAsync(DefinitionCoordinates coordinates, DateTimeOffset at, CancellationToken ct);
        ValueTask<WorkflowDefinitionRecord> WithdrawPackAsync(DefinitionCoordinates coordinates, DateTimeOffset at, CancellationToken ct);
        ValueTask<WorkflowDefinitionRecord> RestorePackAsync(DefinitionCoordinates coordinates, DateTimeOffset at, CancellationToken ct);
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
            WorkflowDefinition model, JsonElement authored,
            WorkflowDefinitionRegistrationOptions? options, CancellationToken ct) =>
            backend.RegisterAsync(model, authored, options, ct);
        internal ValueTask<WorkflowDefinitionRecord> PublishAsync(DefinitionCoordinates c, CancellationToken ct) => backend.PublishAsync(c, ct);
        internal ValueTask<WorkflowDefinitionRecord> WithdrawAsync(DefinitionCoordinates c, CancellationToken ct) => backend.WithdrawAsync(c, ct);
        internal ValueTask<WorkflowDefinitionRecord> PublishPackAsync(DefinitionCoordinates c, DateTimeOffset at, CancellationToken ct) => backend.PublishPackAsync(c, at, ct);
        internal ValueTask<WorkflowDefinitionRecord> WithdrawPackAsync(DefinitionCoordinates c, DateTimeOffset at, CancellationToken ct) => backend.WithdrawPackAsync(c, at, ct);
        internal ValueTask<WorkflowDefinitionRecord> RestorePackAsync(DefinitionCoordinates c, DateTimeOffset at, CancellationToken ct) => backend.RestorePackAsync(c, at, ct);
    }

    private sealed class EntityWriterBackend
        : EntityStoreDefinitionLifecycle<WorkflowDefinitionRecord>, IWriterBackend
    {
        private const string EnvelopeKind = "workflow-definition";
        private const string EntityScheme = "workflowdef";
        private const string EntityAuthority = "workflows";
        private readonly IWorkflowAdmissionValidator admission;
        private readonly TimeProvider time;

        internal EntityWriterBackend(
            IEntityMutationStore store, IWorkflowAdmissionValidator admission, TimeProvider time)
            : base(store, store, time, EntityStoreWorkflowDefinitionStore.DefinitionSchema,
                EnvelopeKind, "key", EntityScheme, EntityAuthority)
        {
            this.admission = admission;
            this.time = time;
        }

        public async ValueTask<WorkflowDefinitionRecord> RegisterAsync(
            WorkflowDefinition model, JsonElement authored,
            WorkflowDefinitionRegistrationOptions? options, CancellationToken ct)
        {
            admission.EnsureAdmissible(model);
            var status = options?.Status ?? model.Status;
            var effectiveAt = options?.EffectiveAt ?? time.GetUtcNow();
            var coordinates = new DefinitionCoordinates(model.Envelope.Tenant, model.Key, model.Version);
            var entityId = EntityIdFor(coordinates);
            if (await Store.GetAsync(entityId, VersionSelector.Latest, ct).ConfigureAwait(false) is not null)
                throw new WorkflowDefinitionConflictException(model.Key, model.Version, model.Tenant);
            using var body = EntityStoreWorkflowDefinitionStore.SerializeEnvelope(
                model.Envelope, status, authored, model.PackSource, effectiveAt);
            var create = new CreateOptions(
                EntityScheme, EntityAuthority, NonceFor(coordinates),
                EntityStoreWorkflowDefinitionStore.DefinitionAuthor, new TenantId(model.Tenant),
                effectiveAt, ExplicitLocalPart: entityId.LocalPart);
            try
            {
                await Mutations.CreateAsync(
                    EntityStoreWorkflowDefinitionStore.DefinitionSchema, body, create, ct).ConfigureAwait(false);
            }
            catch (IdempotencyConflictException)
            {
                throw new WorkflowDefinitionConflictException(model.Key, model.Version, model.Tenant);
            }
            return new WorkflowDefinitionRecord(model.Envelope, status, authored.Clone())
            {
                PackSource = model.PackSource,
                UpdatedAt = effectiveAt,
            };
        }

        public new ValueTask<WorkflowDefinitionRecord> PublishAsync(DefinitionCoordinates c, CancellationToken ct) => base.PublishAsync(c, ct);
        public new ValueTask<WorkflowDefinitionRecord> WithdrawAsync(DefinitionCoordinates c, CancellationToken ct) => base.WithdrawAsync(c, ct);
        public ValueTask<WorkflowDefinitionRecord> PublishPackAsync(DefinitionCoordinates c, DateTimeOffset at, CancellationToken ct) => TransitionAsync(
            c, DefinitionLifecycleStatus.Published,
            [DefinitionLifecycleStatus.Draft, DefinitionLifecycleStatus.Published], at, ct);
        public ValueTask<WorkflowDefinitionRecord> WithdrawPackAsync(DefinitionCoordinates c, DateTimeOffset at, CancellationToken ct) => TransitionAsync(
            c, DefinitionLifecycleStatus.Withdrawn,
            [DefinitionLifecycleStatus.Draft, DefinitionLifecycleStatus.Published,
                DefinitionLifecycleStatus.Deprecated, DefinitionLifecycleStatus.Withdrawn], at, ct);
        public ValueTask<WorkflowDefinitionRecord> RestorePackAsync(DefinitionCoordinates c, DateTimeOffset at, CancellationToken ct) => RestorePackProjectionAtAsync(c, at, ct);

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
    }

}
