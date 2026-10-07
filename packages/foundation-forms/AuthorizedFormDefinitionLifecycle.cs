using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Kernel.Runtime;
using System.Text.Json;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Harborline.Api.Foundation.Forms;

/// <summary>The unavoidable authorize-stage façade for form-definition mutations.</summary>
public sealed class AuthorizedFormDefinitionLifecycle : IDisposable, IPackProjectionParticipant
{
    public void StageProjection(PackProjectionTransaction transaction)
    {
        transaction.Enlist(inner);
        transaction.Enlist(CatalogueSources);
    }
    public CatalogueFormSources CatalogueSources { get; } = new();
    public void Dispose() => CatalogueSources.Dispose();
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

    private sealed class InMemoryFormDefinitionState(TimeProvider time) : IDisposable, IPackProjectionParticipant
    {
        private readonly SemaphoreSlim mutationLock = new(initialCount: 1, maxCount: 1);
        private readonly TimeProvider clock = time;
        private Dictionary<TenantId, Dictionary<FormDefinitionId, Dictionary<SemanticVersion, FormDefinition>>> store = new();

        public void StageProjection(PackProjectionTransaction transaction) => transaction.Stage(this, () =>
        {
            // Mutate already copies the full path before writing; retaining this root is sufficient.
            var before = store;
            return () => store = before;
        });

        public FormDefinition Read(DefinitionCoordinates coordinates)
        {
            using var projectionLease = PackProjectionActivationBarrier.Read();
            var id = new FormDefinitionId(coordinates.Address.Identity.Value);
            var version = new SemanticVersion(
                coordinates.Version.Major, coordinates.Version.Minor, coordinates.Version.Patch);
            if (TryGet(store, coordinates.Address.Tenant, id, version, out var definition)) return definition!;
            throw new FormDefinitionNotFoundException(id, version, coordinates.Address.Tenant);
        }

        public FormDefinition? ReadCurrent(DefinitionAddress address)
        {
            using var projectionLease = PackProjectionActivationBarrier.Read();
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
            using var projectionLease = PackProjectionActivationBarrier.Read(ct);
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
            using var projectionLease = PackProjectionActivationBarrier.Read(ct);
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

    internal static void StageInMemory(InMemoryPersistenceHandle handle, PackProjectionTransaction transaction)
        => transaction.Enlist(Unwrap(handle));

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
        FormDefinition[] snapshot;
        using (PackProjectionActivationBarrier.Read(ct))
            snapshot = Unwrap(handle).List(tenant);
        foreach (var definition in snapshot)
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
        FormDefinition[] snapshot;
        using (PackProjectionActivationBarrier.Read(ct))
            snapshot = Unwrap(handle).ListPublished();
        foreach (var definition in snapshot)
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
        IFormDefinitionLegalHoldValidator? legalHold = null,
        IWritePipelineObserver? pipelineObserver = null)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
        this.roleGateAdmission = roleGateAdmission ?? throw new ArgumentNullException(nameof(roleGateAdmission));
        this.legalHold = legalHold;
        writer = CreateWriter(new EntityWriterBackend(
            persistenceStore ?? throw new ArgumentNullException(nameof(persistenceStore)),
            persistenceTime ?? throw new ArgumentNullException(nameof(persistenceTime)),
            pipelineObserver));
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
        var decided = await DecideAsync(definition.Id.Value, authority, ct).ConfigureAwait(false);
        var candidate = StampLayer(definition, provenance.Layer) with
        {
            CreatedAt = authority.At,
            UpdatedAt = authority.At,
        };
        return await RegisterCoreAsync(candidate, Admission(decided), Target(definition),
            provenance.Owner, DefinitionAuthorityKind.Tenant, null, ct).ConfigureAwait(false);
    }

    public ValueTask<FormDefinition> RegisterAsync(
        FormDefinition definition, WriteAuthority authority, CancellationToken ct = default)
    {
        var provenance = DefinitionAuthorityClassifier.Classify(
            Kind(authority), definition.Tenant, definition.Envelope.CascadeLayer);
        var candidate = StampLayer(definition, provenance.Layer) with
        {
            CreatedAt = authority.Decision.Request.At,
            UpdatedAt = authority.Decision.Request.At,
        };
        return RegisterCoreAsync(candidate, Admission(authority), Target(definition),
            provenance.Owner, authority.AuthorityKind, null, ct);
    }

    /// <summary>Registers a definition using authority minted for its exact source pack/version.</summary>
    public ValueTask<FormDefinition> RegisterAsync(
        FormDefinition definition,
        PackProjectionAuthority authority,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(authority);
        var provenance = DefinitionAuthorityClassifier.Classify(
            DefinitionAuthorityKind.VendorPackage,
            authority.Tenant,
            definition.Envelope.CascadeLayer,
            authority.PackId);
        var candidate = StampPackSource(StampLayer(definition, provenance.Layer), authority);
        return RegisterCoreAsync(candidate, authority.ToWriteAdmission(), PackTarget(definition),
            provenance.Owner, DefinitionAuthorityKind.VendorPackage, authority.PackId, ct);
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
        var decided = await DecideAsync(definition.Id.Value, authority, ct).ConfigureAwait(false);
        var published = StampLayer(definition, provenance.Layer) with
        {
            Status = FormDefinitionStatus.Published,
            CreatedAt = authority.At,
            UpdatedAt = authority.At,
        };
        return await RegisterCoreAsync(published, Admission(decided), Target(definition),
            provenance.Owner, DefinitionAuthorityKind.Tenant, null, ct).ConfigureAwait(false);
    }

    public ValueTask<FormDefinition> RegisterAndPublishAsync(
        FormDefinition definition, WriteAuthority authority, CancellationToken ct = default)
    {
        var provenance = DefinitionAuthorityClassifier.Classify(
            Kind(authority), definition.Tenant, definition.Envelope.CascadeLayer);
        var published = StampLayer(definition, provenance.Layer) with
        {
            Status = FormDefinitionStatus.Published,
            CreatedAt = authority.Decision.Request.At,
            UpdatedAt = authority.Decision.Request.At,
        };
        return RegisterCoreAsync(published, Admission(authority), Target(definition),
            provenance.Owner, authority.AuthorityKind, null, ct);
    }

    /// <summary>Registers an exact pack projection directly as published.</summary>
    public ValueTask<FormDefinition> RegisterAndPublishAsync(
        FormDefinition definition,
        PackProjectionAuthority authority,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(authority);
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
        return RegisterCoreAsync(published, authority.ToWriteAdmission(), PackTarget(definition),
            provenance.Owner, DefinitionAuthorityKind.VendorPackage, authority.PackId, ct);
    }

    public async ValueTask<FormDefinition> PublishAsync(
        DefinitionCoordinates coordinates,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        RequireTenant(coordinates, authority);
        var decided = await DecideAsync(coordinates.Address.Identity.Value, authority, ct).ConfigureAwait(false);
        return await TransitionCoreAsync(coordinates, DefinitionLifecycleTransition.Publish, null,
            Admission(decided), Target(coordinates), async (persisted, token) =>
            {
                var provenance = DefinitionAuthorityClassifier.Classify(
                    DefinitionAuthorityKind.Tenant, authority.Tenant, persisted.Envelope.CascadeLayer);
                await AdmitAsync(persisted, provenance.Owner, token).ConfigureAwait(false);
                await ValidateSupersessionAsync(persisted, DefinitionAuthorityKind.Tenant, null, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
    }

    public ValueTask<FormDefinition> PublishAsync(
        DefinitionCoordinates coordinates, WriteAuthority authority, CancellationToken ct = default) =>
        TransitionCoreAsync(coordinates, DefinitionLifecycleTransition.Publish, null,
            Admission(authority), Target(coordinates), async (persisted, token) =>
            {
                var provenance = DefinitionAuthorityClassifier.Classify(
                    authority.AuthorityKind, coordinates.Address.Tenant, persisted.Envelope.CascadeLayer);
                await AdmitAsync(persisted, provenance.Owner, token).ConfigureAwait(false);
                await ValidateSupersessionAsync(persisted, authority.AuthorityKind, null, token).ConfigureAwait(false);
            }, ct);

    /// <summary>Publishes the exact revision declared by the carried pack authority.</summary>
    public ValueTask<FormDefinition> PublishAsync(
        FormDefinition definition,
        PackProjectionAuthority authority,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(authority);
        return TransitionCoreAsync(Coordinates(definition), DefinitionLifecycleTransition.Publish, authority.ActivationInstant,
            authority.ToWriteAdmission(), PackTarget(definition), async (persisted, token) =>
            {
                RequirePersistedPackSource(persisted, authority);
                var provenance = DefinitionAuthorityClassifier.Classify(
                    DefinitionAuthorityKind.VendorPackage, authority.Tenant,
                    persisted.Envelope.CascadeLayer, authority.PackId);
                await AdmitAsync(persisted, provenance.Owner, token).ConfigureAwait(false);
                await ValidateSupersessionAsync(persisted, DefinitionAuthorityKind.VendorPackage, authority.PackId, token).ConfigureAwait(false);
            }, ct);
    }

    public async ValueTask<FormDefinition> DeprecateAsync(
        DefinitionCoordinates coordinates,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        RequireTenant(coordinates, authority);
        var decided = await DecideAsync(coordinates.Address.Identity.Value, authority, ct).ConfigureAwait(false);
        return await TransitionCoreAsync(coordinates, DefinitionLifecycleTransition.Deprecate, null,
            Admission(decided), Target(coordinates), async (persisted, token) =>
            {
                var provenance = DefinitionAuthorityClassifier.Classify(
                    DefinitionAuthorityKind.Tenant, authority.Tenant, persisted.Envelope.CascadeLayer);
                await AdmitAsync(persisted, provenance.Owner, token).ConfigureAwait(false);
                await RefuseHeldAsync(persisted, DefinitionLegalHoldOperation.Supersession, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
    }

    public async ValueTask<FormDefinition> WithdrawAsync(
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
                await AdmitAsync(persisted, provenance.Owner, token).ConfigureAwait(false);
                await RefuseHeldAsync(persisted, DefinitionLegalHoldOperation.Withdrawal, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
    }

    public ValueTask<FormDefinition> WithdrawAsync(
        DefinitionCoordinates coordinates, WriteAuthority authority, CancellationToken ct = default) =>
        TransitionCoreAsync(coordinates, DefinitionLifecycleTransition.Withdraw, null,
            Admission(authority), Target(coordinates), async (persisted, token) =>
            {
                var provenance = DefinitionAuthorityClassifier.Classify(
                    authority.AuthorityKind, coordinates.Address.Tenant, persisted.Envelope.CascadeLayer);
                await AdmitAsync(persisted, provenance.Owner, token).ConfigureAwait(false);
                await RefuseHeldAsync(persisted, DefinitionLegalHoldOperation.Withdrawal, token).ConfigureAwait(false);
            }, ct);

    /// <summary>Withdraws the exact revision declared by the carried pack authority.</summary>
    public ValueTask<FormDefinition> WithdrawAsync(
        FormDefinition definition,
        PackProjectionAuthority authority,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(authority);
        // Withdrawal reads a persisted revision whose source version and timestamps may predate this
        // activation, so the target compares the pack id only; the write carries the activation instant.
        var target = Target(definition) with { DeclaredPackSource = definition.PackSource, ExactPackVersion = false };
        return TransitionCoreAsync(Coordinates(definition), DefinitionLifecycleTransition.Withdraw, authority.ActivationInstant,
            authority.ToWriteAdmission(), target, async (persisted, token) =>
            {
                RequirePersistedPackSource(persisted, authority);
                var provenance = DefinitionAuthorityClassifier.Classify(
                    DefinitionAuthorityKind.VendorPackage, authority.Tenant,
                    persisted.Envelope.CascadeLayer, authority.PackId);
                await AdmitAsync(persisted, provenance.Owner, token).ConfigureAwait(false);
                await RefuseHeldAsync(persisted, DefinitionLegalHoldOperation.Withdrawal, token).ConfigureAwait(false);
            }, ct);
    }

    public async ValueTask<FormDefinition> RestorePackProjectionAsync(
        DefinitionCoordinates coordinates,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        RequireTenant(coordinates, authority);
        var decided = await DecideAsync(coordinates.Address.Identity.Value, authority, ct).ConfigureAwait(false);
        return await TransitionCoreAsync(coordinates, DefinitionLifecycleTransition.Restore, null,
            Admission(decided), Target(coordinates), async (persisted, token) =>
            {
                var provenance = DefinitionAuthorityClassifier.Classify(
                    DefinitionAuthorityKind.Tenant, authority.Tenant, persisted.Envelope.CascadeLayer);
                await AdmitAsync(persisted, provenance.Owner, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
    }

    public ValueTask<FormDefinition> RestorePackProjectionAsync(
        DefinitionCoordinates coordinates, WriteAuthority authority, CancellationToken ct = default) =>
        TransitionCoreAsync(coordinates, DefinitionLifecycleTransition.Restore, null,
            Admission(authority), Target(coordinates), async (persisted, token) =>
            {
                var provenance = DefinitionAuthorityClassifier.Classify(
                    authority.AuthorityKind, coordinates.Address.Tenant, persisted.Envelope.CascadeLayer);
                await AdmitAsync(persisted, provenance.Owner, token).ConfigureAwait(false);
            }, ct);

    /// <summary>Restores the exact revision declared by the carried pack authority.</summary>
    public ValueTask<FormDefinition> RestorePackProjectionAsync(
        FormDefinition definition,
        PackProjectionAuthority authority,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(authority);
        return TransitionCoreAsync(Coordinates(definition), DefinitionLifecycleTransition.Restore, authority.ActivationInstant,
            authority.ToWriteAdmission(), PackTarget(definition), async (persisted, token) =>
            {
                RequirePersistedPackSource(persisted, authority);
                var provenance = DefinitionAuthorityClassifier.Classify(
                    DefinitionAuthorityKind.VendorPackage, authority.Tenant,
                    persisted.Envelope.CascadeLayer, authority.PackId);
                await AdmitAsync(persisted, provenance.Owner, token).ConfigureAwait(false);
            }, ct);
    }

    /// <summary>
    /// One register act. The writer runs it through <c>WritePipeline.RunAsync</c>: authorize checks the carried
    /// admission against this definition, bind reads the store, mutate freezes, validate runs this façade's
    /// role-gate admission and supersession and then the definition checks, commit creates, react returns.
    /// </summary>
    private ValueTask<FormDefinition> RegisterCoreAsync(
        FormDefinition candidate,
        DefinitionWriteAdmission admission,
        DefinitionWriteTarget target,
        RoleGatedDefinitionOwner owner,
        DefinitionAuthorityKind authorityKind,
        string? authorityPackageId,
        CancellationToken ct) =>
        CatalogueSources.PersistAsync(Coordinates(candidate), () => writer.RegisterAsync(
            admission, target, candidate,
            async (frozen, token) =>
            {
                await AdmitAsync(frozen, owner, token).ConfigureAwait(false);
                await ValidateSupersessionAsync(frozen, authorityKind, authorityPackageId, token).ConfigureAwait(false);
            },
            ct));

    /// <summary>One lifecycle transition act; <paramref name="validate"/> runs at validate on the bound revision.</summary>
    private ValueTask<FormDefinition> TransitionCoreAsync(
        DefinitionCoordinates coordinates,
        DefinitionLifecycleTransition transition,
        DateTimeOffset? at,
        DefinitionWriteAdmission admission,
        DefinitionWriteTarget target,
        Func<FormDefinition, CancellationToken, ValueTask> validate,
        CancellationToken ct) =>
        CatalogueSources.PersistAsync(coordinates, () => writer.TransitionAsync(
            admission, target, coordinates, transition, at, validate, ct));

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

    private static DefinitionWriteTarget Target(FormDefinition definition) =>
        new(definition.Tenant, "forms", Permission.FormsAuthor, definition.Id.Value);

    private static DefinitionWriteTarget Target(DefinitionCoordinates coordinates) =>
        new(coordinates.Address.Tenant, "forms", Permission.FormsAuthor, coordinates.Address.Identity.Value);

    /// <summary>A pack write's target: the caller's declared source and its activation-instant stamps.</summary>
    private static DefinitionWriteTarget PackTarget(FormDefinition definition) => Target(definition) with
    {
        DeclaredPackSource = definition.PackSource,
        CreatedAt = definition.CreatedAt,
        UpdatedAt = definition.UpdatedAt,
    };

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
        ValueTask<FormDefinition> RegisterAsync(
            DefinitionWriteAdmission admission, DefinitionWriteTarget target, FormDefinition definition,
            Func<FormDefinition, CancellationToken, ValueTask> validate, CancellationToken ct);
        ValueTask<FormDefinition> TransitionAsync(
            DefinitionWriteAdmission admission, DefinitionWriteTarget target, DefinitionCoordinates coordinates,
            DefinitionLifecycleTransition transition, DateTimeOffset? at,
            Func<FormDefinition, CancellationToken, ValueTask> validate, CancellationToken ct);
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

        internal ValueTask<FormDefinition> RegisterAsync(
            DefinitionWriteAdmission admission, DefinitionWriteTarget target, FormDefinition value,
            Func<FormDefinition, CancellationToken, ValueTask> validate, CancellationToken ct) =>
            backend.RegisterAsync(admission, target, value, validate, ct);

        internal ValueTask<FormDefinition> TransitionAsync(
            DefinitionWriteAdmission admission, DefinitionWriteTarget target, DefinitionCoordinates value,
            DefinitionLifecycleTransition transition, DateTimeOffset? at,
            Func<FormDefinition, CancellationToken, ValueTask> validate, CancellationToken ct) =>
            backend.TransitionAsync(admission, target, value, transition, at, validate, ct);
    }

    private sealed class EntityWriterBackend
        : EntityStoreDefinitionLifecycle<FormDefinition>, IWriterBackend
    {
        private const string EnvelopeKind = "form-definition";
        private const string EntityScheme = "formdef";
        private const string EntityAuthority = "forms";
        private readonly IWritePipelineObserver? observer;

        internal EntityWriterBackend(IEntityMutationStore store, TimeProvider time, IWritePipelineObserver? observer)
            : base(store, store, time, EntityStoreFormDefinitionStore.DefinitionSchema,
                EnvelopeKind, "formId", EntityScheme, EntityAuthority)
        {
            this.observer = observer;
        }

        public async ValueTask<FormDefinition> RegisterAsync(
            DefinitionWriteAdmission admission, DefinitionWriteTarget target, FormDefinition definition,
            Func<FormDefinition, CancellationToken, ValueTask> validate, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(admission);
            ArgumentNullException.ThrowIfNull(definition);
            return (await WritePipeline.RunAsync(
                new Register(this, admission, target, definition, validate), observer, ct).ConfigureAwait(false))!;
        }

        public async ValueTask<FormDefinition> TransitionAsync(
            DefinitionWriteAdmission admission, DefinitionWriteTarget target, DefinitionCoordinates coordinates,
            DefinitionLifecycleTransition transition, DateTimeOffset? at,
            Func<FormDefinition, CancellationToken, ValueTask> validate, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(admission);
            return (await WritePipeline.RunAsync(
                new Transition(this, admission, target, coordinates, transition, at, validate), observer, ct)
                .ConfigureAwait(false))!;
        }

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

        /// <summary>What register binds: whether the revision is already stored, and whether its lineage parent is.</summary>
        private sealed record RegisterBound(bool Stored, bool ParentRegistered);

        /// <summary>ck-10 S3b: one form-definition registration as its six ADR-0038 stages.</summary>
        private sealed class Register(
            EntityWriterBackend backend,
            DefinitionWriteAdmission admission,
            DefinitionWriteTarget target,
            FormDefinition definition,
            Func<FormDefinition, CancellationToken, ValueTask> validate)
            : KernelWrite<RegisterBound, FormDefinition, FormDefinition, FormDefinition>
        {
            protected override ValueTask AuthorizeAsync(CancellationToken ct)
            {
                admission.Authorize(target);
                return ValueTask.CompletedTask;
            }

            protected override async ValueTask<RegisterBound?> BindAsync(CancellationToken ct)
            {
                var stored = await backend.Store.GetAsync(
                    backend.EntityIdFor(Coordinates(definition)), VersionSelector.Latest, ct).ConfigureAwait(false) is not null;
                var parentRegistered = true;
                if (definition.Lineage is { } lineage)
                {
                    var parent = new DefinitionCoordinates(
                        definition.Tenant, lineage.ParentDefinitionId.Value, lineage.ParentVersion.ToString());
                    parentRegistered = await backend.Store.GetAsync(
                        backend.EntityIdFor(parent), VersionSelector.Latest, ct).ConfigureAwait(false) is not null;
                }
                return new RegisterBound(stored, parentRegistered);
            }

            protected override ValueTask<FormDefinition> MutateAsync(RegisterBound bound, CancellationToken ct) =>
                ValueTask.FromResult(FormDefinitionFreezer.Freeze(definition));

            protected override async ValueTask<FormDefinition> ValidateAsync(
                RegisterBound bound, FormDefinition frozen, CancellationToken ct)
            {
                await validate(frozen, ct).ConfigureAwait(false);
                FormDefinitionValidation.ValidateOverlayOrThrow(frozen);
                FormDefinitionValidation.ValidateSchemaRefOrThrow(frozen);
                if (bound.Stored)
                    throw new FormDefinitionConflictException(frozen.Id, frozen.Version, frozen.Tenant);
                if (frozen.Lineage is { } lineage && !bound.ParentRegistered)
                {
                    throw new FormDefinitionValidationException(
                        frozen.Id,
                        $"lineage references parent '{lineage.ParentDefinitionId}' at version '{lineage.ParentVersion}' which is not registered in tenant '{frozen.Tenant}'.");
                }
                return frozen;
            }

            protected override async ValueTask CommitAsync(FormDefinition frozen, CancellationToken ct)
            {
                var coordinates = Coordinates(frozen);
                var entityId = backend.EntityIdFor(coordinates);
                using var body = backend.Serialize(frozen);
                var options = new CreateOptions(
                    EntityScheme, EntityAuthority, NonceFor(coordinates),
                    EntityStoreFormDefinitionStore.OwnerActor(frozen.Owner), frozen.Tenant,
                    frozen.CreatedAt, ExplicitLocalPart: entityId.LocalPart);
                try
                {
                    await backend.Mutations.CreateAsync(EntityStoreFormDefinitionStore.DefinitionSchema, body, options, ct).ConfigureAwait(false);
                }
                catch (IdempotencyConflictException)
                {
                    throw new FormDefinitionConflictException(frozen.Id, frozen.Version, frozen.Tenant);
                }
            }

            protected override ValueTask<FormDefinition> ReactAsync(FormDefinition frozen, CancellationToken ct) =>
                ValueTask.FromResult(frozen);
        }

        /// <summary>The sealed transition: the revision as it will be stored, and whether its status changes.</summary>
        private sealed record TransitionSealed(FormDefinition Revision, bool Changed);

        /// <summary>ck-10 S3b: one form-definition lifecycle transition as its six ADR-0038 stages.</summary>
        private sealed class Transition(
            EntityWriterBackend backend,
            DefinitionWriteAdmission admission,
            DefinitionWriteTarget target,
            DefinitionCoordinates coordinates,
            DefinitionLifecycleTransition transition,
            DateTimeOffset? at,
            Func<FormDefinition, CancellationToken, ValueTask> validate)
            : KernelWrite<FormDefinition, FormDefinition, TransitionSealed, FormDefinition>
        {
            protected override ValueTask AuthorizeAsync(CancellationToken ct)
            {
                admission.Authorize(target);
                return ValueTask.CompletedTask;
            }

            protected override async ValueTask<FormDefinition?> BindAsync(CancellationToken ct) =>
                await backend.GetAsync(coordinates, ct).ConfigureAwait(false);

            protected override ValueTask<FormDefinition> MutateAsync(FormDefinition existing, CancellationToken ct) =>
                ValueTask.FromResult(backend.WithStatus(existing, RuleFor(transition).Target, at ?? backend.Now()));

            protected override async ValueTask<TransitionSealed> ValidateAsync(
                FormDefinition existing, FormDefinition transitioned, CancellationToken ct)
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

            protected override ValueTask<FormDefinition> ReactAsync(TransitionSealed validated, CancellationToken ct) =>
                ValueTask.FromResult(validated.Revision);
        }
    }

    /// <summary>The in-memory backend writes no raw sink; it carries the same admission and authorize check.</summary>
    private sealed class InMemoryWriterBackend(InMemoryFormDefinitionState state) : IWriterBackend
    {
        public async ValueTask<FormDefinition> RegisterAsync(
            DefinitionWriteAdmission admission, DefinitionWriteTarget target, FormDefinition definition,
            Func<FormDefinition, CancellationToken, ValueTask> validate, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(admission);
            admission.Authorize(target);
            await validate(definition, ct).ConfigureAwait(false);
            return await state.RegisterAsync(definition, ct).ConfigureAwait(false);
        }

        public async ValueTask<FormDefinition> TransitionAsync(
            DefinitionWriteAdmission admission, DefinitionWriteTarget target, DefinitionCoordinates c,
            DefinitionLifecycleTransition transition, DateTimeOffset? at,
            Func<FormDefinition, CancellationToken, ValueTask> validate, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(admission);
            admission.Authorize(target);
            var existing = state.Read(c);
            await validate(existing, ct).ConfigureAwait(false);
            if (transition == DefinitionLifecycleTransition.Restore && existing.Owner != IdentityRef.System)
                throw new InvalidOperationException("Only a System-owned form projection can be restored.");
            var (status, allowed) = transition switch
            {
                DefinitionLifecycleTransition.Publish =>
                    (FormDefinitionStatus.Published, new[] { FormDefinitionStatus.Draft, FormDefinitionStatus.Published }),
                DefinitionLifecycleTransition.Deprecate =>
                    (FormDefinitionStatus.Deprecated, new[] { FormDefinitionStatus.Published, FormDefinitionStatus.Deprecated }),
                DefinitionLifecycleTransition.Withdraw =>
                    (FormDefinitionStatus.Withdrawn, new[]
                    {
                        FormDefinitionStatus.Draft, FormDefinitionStatus.Published,
                        FormDefinitionStatus.Deprecated, FormDefinitionStatus.Withdrawn,
                    }),
                DefinitionLifecycleTransition.Restore =>
                    (FormDefinitionStatus.Published, new[] { FormDefinitionStatus.Withdrawn, FormDefinitionStatus.Published }),
                _ => throw new ArgumentOutOfRangeException(nameof(transition), transition, "Unknown lifecycle transition."),
            };
            return await state.TransitionAsync(c, status, allowed, at, ct).ConfigureAwait(false);
        }
    }

}
