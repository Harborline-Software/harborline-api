using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Foundation.Packs.Install;
using System.Text.Json;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Harborline.Api.Foundation.Forms;

/// <summary>The unavoidable authorize-stage façade for form-definition mutations.</summary>
public sealed class AuthorizedFormDefinitionLifecycle
{
    private readonly IFormDefinitionStore inner;
    private readonly DefinitionWriter writer;
    private readonly AuthorizationGate gate;
    private readonly IRoleGateAdmission roleGateAdmission;
    private readonly IFormDefinitionLegalHoldValidator? legalHold;

    private sealed class WriterKey;

    /// <summary>An opaque composition token; it deliberately carries no inspectable state.</summary>
    internal sealed class InMemoryPersistenceHandle
    {
        private InMemoryPersistenceHandle()
        {
        }
    }

    private sealed class InMemoryFormDefinitionState(TimeProvider time)
    {
        private readonly SemaphoreSlim mutationLock = new(initialCount: 1, maxCount: 1);
        private readonly TimeProvider clock = time;
        private Dictionary<TenantId, Dictionary<FormDefinitionId, Dictionary<SemanticVersion, FormDefinition>>> store = new();

        public FormDefinition Read(DefinitionCoordinates coordinates)
        {
            var id = new FormDefinitionId(coordinates.Address.Identity.Value);
            var version = new SemanticVersion(
                coordinates.Version.Major, coordinates.Version.Minor, coordinates.Version.Patch);
            if (TryGet(store, coordinates.Address.Tenant, id, version, out var definition)) return definition!;
            throw new FormDefinitionNotFoundException(id, version, coordinates.Address.Tenant);
        }

        public FormDefinition? ReadCurrent(DefinitionAddress address)
        {
            var id = new FormDefinitionId(address.Identity.Value);
            if (!store.TryGetValue(address.Tenant, out var byId) || !byId.TryGetValue(id, out var versions))
                return null;
            return versions.Values
                .Where(definition => definition.Status == FormDefinitionStatus.Published)
                .MaxBy(definition => definition.Version);
        }

        public FormDefinition[] List(TenantId tenant) => store.TryGetValue(tenant, out var byId)
            ? byId.OrderBy(pair => pair.Key.Value, StringComparer.Ordinal)
                .SelectMany(pair => pair.Value.OrderBy(version => version.Key))
                .Select(pair => pair.Value)
                .ToArray()
            : [];

        public FormDefinition[] ListPublished() => store
            .OrderBy(pair => pair.Key.Value, StringComparer.Ordinal)
            .SelectMany(pair => pair.Value.OrderBy(id => id.Key.Value, StringComparer.Ordinal))
            .Select(pair => pair.Value.Values
                .Where(item => item.Status == FormDefinitionStatus.Published)
                .MaxBy(item => item.Version))
            .Where(item => item is not null)
            .Cast<FormDefinition>()
            .ToArray();

        public async ValueTask<FormDefinition> RegisterAsync(FormDefinition definition, CancellationToken ct)
        {
            var frozen = FormDefinitionFreezer.Freeze(definition);
            FormDefinitionValidation.ValidateOverlayOrThrow(frozen);
            FormDefinitionValidation.ValidateSchemaRefOrThrow(frozen);
            await mutationLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (TryGet(store, frozen.Tenant, frozen.Id, frozen.Version, out _))
                    throw new FormDefinitionConflictException(frozen.Id, frozen.Version, frozen.Tenant);
                if (frozen.Lineage is { } lineage
                    && !TryGet(store, frozen.Tenant, lineage.ParentDefinitionId, lineage.ParentVersion, out _))
                {
                    throw new FormDefinitionValidationException(
                        frozen.Id,
                        $"lineage references parent '{lineage.ParentDefinitionId}' at version '{lineage.ParentVersion}' which is not registered in tenant '{frozen.Tenant}'.");
                }
                store = Mutate(store, frozen);
                return frozen;
            }
            finally
            {
                mutationLock.Release();
            }
        }

        public async ValueTask<FormDefinition> TransitionAsync(
            DefinitionCoordinates coordinates,
            FormDefinitionStatus target,
            FormDefinitionStatus[] allowed,
            DateTimeOffset? transitionedAt,
            CancellationToken ct)
        {
            await mutationLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var existing = Read(coordinates);
                if (!allowed.Contains(existing.Status))
                    throw new InvalidOperationException(
                        $"FormDefinition '{existing.Id}' v{existing.Version} cannot transition from {existing.Status} to {target}.");
                if (existing.Status == target) return existing;
                var changed = existing with
                {
                    Status = target,
                    UpdatedAt = transitionedAt ?? clock.GetUtcNow(),
                };
                store = Mutate(store, changed);
                return changed;
            }
            finally
            {
                mutationLock.Release();
            }
        }

        public void Dispose() => mutationLock.Dispose();

        private static bool TryGet(
            Dictionary<TenantId, Dictionary<FormDefinitionId, Dictionary<SemanticVersion, FormDefinition>>> source,
            TenantId tenant,
            FormDefinitionId id,
            SemanticVersion version,
            out FormDefinition? value)
        {
            value = null;
            return source.TryGetValue(tenant, out var byId)
                && byId.TryGetValue(id, out var byVersion)
                && byVersion.TryGetValue(version, out value);
        }

        private static Dictionary<TenantId, Dictionary<FormDefinitionId, Dictionary<SemanticVersion, FormDefinition>>> Mutate(
            Dictionary<TenantId, Dictionary<FormDefinitionId, Dictionary<SemanticVersion, FormDefinition>>> source,
            FormDefinition definition)
        {
            var result = new Dictionary<TenantId, Dictionary<FormDefinitionId, Dictionary<SemanticVersion, FormDefinition>>>(source);
            var byId = result.TryGetValue(definition.Tenant, out var ids)
                ? new Dictionary<FormDefinitionId, Dictionary<SemanticVersion, FormDefinition>>(ids)
                : new();
            result[definition.Tenant] = byId;
            var byVersion = byId.TryGetValue(definition.Id, out var versions)
                ? new Dictionary<SemanticVersion, FormDefinition>(versions)
                : new();
            byId[definition.Id] = byVersion;
            byVersion[definition.Version] = definition;
            return result;
        }
    }

    private static readonly ConditionalWeakTable<InMemoryPersistenceHandle, InMemoryFormDefinitionState>
        InMemoryStates = new();

    internal static InMemoryPersistenceHandle CreateInMemoryPersistence(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        var handle = (InMemoryPersistenceHandle)(Activator.CreateInstance(
            typeof(InMemoryPersistenceHandle), nonPublic: true)
            ?? throw new InvalidOperationException("The opaque in-memory persistence handle could not be created."));
        InMemoryStates.Add(handle, new InMemoryFormDefinitionState(time));
        return handle;
    }

    private static InMemoryFormDefinitionState Unwrap(InMemoryPersistenceHandle handle) =>
        InMemoryStates.TryGetValue(handle, out var state)
            ? state
            : throw new ObjectDisposedException(nameof(InMemoryFormDefinitionStore));

    internal static ValueTask<FormDefinition> ReadInMemoryAsync(
        InMemoryPersistenceHandle handle,
        DefinitionCoordinates coordinates,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Unwrap(handle).Read(coordinates));
    }

    internal static ValueTask<FormDefinition?> ReadCurrentInMemoryAsync(
        InMemoryPersistenceHandle handle,
        DefinitionAddress address,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Unwrap(handle).ReadCurrent(address));
    }

    internal static async IAsyncEnumerable<FormDefinition> ListInMemoryByTenantAsync(
        InMemoryPersistenceHandle handle,
        TenantId tenant,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var definition in Unwrap(handle).List(tenant))
        {
            ct.ThrowIfCancellationRequested();
            yield return definition;
            await Task.Yield();
        }
    }

    internal static async IAsyncEnumerable<FormDefinition> ListPublishedInMemoryAsync(
        InMemoryPersistenceHandle handle,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var definition in Unwrap(handle).ListPublished())
        {
            ct.ThrowIfCancellationRequested();
            yield return definition;
            await Task.Yield();
        }
    }

    internal static void DisposeInMemory(InMemoryPersistenceHandle handle)
    {
        if (InMemoryStates.TryGetValue(handle, out var state))
        {
            InMemoryStates.Remove(handle);
            state.Dispose();
        }
    }

    internal AuthorizedFormDefinitionLifecycle(
        EntityStoreFormDefinitionStore inner,
        IEntityMutationStore persistenceStore,
        TimeProvider persistenceTime,
        AuthorizationGate gate,
        IRoleGateAdmission roleGateAdmission,
        IFormDefinitionLegalHoldValidator? legalHold = null)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
        this.roleGateAdmission = roleGateAdmission ?? throw new ArgumentNullException(nameof(roleGateAdmission));
        this.legalHold = legalHold;
        writer = CreateWriter(new EntityWriterBackend(
            persistenceStore ?? throw new ArgumentNullException(nameof(persistenceStore)),
            persistenceTime ?? throw new ArgumentNullException(nameof(persistenceTime))));
    }

    internal AuthorizedFormDefinitionLifecycle(
        InMemoryFormDefinitionStore inner,
        InMemoryPersistenceHandle persistenceHandle,
        AuthorizationGate gate,
        IRoleGateAdmission roleGateAdmission,
        IFormDefinitionLegalHoldValidator? legalHold = null)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
        this.roleGateAdmission = roleGateAdmission ?? throw new ArgumentNullException(nameof(roleGateAdmission));
        this.legalHold = legalHold;
        writer = CreateWriter(new InMemoryWriterBackend(
            Unwrap(persistenceHandle ?? throw new ArgumentNullException(nameof(persistenceHandle)))));
    }

    internal IRoleGateAdmission RoleGateAdmission => roleGateAdmission;

    private static DefinitionWriter CreateWriter(IWriterBackend backend) =>
        (DefinitionWriter)(Activator.CreateInstance(
            typeof(DefinitionWriter),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [new WriterKey(), backend],
            culture: null)
            ?? throw new InvalidOperationException("The private-key form writer could not be constructed."));
    /// <summary>Opaque one-form authoring authority minted by this lifecycle.</summary>
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

    public ValueTask<FormDefinition> GetAsync(DefinitionCoordinates coordinates, CancellationToken ct = default) =>
        inner.GetAsync(coordinates, ct);

    public ValueTask<FormDefinition?> GetCurrentPublishedAsync(DefinitionAddress address, CancellationToken ct = default) =>
        inner.GetCurrentPublishedAsync(address, ct);

    public IAsyncEnumerable<FormDefinition> ListByTenantAsync(TenantId tenant, CancellationToken ct = default) =>
        inner.ListByTenantAsync(tenant, ct);

    public async ValueTask<FormDefinition> RegisterAsync(
        FormDefinition definition,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        if (definition.Tenant != authority.Tenant)
            throw new ArgumentException("The form definition tenant does not match the write authority.", nameof(definition));
        var provenance = DefinitionAuthorityClassifier.Classify(
            DefinitionAuthorityKind.Tenant, authority.Tenant, definition.Envelope.CascadeLayer);
        await DecideAsync(definition.Id.Value, authority, ct).ConfigureAwait(false);
        var candidate = StampLayer(definition, provenance.Layer) with
        {
            CreatedAt = authority.At,
            UpdatedAt = authority.At,
        };
        await AdmitAsync(candidate, provenance.Owner, ct).ConfigureAwait(false);
        await ValidateSupersessionAsync(candidate, DefinitionAuthorityKind.Tenant, null, ct).ConfigureAwait(false);
        var stored = await writer.RegisterAsync(candidate, ct).ConfigureAwait(false);
        return stored;
    }

    public async ValueTask<FormDefinition> RegisterAsync(
        FormDefinition definition, WriteAuthority authority, CancellationToken ct = default)
    {
        RequireWriteAuthority(definition.Tenant, definition.Id.Value, authority);
        var provenance = DefinitionAuthorityClassifier.Classify(
            authority.AuthorityKind, definition.Tenant, definition.Envelope.CascadeLayer);
        var candidate = StampLayer(definition, provenance.Layer) with
        {
            CreatedAt = authority.Decision.Request.At,
            UpdatedAt = authority.Decision.Request.At,
        };
        await AdmitAsync(candidate, provenance.Owner, ct).ConfigureAwait(false);
        await ValidateSupersessionAsync(candidate, authority.AuthorityKind, null, ct).ConfigureAwait(false);
        var stored = await writer.RegisterAsync(candidate, ct).ConfigureAwait(false);
        return stored;
    }

    /// <summary>Registers a definition using authority minted for its exact source pack/version.</summary>
    public async ValueTask<FormDefinition> RegisterAsync(
        FormDefinition definition,
        PackProjectionAuthority authority,
        CancellationToken ct = default)
    {
        RequirePackAuthority(definition, authority);
        var provenance = DefinitionAuthorityClassifier.Classify(
            DefinitionAuthorityKind.VendorPackage,
            authority.Tenant,
            definition.Envelope.CascadeLayer,
            authority.PackId);
        var candidate = StampPackSource(StampLayer(definition, provenance.Layer), authority);
        await AdmitAsync(candidate, provenance.Owner, ct).ConfigureAwait(false);
        await ValidateSupersessionAsync(candidate, DefinitionAuthorityKind.VendorPackage, authority.PackId, ct).ConfigureAwait(false);
        var stored = await writer.RegisterAsync(candidate, ct).ConfigureAwait(false);
        return stored;
    }

    /// <summary>Registers an exact pack projection directly as published.</summary>
    public async ValueTask<FormDefinition> RegisterAndPublishAsync(
        FormDefinition definition,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        if (definition.Tenant != authority.Tenant)
            throw new ArgumentException("The form definition tenant does not match the write authority.", nameof(definition));
        var provenance = DefinitionAuthorityClassifier.Classify(
            DefinitionAuthorityKind.Tenant, authority.Tenant, definition.Envelope.CascadeLayer);
        await DecideAsync(definition.Id.Value, authority, ct).ConfigureAwait(false);
        var published = StampLayer(definition, provenance.Layer) with
        {
            Status = FormDefinitionStatus.Published,
            CreatedAt = authority.At,
            UpdatedAt = authority.At,
        };
        await AdmitAsync(published, provenance.Owner, ct).ConfigureAwait(false);
        await ValidateSupersessionAsync(published, DefinitionAuthorityKind.Tenant, null, ct).ConfigureAwait(false);
        var stored = await writer.RegisterAsync(published, ct).ConfigureAwait(false);
        return stored;
    }

    public async ValueTask<FormDefinition> RegisterAndPublishAsync(
        FormDefinition definition, WriteAuthority authority, CancellationToken ct = default)
    {
        RequireWriteAuthority(definition.Tenant, definition.Id.Value, authority);
        var provenance = DefinitionAuthorityClassifier.Classify(
            authority.AuthorityKind, definition.Tenant, definition.Envelope.CascadeLayer);
        var published = StampLayer(definition, provenance.Layer) with
        {
            Status = FormDefinitionStatus.Published,
            CreatedAt = authority.Decision.Request.At,
            UpdatedAt = authority.Decision.Request.At,
        };
        await AdmitAsync(published, provenance.Owner, ct).ConfigureAwait(false);
        await ValidateSupersessionAsync(published, authority.AuthorityKind, null, ct).ConfigureAwait(false);
        var stored = await writer.RegisterAsync(published, ct).ConfigureAwait(false);
        return stored;
    }

    /// <summary>Registers an exact pack projection directly as published.</summary>
    public async ValueTask<FormDefinition> RegisterAndPublishAsync(
        FormDefinition definition,
        PackProjectionAuthority authority,
        CancellationToken ct = default)
    {
        RequirePackAuthority(definition, authority);
        var provenance = DefinitionAuthorityClassifier.Classify(
            DefinitionAuthorityKind.VendorPackage,
            authority.Tenant,
            definition.Envelope.CascadeLayer,
            authority.PackId);
        var published = StampLayer(definition, provenance.Layer) with
        {
            Status = FormDefinitionStatus.Published,
            CreatedAt = authority.ActivationInstant,
            UpdatedAt = authority.ActivationInstant,
            PackSource = Source(authority),
        };
        await AdmitAsync(published, provenance.Owner, ct).ConfigureAwait(false);
        await ValidateSupersessionAsync(published, DefinitionAuthorityKind.VendorPackage, authority.PackId, ct).ConfigureAwait(false);
        var stored = await writer.RegisterAsync(published, ct).ConfigureAwait(false);
        return stored;
    }

    public async ValueTask<FormDefinition> PublishAsync(
        DefinitionCoordinates coordinates,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        RequireTenant(coordinates, authority);
        await DecideAsync(coordinates.Address.Identity.Value, authority, ct).ConfigureAwait(false);
        var definition = await inner.GetAsync(coordinates, ct).ConfigureAwait(false);
        var provenance = DefinitionAuthorityClassifier.Classify(
            DefinitionAuthorityKind.Tenant, authority.Tenant, definition.Envelope.CascadeLayer);
        await AdmitAsync(definition, provenance.Owner, ct).ConfigureAwait(false);
        await ValidateSupersessionAsync(definition, DefinitionAuthorityKind.Tenant, null, ct).ConfigureAwait(false);
        var stored = await writer.PublishAsync(coordinates, ct).ConfigureAwait(false);
        return stored;
    }

    public async ValueTask<FormDefinition> PublishAsync(
        DefinitionCoordinates coordinates, WriteAuthority authority, CancellationToken ct = default)
    {
        RequireWriteAuthority(coordinates.Address.Tenant, coordinates.Address.Identity.Value, authority);
        var definition = await inner.GetAsync(coordinates, ct).ConfigureAwait(false);
        var provenance = DefinitionAuthorityClassifier.Classify(
            authority.AuthorityKind, coordinates.Address.Tenant, definition.Envelope.CascadeLayer);
        await AdmitAsync(definition, provenance.Owner, ct).ConfigureAwait(false);
        await ValidateSupersessionAsync(definition, authority.AuthorityKind, null, ct).ConfigureAwait(false);
        var stored = await writer.PublishAsync(coordinates, ct).ConfigureAwait(false);
        return stored;
    }

    /// <summary>Publishes the exact revision declared by the carried pack authority.</summary>
    public async ValueTask<FormDefinition> PublishAsync(
        FormDefinition definition,
        PackProjectionAuthority authority,
        CancellationToken ct = default)
    {
        RequirePackAuthority(definition, authority);
        var coordinates = Coordinates(definition);
        var persisted = await inner.GetAsync(coordinates, ct).ConfigureAwait(false);
        RequirePersistedPackSource(persisted, authority);
        var provenance = DefinitionAuthorityClassifier.Classify(
            DefinitionAuthorityKind.VendorPackage, authority.Tenant,
            persisted.Envelope.CascadeLayer, authority.PackId);
        await AdmitAsync(persisted, provenance.Owner, ct).ConfigureAwait(false);
        await ValidateSupersessionAsync(persisted, DefinitionAuthorityKind.VendorPackage, authority.PackId, ct).ConfigureAwait(false);
        var stored = await writer.PublishPackAsync(
            coordinates, authority.ActivationInstant, ct).ConfigureAwait(false);
        return stored;
    }

    public async ValueTask<FormDefinition> DeprecateAsync(
        DefinitionCoordinates coordinates,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        RequireTenant(coordinates, authority);
        await DecideAsync(coordinates.Address.Identity.Value, authority, ct).ConfigureAwait(false);
        var definition = await inner.GetAsync(coordinates, ct).ConfigureAwait(false);
        var provenance = DefinitionAuthorityClassifier.Classify(
            DefinitionAuthorityKind.Tenant, authority.Tenant, definition.Envelope.CascadeLayer);
        await AdmitAsync(definition, provenance.Owner, ct).ConfigureAwait(false);
        await RefuseHeldAsync(definition, DefinitionLegalHoldOperation.Supersession, ct).ConfigureAwait(false);
        var stored = await writer.DeprecateAsync(coordinates, ct).ConfigureAwait(false);
        return stored;
    }

    public async ValueTask<FormDefinition> WithdrawAsync(
        DefinitionCoordinates coordinates,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        RequireTenant(coordinates, authority);
        await DecideAsync(coordinates.Address.Identity.Value, authority, ct).ConfigureAwait(false);
        var definition = await inner.GetAsync(coordinates, ct).ConfigureAwait(false);
        var provenance = DefinitionAuthorityClassifier.Classify(
            DefinitionAuthorityKind.Tenant, authority.Tenant, definition.Envelope.CascadeLayer);
        await AdmitAsync(definition, provenance.Owner, ct).ConfigureAwait(false);
        await RefuseHeldAsync(definition, DefinitionLegalHoldOperation.Withdrawal, ct).ConfigureAwait(false);
        var stored = await writer.WithdrawAsync(coordinates, ct).ConfigureAwait(false);
        return stored;
    }

    public async ValueTask<FormDefinition> WithdrawAsync(
        DefinitionCoordinates coordinates, WriteAuthority authority, CancellationToken ct = default)
    {
        RequireWriteAuthority(coordinates.Address.Tenant, coordinates.Address.Identity.Value, authority);
        var definition = await inner.GetAsync(coordinates, ct).ConfigureAwait(false);
        var provenance = DefinitionAuthorityClassifier.Classify(
            authority.AuthorityKind, coordinates.Address.Tenant, definition.Envelope.CascadeLayer);
        await AdmitAsync(definition, provenance.Owner, ct).ConfigureAwait(false);
        await RefuseHeldAsync(definition, DefinitionLegalHoldOperation.Withdrawal, ct).ConfigureAwait(false);
        var stored = await writer.WithdrawAsync(coordinates, ct).ConfigureAwait(false);
        return stored;
    }

    /// <summary>Withdraws the exact revision declared by the carried pack authority.</summary>
    public async ValueTask<FormDefinition> WithdrawAsync(
        FormDefinition definition,
        PackProjectionAuthority authority,
        CancellationToken ct = default)
    {
        RequirePackAuthority(definition, authority, withdrawingPersistedRevision: true);
        var coordinates = Coordinates(definition);
        var persisted = await inner.GetAsync(coordinates, ct).ConfigureAwait(false);
        RequirePersistedPackSource(persisted, authority);
        var provenance = DefinitionAuthorityClassifier.Classify(
            DefinitionAuthorityKind.VendorPackage, authority.Tenant,
            persisted.Envelope.CascadeLayer, authority.PackId);
        await AdmitAsync(persisted, provenance.Owner, ct).ConfigureAwait(false);
        await RefuseHeldAsync(persisted, DefinitionLegalHoldOperation.Withdrawal, ct).ConfigureAwait(false);
        var stored = await writer.WithdrawPackAsync(
            coordinates, authority.ActivationInstant, ct).ConfigureAwait(false);
        return stored;
    }

    public async ValueTask<FormDefinition> RestorePackProjectionAsync(
        DefinitionCoordinates coordinates,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        RequireTenant(coordinates, authority);
        await DecideAsync(coordinates.Address.Identity.Value, authority, ct).ConfigureAwait(false);
        var definition = await inner.GetAsync(coordinates, ct).ConfigureAwait(false);
        var provenance = DefinitionAuthorityClassifier.Classify(
            DefinitionAuthorityKind.Tenant, authority.Tenant, definition.Envelope.CascadeLayer);
        await AdmitAsync(definition, provenance.Owner, ct).ConfigureAwait(false);
        var stored = await writer.RestorePackProjectionAsync(coordinates, ct).ConfigureAwait(false);
        return stored;
    }

    public async ValueTask<FormDefinition> RestorePackProjectionAsync(
        DefinitionCoordinates coordinates, WriteAuthority authority, CancellationToken ct = default)
    {
        RequireWriteAuthority(coordinates.Address.Tenant, coordinates.Address.Identity.Value, authority);
        var definition = await inner.GetAsync(coordinates, ct).ConfigureAwait(false);
        var provenance = DefinitionAuthorityClassifier.Classify(
            authority.AuthorityKind, coordinates.Address.Tenant, definition.Envelope.CascadeLayer);
        await AdmitAsync(definition, provenance.Owner, ct).ConfigureAwait(false);
        var stored = await writer.RestorePackProjectionAsync(coordinates, ct).ConfigureAwait(false);
        return stored;
    }

    /// <summary>Restores the exact revision declared by the carried pack authority.</summary>
    public async ValueTask<FormDefinition> RestorePackProjectionAsync(
        FormDefinition definition,
        PackProjectionAuthority authority,
        CancellationToken ct = default)
    {
        RequirePackAuthority(definition, authority);
        var coordinates = Coordinates(definition);
        var persisted = await inner.GetAsync(coordinates, ct).ConfigureAwait(false);
        RequirePersistedPackSource(persisted, authority);
        var provenance = DefinitionAuthorityClassifier.Classify(
            DefinitionAuthorityKind.VendorPackage, authority.Tenant,
            persisted.Envelope.CascadeLayer, authority.PackId);
        await AdmitAsync(persisted, provenance.Owner, ct).ConfigureAwait(false);
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
            throw new ArgumentException("A form definition id is required.", nameof(id));
        var decision = await gate.DecideAsync(
            authority.Request(AuthorizationOperation.Parse(Permission.FormsAuthor), "forms", AuthorizationTargetId(id)), ct).ConfigureAwait(false);
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
        if (decision.Request.Act.Operation.Value != Permission.FormsAuthor
            || decision.Request.Target.RecordKind != "forms"
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
            || decision.Request.Act.Operation.Value != Permission.FormsAuthor
            || decision.Request.Target.RecordKind != "forms"
            || decision.Request.Target.RecordId != AuthorizationTargetId(id))
            throw new AuthorizationDeniedException(decision);
    }

    private static void RequirePackAuthority(
        FormDefinition definition, PackProjectionAuthority authority, bool withdrawingPersistedRevision = false)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(authority);
        authority.EnsureUsable();
        if (definition.Tenant != authority.Tenant)
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.TenantMismatch);
        if (definition.PackSource is { } source
            && (!string.Equals(source.PackId, authority.PackId, StringComparison.Ordinal)
                || (!withdrawingPersistedRevision
                    && !string.Equals(source.PackVersion, authority.PackVersion, StringComparison.Ordinal))))
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.SourceMismatch);
        // Withdrawal reads a persisted revision whose source version and timestamps may predate
        // this activation. The writer stamps the withdrawal with the carried activation instant.
        if (!withdrawingPersistedRevision && definition.CreatedAt != authority.ActivationInstant)
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.WriteInstantMismatch);
        if (!withdrawingPersistedRevision && definition.UpdatedAt != authority.ActivationInstant)
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.WriteInstantMismatch);
    }

    private static FormDefinition StampPackSource(
        FormDefinition definition,
        PackProjectionAuthority authority) => definition with { PackSource = Source(authority) };

    private static PackProjectionSource Source(PackProjectionAuthority authority) =>
        new(authority.PackId, authority.PackVersion);

    private static void RequirePersistedPackSource(
        FormDefinition persisted,
        PackProjectionAuthority authority)
    {
        if (persisted.Tenant != authority.Tenant)
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.TenantMismatch);
        if (persisted.PackSource is not { } source
            || !string.Equals(source.PackId, authority.PackId, StringComparison.Ordinal))
        {
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.SourceMismatch);
        }
    }

    private static DefinitionCoordinates Coordinates(FormDefinition definition) =>
        new(definition.Tenant, definition.Id.Value, definition.Version.ToString());

    private ValueTask AdmitAsync(
        FormDefinition definition,
        RoleGatedDefinitionOwner owner,
        CancellationToken ct) => roleGateAdmission.AdmitAsync(ToRoleGated(definition, owner), ct);

    private ValueTask AdmitStoredAsync(FormDefinition definition, CancellationToken ct) =>
        roleGateAdmission.AdmitAsync(ToRoleGated(definition), ct);

    public static RoleGatedDefinition ToRoleGated(FormDefinition definition)
    {
        var owner = DefinitionAuthorityClassifier.FromStored(
            definition.Tenant,
            definition.Envelope.CascadeLayer,
            definition.PackSource?.PackId).Owner;
        return ToRoleGated(definition, owner);
    }

    private static RoleGatedDefinition ToRoleGated(
        FormDefinition definition,
        RoleGatedDefinitionOwner owner)
    {
        var gates = new List<DeclarativeGateReference>();
        AddAccess(gates, "form", definition.Overlay.Aspects?.Access);
        foreach (var section in definition.Overlay.Sections)
        {
            AddRoles(gates, $"section:{section.Id}.read", section.Access.ReadRoles);
            AddRoles(gates, $"section:{section.Id}.write", section.Access.WriteRoles);
            AddStandings(gates, $"section:{section.Id}.read", section.Access.ReadStandings);
            AddStandings(gates, $"section:{section.Id}.write", section.Access.WriteStandings);
        }
        foreach (var field in definition.Overlay.Fields)
        {
            AddRoles(gates, $"field:{field.Key}.read", field.Value.FieldReadRoles);
            AddRoles(gates, $"field:{field.Key}.write", field.Value.FieldWriteRoles);
            AddStandings(gates, $"field:{field.Key}.read", field.Value.FieldReadStandings);
            AddStandings(gates, $"field:{field.Key}.write", field.Value.FieldWriteStandings);
            AddAccess(gates, $"field:{field.Key}", field.Value.Aspects?.Access);
        }
        return new RoleGatedDefinition(
            "form", definition.Id.Value, definition.Version.ToString(), owner, gates);
    }

    private static void AddAccess(List<DeclarativeGateReference> gates, string prefix, AccessAspect? access)
    {
        if (access is null) return;
        AddRoles(gates, $"{prefix}.read", access.ReadRoles);
        AddRoles(gates, $"{prefix}.write", access.WriteRoles);
        AddStandings(gates, $"{prefix}.read", access.ReadStandings);
        AddStandings(gates, $"{prefix}.write", access.WriteStandings);
    }

    private static void AddRoles(List<DeclarativeGateReference> gates, string gate, IEnumerable<string>? roles)
    {
        if (roles is null) return;
        gates.AddRange(roles.Select(role => DeclarativeGateReference.ForRole(gate, role)));
    }

    private static void AddStandings(
        List<DeclarativeGateReference> gates,
        string gate,
        IEnumerable<RecordStandingReference>? standings)
    {
        if (standings is null) return;
        gates.AddRange(standings.Select(standing => DeclarativeGateReference.ForStanding(gate, standing)));
    }

    private static FormDefinition StampLayer(FormDefinition definition, CascadeLayer layer) =>
        definition with { Envelope = definition.Envelope with { CascadeLayer = layer } };

    private async ValueTask ValidateSupersessionAsync(
        FormDefinition candidate,
        DefinitionAuthorityKind authorityKind,
        string? authorityPackageId,
        CancellationToken ct)
    {
        var current = await inner.GetCurrentPublishedAsync(
            new DefinitionAddress(candidate.Tenant, candidate.Id.Value), ct).ConfigureAwait(false);
        if (current is not null && candidate.Version > current.Version)
        {
            if (authorityKind == DefinitionAuthorityKind.VendorPackage
                && !string.Equals(current.PackSource?.PackId, authorityPackageId, StringComparison.Ordinal))
            {
                throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.SourceMismatch);
            }
            _ = DefinitionAuthorityClassifier.Classify(
                authorityKind, candidate.Tenant, current.Envelope.CascadeLayer, authorityPackageId);
            if (legalHold is not null)
            {
                await legalHold.RefuseHeldAsync(
                    current, DefinitionLegalHoldOperation.Supersession, ct).ConfigureAwait(false);
            }
        }
    }

    private ValueTask RefuseHeldAsync(
        FormDefinition definition,
        DefinitionLegalHoldOperation operation,
        CancellationToken ct) => legalHold?.RefuseHeldAsync(definition, operation, ct) ?? ValueTask.CompletedTask;

    internal static string AuthorizationTargetId(string definitionId) => Uri.EscapeDataString(definitionId);

    private static void RequireTenant(DefinitionCoordinates coordinates, AuthorizationWriteContext authority)
    {
        if (coordinates.Address.Tenant != authority.Tenant)
            throw new ArgumentException("The form coordinates tenant does not match the write authority.", nameof(coordinates));
    }

    private interface IWriterBackend
    {
        ValueTask<FormDefinition> RegisterAsync(FormDefinition definition, CancellationToken ct);
        ValueTask<FormDefinition> PublishAsync(DefinitionCoordinates coordinates, CancellationToken ct);
        ValueTask<FormDefinition> DeprecateAsync(DefinitionCoordinates coordinates, CancellationToken ct);
        ValueTask<FormDefinition> WithdrawAsync(DefinitionCoordinates coordinates, CancellationToken ct);
        ValueTask<FormDefinition> RestorePackProjectionAsync(DefinitionCoordinates coordinates, CancellationToken ct);
        ValueTask<FormDefinition> PublishPackAsync(DefinitionCoordinates coordinates, DateTimeOffset at, CancellationToken ct);
        ValueTask<FormDefinition> WithdrawPackAsync(DefinitionCoordinates coordinates, DateTimeOffset at, CancellationToken ct);
        ValueTask<FormDefinition> RestorePackAsync(DefinitionCoordinates coordinates, DateTimeOffset at, CancellationToken ct);
    }

    /// <summary>The non-resolvable writer. Every constructor requires the lifecycle's private key.</summary>
    internal sealed class DefinitionWriter
    {
        private readonly IWriterBackend backend;

        private DefinitionWriter(WriterKey key, IWriterBackend backend)
        {
            ArgumentNullException.ThrowIfNull(key);
            this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        internal ValueTask<FormDefinition> RegisterAsync(FormDefinition value, CancellationToken ct) => backend.RegisterAsync(value, ct);
        internal ValueTask<FormDefinition> PublishAsync(DefinitionCoordinates value, CancellationToken ct) => backend.PublishAsync(value, ct);
        internal ValueTask<FormDefinition> DeprecateAsync(DefinitionCoordinates value, CancellationToken ct) => backend.DeprecateAsync(value, ct);
        internal ValueTask<FormDefinition> WithdrawAsync(DefinitionCoordinates value, CancellationToken ct) => backend.WithdrawAsync(value, ct);
        internal ValueTask<FormDefinition> RestorePackProjectionAsync(DefinitionCoordinates value, CancellationToken ct) => backend.RestorePackProjectionAsync(value, ct);
        internal ValueTask<FormDefinition> PublishPackAsync(DefinitionCoordinates value, DateTimeOffset at, CancellationToken ct) => backend.PublishPackAsync(value, at, ct);
        internal ValueTask<FormDefinition> WithdrawPackAsync(DefinitionCoordinates value, DateTimeOffset at, CancellationToken ct) => backend.WithdrawPackAsync(value, at, ct);
        internal ValueTask<FormDefinition> RestorePackAsync(DefinitionCoordinates value, DateTimeOffset at, CancellationToken ct) => backend.RestorePackAsync(value, at, ct);
    }

    private sealed class EntityWriterBackend
        : EntityStoreDefinitionLifecycle<FormDefinition>, IWriterBackend
    {
        private const string EnvelopeKind = "form-definition";
        private const string EntityScheme = "formdef";
        private const string EntityAuthority = "forms";

        internal EntityWriterBackend(IEntityMutationStore store, TimeProvider time)
            : base(store, store, time, EntityStoreFormDefinitionStore.DefinitionSchema,
                EnvelopeKind, "formId", EntityScheme, EntityAuthority)
        {
        }

        public async ValueTask<FormDefinition> RegisterAsync(FormDefinition definition, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(definition);
            var frozen = FormDefinitionFreezer.Freeze(definition);
            FormDefinitionValidation.ValidateOverlayOrThrow(frozen);
            FormDefinitionValidation.ValidateSchemaRefOrThrow(frozen);
            var coordinates = CoordinatesOf(frozen);
            var entityId = EntityIdFor(coordinates);
            if (await Store.GetAsync(entityId, VersionSelector.Latest, ct).ConfigureAwait(false) is not null)
                throw new FormDefinitionConflictException(frozen.Id, frozen.Version, frozen.Tenant);
            if (frozen.Lineage is { } lineage)
            {
                var parentCoordinates = new DefinitionCoordinates(
                    frozen.Tenant, lineage.ParentDefinitionId.Value, lineage.ParentVersion.ToString());
                if (await Store.GetAsync(EntityIdFor(parentCoordinates), VersionSelector.Latest, ct).ConfigureAwait(false) is null)
                {
                    throw new FormDefinitionValidationException(
                        frozen.Id,
                        $"lineage references parent '{lineage.ParentDefinitionId}' at version '{lineage.ParentVersion}' which is not registered in tenant '{frozen.Tenant}'.");
                }
            }
            using var body = Serialize(frozen);
            var options = new CreateOptions(
                EntityScheme, EntityAuthority, NonceFor(coordinates),
                EntityStoreFormDefinitionStore.OwnerActor(frozen.Owner), frozen.Tenant,
                frozen.CreatedAt, ExplicitLocalPart: entityId.LocalPart);
            try
            {
                await Mutations.CreateAsync(EntityStoreFormDefinitionStore.DefinitionSchema, body, options, ct).ConfigureAwait(false);
            }
            catch (IdempotencyConflictException)
            {
                throw new FormDefinitionConflictException(frozen.Id, frozen.Version, frozen.Tenant);
            }
            return frozen;
        }

        public new ValueTask<FormDefinition> PublishAsync(DefinitionCoordinates c, CancellationToken ct) => base.PublishAsync(c, ct);
        public ValueTask<FormDefinition> DeprecateAsync(DefinitionCoordinates c, CancellationToken ct) => TransitionAsync(
            c, DefinitionLifecycleStatus.Deprecated,
            [DefinitionLifecycleStatus.Published, DefinitionLifecycleStatus.Deprecated], ct);
        public new ValueTask<FormDefinition> WithdrawAsync(DefinitionCoordinates c, CancellationToken ct) => base.WithdrawAsync(c, ct);
        public new ValueTask<FormDefinition> RestorePackProjectionAsync(DefinitionCoordinates c, CancellationToken ct) => base.RestorePackProjectionAsync(c, ct);
        public ValueTask<FormDefinition> PublishPackAsync(DefinitionCoordinates c, DateTimeOffset at, CancellationToken ct) => TransitionAsync(
            c, DefinitionLifecycleStatus.Published,
            [DefinitionLifecycleStatus.Draft, DefinitionLifecycleStatus.Published], at, ct);
        public ValueTask<FormDefinition> WithdrawPackAsync(DefinitionCoordinates c, DateTimeOffset at, CancellationToken ct) => TransitionAsync(
            c, DefinitionLifecycleStatus.Withdrawn,
            [DefinitionLifecycleStatus.Draft, DefinitionLifecycleStatus.Published,
                DefinitionLifecycleStatus.Deprecated, DefinitionLifecycleStatus.Withdrawn], at, ct);
        public ValueTask<FormDefinition> RestorePackAsync(DefinitionCoordinates c, DateTimeOffset at, CancellationToken ct) => RestorePackProjectionAtAsync(c, at, ct);

        protected override DefinitionCoordinates CoordinatesOf(FormDefinition d) => Coordinates(d);
        protected override DefinitionLifecycleStatus StatusOf(FormDefinition d) => (DefinitionLifecycleStatus)d.Status;
        protected override FormDefinition WithStatus(FormDefinition d, DefinitionLifecycleStatus status, DateTimeOffset at) =>
            d with { Status = (FormDefinitionStatus)status, UpdatedAt = at };
        protected override JsonDocument Serialize(FormDefinition d) => EntityStoreFormDefinitionStore.SerializeDefinition(d);
        protected override FormDefinition Deserialize(JsonDocument body) => EntityStoreFormDefinitionStore.DeserializeDefinition(body);
        protected override ActorId TransitionActor(FormDefinition d) => EntityStoreFormDefinitionStore.OwnerActor(d.Owner);
        protected override Exception CreateNotFoundException(DefinitionCoordinates c) => new FormDefinitionNotFoundException(
            new FormDefinitionId(c.Address.Identity.Value),
            new SemanticVersion(c.Version.Major, c.Version.Minor, c.Version.Patch), c.Address.Tenant);
        protected override Exception CreateInvalidTransitionException(
            FormDefinition d, DefinitionLifecycleStatus target, IReadOnlyCollection<DefinitionLifecycleStatus> allowed) =>
            new InvalidOperationException(
                $"FormDefinition '{d.Id}' v{d.Version} cannot transition from {d.Status} to {target}; allowed source statuses are [{string.Join(", ", allowed)}].");
        protected override ValueTask ValidatePackRestoreAsync(FormDefinition d, CancellationToken ct)
        {
            if (d.Owner != IdentityRef.System)
                throw new InvalidOperationException($"Only a System-owned form projection can be restored; '{d.Id}' v{d.Version} is owned by {d.Owner}.");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InMemoryWriterBackend(InMemoryFormDefinitionState state) : IWriterBackend
    {
        public ValueTask<FormDefinition> RegisterAsync(FormDefinition definition, CancellationToken ct) =>
            state.RegisterAsync(definition, ct);

        public ValueTask<FormDefinition> PublishAsync(DefinitionCoordinates c, CancellationToken ct) =>
            state.TransitionAsync(c, FormDefinitionStatus.Published, [FormDefinitionStatus.Draft, FormDefinitionStatus.Published], null, ct);
        public ValueTask<FormDefinition> DeprecateAsync(DefinitionCoordinates c, CancellationToken ct) =>
            state.TransitionAsync(c, FormDefinitionStatus.Deprecated, [FormDefinitionStatus.Published, FormDefinitionStatus.Deprecated], null, ct);
        public ValueTask<FormDefinition> WithdrawAsync(DefinitionCoordinates c, CancellationToken ct) =>
            state.TransitionAsync(c, FormDefinitionStatus.Withdrawn,
                [FormDefinitionStatus.Draft, FormDefinitionStatus.Published, FormDefinitionStatus.Deprecated, FormDefinitionStatus.Withdrawn], null, ct);
        public async ValueTask<FormDefinition> RestorePackProjectionAsync(DefinitionCoordinates c, CancellationToken ct)
        {
            var existing = state.Read(c);
            if (existing.Owner != IdentityRef.System) throw new InvalidOperationException("Only a System-owned form projection can be restored.");
            return await state.TransitionAsync(c, FormDefinitionStatus.Published,
                [FormDefinitionStatus.Withdrawn, FormDefinitionStatus.Published], null, ct).ConfigureAwait(false);
        }
        public ValueTask<FormDefinition> PublishPackAsync(DefinitionCoordinates c, DateTimeOffset at, CancellationToken ct) =>
            state.TransitionAsync(c, FormDefinitionStatus.Published, [FormDefinitionStatus.Draft, FormDefinitionStatus.Published], at, ct);
        public ValueTask<FormDefinition> WithdrawPackAsync(DefinitionCoordinates c, DateTimeOffset at, CancellationToken ct) =>
            state.TransitionAsync(c, FormDefinitionStatus.Withdrawn,
                [FormDefinitionStatus.Draft, FormDefinitionStatus.Published, FormDefinitionStatus.Deprecated, FormDefinitionStatus.Withdrawn], at, ct);
        public async ValueTask<FormDefinition> RestorePackAsync(DefinitionCoordinates c, DateTimeOffset at, CancellationToken ct)
        {
            var existing = state.Read(c);
            if (existing.Owner != IdentityRef.System) throw new InvalidOperationException("Only a System-owned form projection can be restored.");
            return await state.TransitionAsync(c, FormDefinitionStatus.Published,
                [FormDefinitionStatus.Withdrawn, FormDefinitionStatus.Published], at, ct).ConfigureAwait(false);
        }
    }

}
