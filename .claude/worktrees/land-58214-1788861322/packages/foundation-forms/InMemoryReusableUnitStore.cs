using System.Runtime.CompilerServices;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms;

/// <summary>
/// In-memory reference implementation of <see cref="IReusableUnitStore"/> (D4). Mirrors
/// <see cref="InMemoryFormDefinitionStore"/> — a nested dictionary keyed by
/// tenant → unit id → version → record, mutation serialised by a single
/// <see cref="SemaphoreSlim"/> with copy-on-write so snapshot iteration is lock-free. Suitable
/// for tests, single-process bootstrapping, and the early authoring loop; a durable
/// entity-store-backed implementation composes this contract in a follow-up.
/// </summary>
public sealed class InMemoryReusableUnitStore : IReusableUnitStore, IDisposable
{
    private readonly SemaphoreSlim _mutationLock = new(initialCount: 1, maxCount: 1);
    private readonly TimeProvider _time;

    // tenant → id → version → unit
    private Dictionary<TenantId, Dictionary<ReusableUnitId, Dictionary<SemanticVersion, ReusableUnit>>> _store = new();

    /// <summary>Constructs a store with the supplied <see cref="TimeProvider"/>.</summary>
    public InMemoryReusableUnitStore(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <inheritdoc />
    public ValueTask<ReusableUnit> GetAsync(TenantId tenant, ReusableUnitId id, SemanticVersion version, CancellationToken ct = default)
    {
        var snapshot = _store;
        if (snapshot.TryGetValue(tenant, out var byId) &&
            byId.TryGetValue(id, out var byVersion) &&
            byVersion.TryGetValue(version, out var unit))
        {
            return new ValueTask<ReusableUnit>(unit);
        }

        throw new ReusableUnitNotFoundException(id, version, tenant);
    }

    /// <inheritdoc />
    public ValueTask<ReusableUnit?> GetCurrentPublishedAsync(TenantId tenant, ReusableUnitId id, CancellationToken ct = default)
    {
        var snapshot = _store;
        if (!snapshot.TryGetValue(tenant, out var byId) || !byId.TryGetValue(id, out var byVersion))
        {
            return new ValueTask<ReusableUnit?>((ReusableUnit?)null);
        }

        ReusableUnit? best = null;
        foreach (var revision in byVersion.Values)
        {
            if (revision.Status != FormDefinitionStatus.Published) continue;
            if (best is null || revision.Version > best.Version)
            {
                best = revision;
            }
        }

        return new ValueTask<ReusableUnit?>(best);
    }

    /// <inheritdoc />
    public async ValueTask<ReusableUnit> RegisterAsync(ReusableUnit unit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ReusableUnitValidation.ValidateOrThrow(unit);

        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snapshot = _store;
            if (snapshot.TryGetValue(unit.Tenant, out var byId) &&
                byId.TryGetValue(unit.Id, out var byVersion) &&
                byVersion.ContainsKey(unit.Version))
            {
                throw new ReusableUnitConflictException(unit.Id, unit.Version, unit.Tenant);
            }

            _store = MutateStore(snapshot, unit);
            return unit;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask<ReusableUnit> PublishAsync(TenantId tenant, ReusableUnitId id, SemanticVersion version, CancellationToken ct = default)
        => TransitionAsync(tenant, id, version, FormDefinitionStatus.Published, allowedFrom: new[] { FormDefinitionStatus.Draft, FormDefinitionStatus.Published }, ct);

    /// <inheritdoc />
    public ValueTask<ReusableUnit> DeprecateAsync(TenantId tenant, ReusableUnitId id, SemanticVersion version, CancellationToken ct = default)
        => TransitionAsync(tenant, id, version, FormDefinitionStatus.Deprecated, allowedFrom: new[] { FormDefinitionStatus.Published, FormDefinitionStatus.Deprecated }, ct);

    /// <inheritdoc />
    public ValueTask<ReusableUnit> WithdrawAsync(TenantId tenant, ReusableUnitId id, SemanticVersion version, CancellationToken ct = default)
        => TransitionAsync(tenant, id, version, FormDefinitionStatus.Withdrawn, allowedFrom: new[] { FormDefinitionStatus.Draft, FormDefinitionStatus.Published, FormDefinitionStatus.Deprecated, FormDefinitionStatus.Withdrawn }, ct);

    /// <inheritdoc />
    public async IAsyncEnumerable<ReusableUnit> ListByTenantAsync(TenantId tenant, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var snapshot = _store;
        if (!snapshot.TryGetValue(tenant, out var byId))
        {
            yield break;
        }

        foreach (var idEntry in byId.OrderBy(kv => kv.Key.Value, StringComparer.Ordinal))
        {
            foreach (var versionEntry in idEntry.Value.OrderBy(kv => kv.Key))
            {
                ct.ThrowIfCancellationRequested();
                yield return versionEntry.Value;
                await Task.Yield();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _mutationLock.Dispose();

    private async ValueTask<ReusableUnit> TransitionAsync(
        TenantId tenant,
        ReusableUnitId id,
        SemanticVersion version,
        FormDefinitionStatus target,
        FormDefinitionStatus[] allowedFrom,
        CancellationToken ct)
    {
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snapshot = _store;
            if (!snapshot.TryGetValue(tenant, out var byId) ||
                !byId.TryGetValue(id, out var byVersion) ||
                !byVersion.TryGetValue(version, out var existing))
            {
                throw new ReusableUnitNotFoundException(id, version, tenant);
            }

            if (!allowedFrom.Contains(existing.Status))
            {
                throw new InvalidOperationException(
                    $"ReusableUnit '{id}' v{version} cannot transition from {existing.Status} to {target}; allowed source statuses are [{string.Join(", ", allowedFrom)}].");
            }

            if (existing.Status == target)
            {
                return existing;
            }

            var transitioned = existing with
            {
                Status = target,
                UpdatedAt = _time.GetUtcNow(),
            };

            _store = MutateStore(snapshot, transitioned);
            return transitioned;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    private static Dictionary<TenantId, Dictionary<ReusableUnitId, Dictionary<SemanticVersion, ReusableUnit>>> MutateStore(
        Dictionary<TenantId, Dictionary<ReusableUnitId, Dictionary<SemanticVersion, ReusableUnit>>> snapshot,
        ReusableUnit unit)
    {
        var rebuilt = new Dictionary<TenantId, Dictionary<ReusableUnitId, Dictionary<SemanticVersion, ReusableUnit>>>(snapshot);
        if (!rebuilt.TryGetValue(unit.Tenant, out var byId))
        {
            byId = new Dictionary<ReusableUnitId, Dictionary<SemanticVersion, ReusableUnit>>();
            rebuilt[unit.Tenant] = byId;
        }
        else
        {
            byId = new Dictionary<ReusableUnitId, Dictionary<SemanticVersion, ReusableUnit>>(byId);
            rebuilt[unit.Tenant] = byId;
        }

        if (!byId.TryGetValue(unit.Id, out var byVersion))
        {
            byVersion = new Dictionary<SemanticVersion, ReusableUnit>();
            byId[unit.Id] = byVersion;
        }
        else
        {
            byVersion = new Dictionary<SemanticVersion, ReusableUnit>(byVersion);
            byId[unit.Id] = byVersion;
        }

        byVersion[unit.Version] = unit;
        return rebuilt;
    }
}
