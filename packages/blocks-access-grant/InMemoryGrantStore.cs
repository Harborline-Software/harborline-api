using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.AccessGrant;

public sealed class InMemoryGrantStore : IGrantStore, IGrantAuthorizationEpochReader
{
    private readonly object _gate;
    private readonly Dictionary<(string Tenant, Guid Id), AccessGrant> _grants = [];
    private readonly Dictionary<(string Tenant, Guid Id), long> _ownerVersions = [];
    private readonly Dictionary<(string Tenant, string Source), Guid> _sources = [];
    private readonly Dictionary<(string Tenant, Guid Id), string?> _sourceByGrant = [];
    private readonly Dictionary<(string Tenant, string Principal), long> _epochs = [];

    internal InMemoryGrantStore(InMemoryAuthorizationBootstrapFence bootstrapFence)
    {
        ArgumentNullException.ThrowIfNull(bootstrapFence);
        _gate = bootstrapFence.Gate;
        BootstrapFence = bootstrapFence;
    }

    internal InMemoryAuthorizationBootstrapFence BootstrapFence { get; }

    public Task<AccessGrant> AppendAsync(TenantId tenantId, AccessGrant grant, string? sourceReference = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (tenantId != grant.TenantId) throw new ArgumentException("Grant tenant does not match store tenant.", nameof(grant));
        lock (_gate)
        {
            if (sourceReference is not null && _sources.TryGetValue((tenantId.Value, sourceReference), out var sourceId))
            {
                var existing = _grants[(tenantId.Value, sourceId)];
                if (existing != grant) throw new InvalidOperationException("A source reference cannot replace immutable grant evidence.");
                return Task.FromResult(existing);
            }
            var key = (tenantId.Value, grant.GrantId.Value);
            if (_grants.TryGetValue(key, out var byId))
            {
                if (byId != grant) throw new InvalidOperationException("A grant id cannot replace immutable grant evidence.");
                if (!string.Equals(_sourceByGrant[key], sourceReference, StringComparison.Ordinal))
                    throw new InvalidOperationException("A grant id cannot replace its source reference.");
                return Task.FromResult(byId);
            }
            _grants.Add(key, grant);
            _ownerVersions.Add(key, 1);
            _sourceByGrant.Add(key, sourceReference);
            if (sourceReference is not null) _sources.Add((tenantId.Value, sourceReference), grant.GrantId.Value);
            AdvanceEpoch(grant);
            return Task.FromResult(grant);
        }
    }

    public Task<AccessGrant?> FindAsync(TenantId tenantId, GrantId grantId, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult(_grants.GetValueOrDefault((tenantId.Value, grantId.Value))); }
    public Task<VersionedAccessGrant?> FindVersionedAsync(
        TenantId tenantId, GrantId grantId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var key = (tenantId.Value, grantId.Value);
            return Task.FromResult(_grants.TryGetValue(key, out var grant)
                ? new VersionedAccessGrant(grant, _ownerVersions[key])
                : null);
        }
    }
    public Task<AccessGrant?> FindBySourceReferenceAsync(TenantId tenantId, string sourceReference, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult(_sources.TryGetValue((tenantId.Value, sourceReference), out var id) ? _grants[(tenantId.Value, id)] : null); }
    public Task<IReadOnlyList<AccessGrant>> FindByPrincipalAsync(TenantId tenantId, ActorId principal, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult<IReadOnlyList<AccessGrant>>(_grants.Values.Where(g => g.TenantId == tenantId && g.Subject == principal).ToArray()); }
    public Task<IReadOnlyList<VersionedAccessGrant>> FindVersionedByPrincipalAsync(
        TenantId tenantId, ActorId principal, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult<IReadOnlyList<VersionedAccessGrant>>(_grants
                .Where(item => item.Value.TenantId == tenantId && item.Value.Subject == principal)
                .Select(item => new VersionedAccessGrant(item.Value, _ownerVersions[item.Key]))
                .ToArray());
    }
    public Task<IReadOnlyList<AccessGrant>> SnapshotAsync(TenantId tenantId, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult<IReadOnlyList<AccessGrant>>(_grants.Values.Where(g => g.TenantId == tenantId).ToArray()); }
    public Task<bool> HasAdministratorGrantEverAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(_grants.Values.Any(g => g.Role == RoleReference.Administrator));
    }
    public Task<AccessGrant?> ChangeValidityAsync(TenantId tenantId, GrantId grantId, GrantValidity validity, ActorId changedBy, GrantReason reason, CancellationToken ct = default) =>
        Mutate(tenantId, grantId, g => g.Status == GrantStatus.Revoked
            ? throw new InvalidOperationException("A revoked grant's validity is immutable.")
            : g with { Validity = validity, ValidityChange = new GrantValidityChangeEvidence(changedBy, reason) });
    public Task<AccessGrant?> RecordReviewAsync(TenantId tenantId, GrantId grantId, DateTimeOffset reviewedAt, ActorId reviewedBy, CancellationToken ct = default) =>
        Mutate(tenantId, grantId, g => g.Status == GrantStatus.Revoked
            ? throw new InvalidOperationException("A revoked grant cannot be reviewed.")
            : g with { LastReviewedAt = reviewedAt, LastReviewedBy = reviewedBy });
    public Task<AccessGrant?> RevokeAsync(TenantId tenantId, GrantId grantId, GrantRevocation revocation, CancellationToken ct = default) =>
        Mutate(tenantId, grantId, g => g.Status != GrantStatus.Revoked
            ? g with { Status = GrantStatus.Revoked, Revocation = revocation }
            : g.Revocation == revocation
                ? g
                : throw new InvalidOperationException("A revoked grant cannot replace its revocation evidence."));

    /// <inheritdoc />
    public Task<AdministratorHandover?> HandoverAdministratorAsync(
        TenantId tenantId, GrantId currentGrantId, AccessGrant successor, GrantRevocation revocation,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(successor);
        ArgumentNullException.ThrowIfNull(revocation);
        if (tenantId != successor.TenantId)
            throw new ArgumentException("Grant tenant does not match store tenant.", nameof(successor));
        // L618. Both legs under the one store-wide lock, and every refusal is raised BEFORE the first write,
        // so a failed handover leaves the Administrator population exactly as it was.
        lock (_gate)
        {
            var currentKey = (tenantId.Value, currentGrantId.Value);
            if (!_grants.TryGetValue(currentKey, out var current)) return Task.FromResult<AdministratorHandover?>(null);
            if (current.Status == GrantStatus.Revoked)
                throw new InvalidOperationException("A revoked grant cannot be handed over.");
            var successorKey = (tenantId.Value, successor.GrantId.Value);
            if (_grants.ContainsKey(successorKey))
                throw new InvalidOperationException("A grant id cannot replace immutable grant evidence.");
            var revoked = current with { Status = GrantStatus.Revoked, Revocation = revocation };

            // not_last_administrator() (L619) over a population that already carries the successor.
            LastAdministratorGuard.EnsureNotLastAdministrator(
                current, revoked,
                _grants.Values.Where(g => g.TenantId == tenantId).Append(successor).ToArray());

            _grants.Add(successorKey, successor);
            _ownerVersions.Add(successorKey, 1);
            _sourceByGrant.Add(successorKey, null);
            AdvanceEpoch(successor);
            _grants[currentKey] = revoked;
            _ownerVersions[currentKey] = checked(_ownerVersions[currentKey] + 1);
            AdvanceEpoch(revoked);
            return Task.FromResult<AdministratorHandover?>(new AdministratorHandover(successor, revoked));
        }
    }

    public Task<long?> ReadAuthorizationEpochAsync(
        TenantId tenantId, ActorId principal, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<long?>(_epochs.GetValueOrDefault((tenantId.Value, principal.Value)));
    }

    private Task<AccessGrant?> Mutate(TenantId tenantId, GrantId grantId, Func<AccessGrant, AccessGrant> change)
    {
        lock (_gate)
        {
            var key = (tenantId.Value, grantId.Value);
            if (!_grants.TryGetValue(key, out var grant)) return Task.FromResult<AccessGrant?>(null);
            var changed = change(grant);
            if (changed != grant)
            {
                // not_last_administrator() (L619). Read and write under the one store-wide lock.
                if (LastAdministratorGuard.Guards(grant))
                    LastAdministratorGuard.EnsureNotLastAdministrator(
                        grant, changed, _grants.Values.Where(g => g.TenantId == tenantId).ToArray());
                _grants[key] = changed;
                _ownerVersions[key] = checked(_ownerVersions[key] + 1);
                AdvanceEpoch(changed);
            }
            return Task.FromResult<AccessGrant?>(changed);
        }
    }

    private void AdvanceEpoch(AccessGrant grant)
    {
        var key = (grant.TenantId.Value, grant.Subject.Value);
        _epochs[key] = checked(_epochs.GetValueOrDefault(key) + 1);
    }
}
