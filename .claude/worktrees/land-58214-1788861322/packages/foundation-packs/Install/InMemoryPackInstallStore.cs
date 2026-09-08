using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Packs.Install;

/// <summary>
/// An in-memory <see cref="IPackInstallStore"/> — the v1 default + test substrate. Atomicity (S-7) is
/// achieved by COPY-ON-WRITE + a single reference swap: <see cref="Commit"/> builds a complete new
/// per-tenant state and only publishes it at the very end, so a fault at any point mid-commit leaves the
/// prior state fully intact (never a partial seed layer). A durable adapter replaces the swap with a DB
/// transaction but keeps the same all-or-nothing contract.
/// </summary>
public sealed class InMemoryPackInstallStore : IPackInstallStore, IPackInstallMutationStore, IPackProjectionAdmissionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<TenantId, TenantState> _byTenant = new();

    /// <inheritdoc />
    public InstalledPack? GetActive(TenantId tenant, string packKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packKey);
        lock (_gate)
        {
            if (!_byTenant.TryGetValue(tenant, out var state)
                || !state.Active.TryGetValue(packKey, out var version))
            {
                return null;
            }

            return state.Versions.GetValueOrDefault(VersionKey(packKey, version));
        }
    }

    /// <inheritdoc />
    public InstalledPack? GetVersion(TenantId tenant, string packKey, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        lock (_gate)
        {
            return _byTenant.TryGetValue(tenant, out var state)
                ? state.Versions.GetValueOrDefault(VersionKey(packKey, version))
                : null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<InstalledPack> ListInstalled(TenantId tenant)
    {
        lock (_gate)
        {
            if (!_byTenant.TryGetValue(tenant, out var state))
            {
                return Array.Empty<InstalledPack>();
            }

            return state.Versions.Values
                .OrderBy(p => p.PackKey, StringComparer.Ordinal)
                .ThenBy(p => p.Version, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <inheritdoc />
    public PackInstallWatermark? GetWatermark(TenantId tenant, string packKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packKey);
        lock (_gate)
        {
            return _byTenant.TryGetValue(tenant, out var state)
                ? state.Watermarks.GetValueOrDefault(packKey)
                : null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<PackTenantOverride> GetOverrides(TenantId tenant, string packKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packKey);
        lock (_gate)
        {
            if (!_byTenant.TryGetValue(tenant, out var state)
                || !state.Overrides.TryGetValue(packKey, out var overrides))
            {
                return Array.Empty<PackTenantOverride>();
            }

            return overrides.Select(o => o.DeepCopy()).ToList();
        }
    }

    /// <inheritdoc />
    public void SaveOverride(TenantId tenant, string packKey, PackTenantOverride tenantOverride)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packKey);
        ArgumentNullException.ThrowIfNull(tenantOverride);

        lock (_gate)
        {
            var next = _byTenant.TryGetValue(tenant, out var current) ? current.Clone() : new TenantState();
            var list = next.Overrides.TryGetValue(packKey, out var existing)
                ? existing.Where(o => o.ContentKey != tenantOverride.ContentKey).ToList()
                : new List<PackTenantOverride>();
            list.Add(tenantOverride.DeepCopy());
            next.Overrides[packKey] = list;
            _byTenant[tenant] = next;
        }
    }

    /// <inheritdoc />
    public void Commit(PackInstallTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var pack = transaction.InstalledPack;

        lock (_gate)
        {
            // COPY-ON-WRITE: build the whole next state off to the side; publish only at the very end.
            var next = _byTenant.TryGetValue(transaction.Tenant, out var current)
                ? current.Clone()
                : new TenantState();

            next.Versions[VersionKey(pack.PackKey, pack.Version)] = pack;
            next.Watermarks[pack.PackKey] = transaction.Watermark;
            next.Overrides[pack.PackKey] = transaction.ReattachedOverrides.Select(o => o.DeepCopy()).ToList();

            // Single publish — the atomic boundary. Everything above is on the copy; a throw before here
            // leaves the store untouched (S-7 no-partial-seed-layer).
            _byTenant[transaction.Tenant] = next;
        }
    }

    /// <inheritdoc />
    public void Activate(TenantId tenant, string packKey, string version)
        => Activate(tenant, packKey, version, admission: null);

    void IPackProjectionAdmissionStore.ActivateAndRecordProjectionAdmission(
        TenantId tenant, string packKey, string version, PackProjectionAdmission admission)
        => Activate(tenant, packKey, version, admission);

    private void Activate(
        TenantId tenant, string packKey, string version, PackProjectionAdmission? admission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        lock (_gate)
        {
            if (!_byTenant.TryGetValue(tenant, out var current))
            {
                throw new PackTransitionStateException($"No installed packs for tenant; cannot activate '{packKey}@{version}'.");
            }

            var targetKey = VersionKey(packKey, version);
            if (!current.Versions.ContainsKey(targetKey))
            {
                throw new PackTransitionStateException($"Pack version '{packKey}@{version}' is not installed; cannot activate.");
            }

            var next = current.Clone();

            AddProjectionAdmission(next, tenant, packKey, version, admission);

            // Supersede a prior Active version of the same key (its immutable seed layer is retained, S-2).
            if (next.Active.TryGetValue(packKey, out var priorVersion) && priorVersion != version)
            {
                var priorKey = VersionKey(packKey, priorVersion);
                if (next.Versions.TryGetValue(priorKey, out var prior))
                {
                    next.Versions[priorKey] = prior with { Lifecycle = PackLifecycleState.Superseded };
                }
            }

            next.Versions[targetKey] = next.Versions[targetKey] with { Lifecycle = PackLifecycleState.Active };
            next.Active[packKey] = version;

            _byTenant[tenant] = next;
        }
    }

    /// <inheritdoc />
    public void Deactivate(TenantId tenant, string packKey, string version)
        => Deactivate(tenant, packKey, version, admission: null);

    void IPackProjectionAdmissionStore.DeactivateAndRecordProjectionAdmission(
        TenantId tenant, string packKey, string version, PackProjectionAdmission admission)
        => Deactivate(tenant, packKey, version, admission);

    private void Deactivate(
        TenantId tenant, string packKey, string version, PackProjectionAdmission? admission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        lock (_gate)
        {
            if (!_byTenant.TryGetValue(tenant, out var current)
                || !current.Active.TryGetValue(packKey, out var activeVersion)
                || !string.Equals(activeVersion, version, StringComparison.Ordinal))
            {
                throw new PackTransitionStateException(
                    $"Pack version '{packKey}@{version}' is not Active; cannot deactivate.");
            }

            var targetKey = VersionKey(packKey, version);
            if (!current.Versions.ContainsKey(targetKey))
            {
                throw new PackTransitionStateException(
                    $"Pack version '{packKey}@{version}' is not installed; cannot deactivate.");
            }

            var next = current.Clone();
            AddProjectionAdmission(next, tenant, packKey, version, admission);
            next.Versions[targetKey] = next.Versions[targetKey] with { Lifecycle = PackLifecycleState.Inactive };
            next.Active.Remove(packKey);
            _byTenant[tenant] = next;
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> GetKeyOwnership(TenantId tenant)
    {
        lock (_gate)
        {
            return _byTenant.TryGetValue(tenant, out var state)
                ? new Dictionary<string, string>(state.KeyOwnership, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <inheritdoc />
    public void RecordKeyOwnership(TenantId tenant, string contentKey, string owningPackKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(owningPackKey);

        lock (_gate)
        {
            var next = _byTenant.TryGetValue(tenant, out var current) ? current.Clone() : new TenantState();
            next.KeyOwnership[contentKey] = owningPackKey;
            _byTenant[tenant] = next;
        }
    }

    IReadOnlyList<PackProjectionAdmission> IPackProjectionAdmissionStore.ListIncompleteProjectionAdmissions()
    {
        lock (_gate)
        {
            return _byTenant.Values
                .SelectMany(state => state.ProjectionAdmissions.Values)
                .Where(admission => !admission.Projected)
                .OrderBy(admission => admission.Tenant.Value, StringComparer.Ordinal)
                .ThenBy(admission => admission.Instant)
                .ThenBy(admission => admission.AdmissionId)
                .ToArray();
        }
    }

    void IPackProjectionAdmissionStore.MarkProjectionCompleted(Guid admissionId)
    {
        lock (_gate)
        {
            foreach (var (tenant, current) in _byTenant)
            {
                if (!current.ProjectionAdmissions.TryGetValue(admissionId, out var admission))
                    continue;
                var next = current.Clone();
                next.ProjectionAdmissions[admissionId] = admission with { Projected = true };
                _byTenant[tenant] = next;
                return;
            }
        }
    }

    private static void AddProjectionAdmission(
        TenantState state,
        TenantId tenant,
        string packKey,
        string version,
        PackProjectionAdmission? admission)
    {
        if (admission is null)
            return;
        if (admission.Tenant != tenant
            || !string.Equals(admission.PackId, packKey, StringComparison.Ordinal)
            || !string.Equals(admission.PackVersion, version, StringComparison.Ordinal))
            throw new InvalidOperationException("The pack projection admission does not match the transition.");
        if (!state.ProjectionAdmissions.TryAdd(admission.AdmissionId, admission with
            {
                DerivationIds = admission.DerivationIds.ToArray(),
            }))
        {
            throw new InvalidOperationException("A pack projection admission id cannot be reused.");
        }
    }

    /// <summary>
    /// The per-tenant version-map key. A VALUE-TUPLE key (structural equality) — NOT a delimited string —
    /// so it is collision-free by construction: no in-band separator to reserve, escape, or validate, and
    /// no way for a <c>packKey</c>/<c>version</c> containing any character (including a control byte) to
    /// forge a boundary. (The prior form used a literal NUL separator, which also made this source file
    /// binary to git.)
    /// </summary>
    private static (string PackKey, string Version) VersionKey(string packKey, string version) => (packKey, version);

    private sealed class TenantState
    {
        public Dictionary<(string PackKey, string Version), InstalledPack> Versions { get; init; } = new();
        public Dictionary<string, string> Active { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, PackInstallWatermark> Watermarks { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<PackTenantOverride>> Overrides { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> KeyOwnership { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<Guid, PackProjectionAdmission> ProjectionAdmissions { get; init; } = new();

        public TenantState Clone() => new()
        {
            Versions = new Dictionary<(string, string), InstalledPack>(Versions),
            Active = new Dictionary<string, string>(Active, StringComparer.Ordinal),
            Watermarks = new Dictionary<string, PackInstallWatermark>(Watermarks, StringComparer.Ordinal),
            Overrides = Overrides.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Select(o => o.DeepCopy()).ToList(),
                StringComparer.Ordinal),
            KeyOwnership = new Dictionary<string, string>(KeyOwnership, StringComparer.Ordinal),
            ProjectionAdmissions = new Dictionary<Guid, PackProjectionAdmission>(ProjectionAdmissions),
        };
    }
}
