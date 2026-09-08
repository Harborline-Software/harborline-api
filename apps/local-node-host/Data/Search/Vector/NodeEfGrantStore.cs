using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Microsoft.EntityFrameworkCore;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;

namespace Harborline.Api.LocalNodeHost.Data.Search.Vector;

public sealed class NodeEfGrantStore(IDbContextFactory<NodeLocalSearchDbContext> contextFactory)
    : IGrantStore, IGrantAuthorizationEpochReader
{
    public async Task<AccessGrant> AppendAsync(TenantId tenantId, AccessGrant grant, string? sourceReference = null, CancellationToken ct = default)
    {
        if (tenantId != grant.TenantId) throw new InvalidOperationException("Cross-tenant grant write rejected.");
        await using var ctx = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        AccessGrant? persisted = null;
        await HomeEpochFenceTransaction.RunAsync(ctx, async () =>
        {
            if (sourceReference is not null)
            {
                var duplicate = await ctx.Grants.AsNoTracking().FirstOrDefaultAsync(
                    row => row.TenantId == tenantId.Value && row.SourceReference == sourceReference, ct)
                    .ConfigureAwait(false);
                if (duplicate is not null)
                {
                    var existing = ToGrant(duplicate);
                    if (existing != grant)
                        throw new InvalidOperationException("A source reference cannot replace immutable grant evidence.");
                    persisted = existing;
                    return;
                }
            }
            var byId = await ctx.Grants.AsNoTracking().FirstOrDefaultAsync(
                row => row.TenantId == tenantId.Value && row.GrantId == grant.GrantId.ToString(), ct)
                .ConfigureAwait(false);
            if (byId is not null)
            {
                var existing = ToGrant(byId);
                if (existing != grant)
                    throw new InvalidOperationException("A grant id cannot replace immutable grant evidence.");
                if (!string.Equals(byId.SourceReference, sourceReference, StringComparison.Ordinal))
                    throw new InvalidOperationException("A grant id cannot replace its source reference.");
                persisted = existing;
                return;
            }
            ctx.Grants.Add(ToRow(grant, sourceReference));
            await AdvanceEpochAsync(ctx, grant.TenantId, grant.Subject, ct).ConfigureAwait(false);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            persisted = grant;
        }, ct).ConfigureAwait(false);
        return persisted!;
    }

    public async Task<AccessGrant?> FindAsync(TenantId tenantId, GrantId grantId, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await ctx.Grants.AsNoTracking().FirstOrDefaultAsync(
            g => g.TenantId == tenantId.Value && g.GrantId == grantId.ToString(), ct).ConfigureAwait(false);
        return row is null ? null : ToGrant(row);
    }

    public async Task<VersionedAccessGrant?> FindVersionedAsync(
        TenantId tenantId, GrantId grantId, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await ctx.Grants.AsNoTracking().FirstOrDefaultAsync(
            g => g.TenantId == tenantId.Value && g.GrantId == grantId.ToString(), ct).ConfigureAwait(false);
        return row is null ? null : new VersionedAccessGrant(ToGrant(row), row.OwnerVersion);
    }

    public async Task<AccessGrant?> FindBySourceReferenceAsync(TenantId tenantId, string sourceReference, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await ctx.Grants.AsNoTracking().FirstOrDefaultAsync(
            g => g.TenantId == tenantId.Value && g.SourceReference == sourceReference, ct).ConfigureAwait(false);
        return row is null ? null : ToGrant(row);
    }

    public async Task<IReadOnlyList<AccessGrant>> FindByPrincipalAsync(TenantId tenantId, ActorId principal, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return (await ctx.Grants.AsNoTracking().Where(g => g.TenantId == tenantId.Value && g.SubjectId == principal.Value)
            .ToArrayAsync(ct).ConfigureAwait(false)).Select(ToGrant).ToArray();
    }

    public async Task<IReadOnlyList<VersionedAccessGrant>> FindVersionedByPrincipalAsync(
        TenantId tenantId, ActorId principal, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return (await ctx.Grants.AsNoTracking()
                .Where(g => g.TenantId == tenantId.Value && g.SubjectId == principal.Value)
                .ToArrayAsync(ct).ConfigureAwait(false))
            .Select(row => new VersionedAccessGrant(ToGrant(row), row.OwnerVersion))
            .ToArray();
    }

    public async Task<IReadOnlyList<AccessGrant>> SnapshotAsync(TenantId tenantId, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return (await ctx.Grants.AsNoTracking().Where(g => g.TenantId == tenantId.Value)
            .ToArrayAsync(ct).ConfigureAwait(false)).Select(ToGrant).ToArray();
    }

    public async Task<bool> HasAdministratorGrantEverAsync(CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await ctx.Grants.AsNoTracking().AnyAsync(
            row => row.RoleVocabulary == RoleReference.Administrator.Vocabulary
                && row.RoleName == RoleReference.Administrator.Name,
            ct).ConfigureAwait(false);
    }

    public Task<AccessGrant?> ChangeValidityAsync(TenantId tenantId, GrantId grantId, GrantValidity validity, ActorId changedBy, GrantReason reason, CancellationToken ct = default) =>
        MutateAsync(tenantId, grantId, g => g.Status == GrantStatus.Revoked
            ? throw new InvalidOperationException("A revoked grant's validity is immutable.")
            : g with { Validity = validity, ValidityChange = new GrantValidityChangeEvidence(changedBy, reason) }, ct);
    public Task<AccessGrant?> RecordReviewAsync(TenantId tenantId, GrantId grantId, DateTimeOffset reviewedAt, ActorId reviewedBy, CancellationToken ct = default) =>
        MutateAsync(tenantId, grantId, g => g.Status == GrantStatus.Revoked
            ? throw new InvalidOperationException("A revoked grant cannot be reviewed.")
            : g with { LastReviewedAt = reviewedAt, LastReviewedBy = reviewedBy }, ct);
    public Task<AccessGrant?> RevokeAsync(TenantId tenantId, GrantId grantId, GrantRevocation revocation, CancellationToken ct = default) =>
        MutateAsync(tenantId, grantId, g => g.Status != GrantStatus.Revoked
            ? g with { Status = GrantStatus.Revoked, Revocation = revocation }
            : g.Revocation == revocation
                ? g
                : throw new InvalidOperationException("A revoked grant cannot replace its revocation evidence."), ct);

    /// <inheritdoc />
    public async Task<AdministratorHandover?> HandoverAdministratorAsync(
        TenantId tenantId, GrantId currentGrantId, AccessGrant successor, GrantRevocation revocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(successor);
        ArgumentNullException.ThrowIfNull(revocation);
        if (tenantId != successor.TenantId) throw new InvalidOperationException("Cross-tenant grant write rejected.");
        await using var ctx = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        AdministratorHandover? result = null;
        // L618. Both legs are staged inside the ONE BEGIN IMMEDIATE fence transaction and committed by a
        // single SaveChangesAsync, so a failure anywhere -- the guard, a duplicate successor id, the write
        // itself -- rolls the whole handover back and leaves the Administrator population unchanged.
        await HomeEpochFenceTransaction.RunAsync(ctx, async () =>
        {
            var row = await ctx.Grants.FirstOrDefaultAsync(
                g => g.TenantId == tenantId.Value && g.GrantId == currentGrantId.ToString(), ct)
                .ConfigureAwait(false);
            if (row is null) return;
            var current = ToGrant(row);
            if (current.Status == GrantStatus.Revoked)
                throw new InvalidOperationException("A revoked grant cannot be handed over.");
            var revoked = current with { Status = GrantStatus.Revoked, Revocation = revocation };

            ctx.Grants.Add(ToRow(successor, sourceReference: null));
            await AdvanceEpochAsync(ctx, successor.TenantId, successor.Subject, ct).ConfigureAwait(false);

            // not_last_administrator() (L619) over a population that already carries the successor, read
            // under the write lock this transaction holds.
            var population = (await ctx.Grants.AsNoTracking().Where(g => g.TenantId == tenantId.Value)
                .ToArrayAsync(ct).ConfigureAwait(false)).Select(ToGrant).Append(successor);
            LastAdministratorGuard.EnsureNotLastAdministrator(current, revoked, population);

            ctx.Entry(row).CurrentValues.SetValues(
                ToRow(revoked, row.SourceReference, checked(row.OwnerVersion + 1)));
            await AdvanceEpochAsync(ctx, revoked.TenantId, revoked.Subject, ct).ConfigureAwait(false);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            result = new AdministratorHandover(successor, revoked);
        }, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<AccessGrant?> MutateAsync(TenantId tenantId, GrantId grantId, Func<AccessGrant, AccessGrant> change, CancellationToken ct)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        AccessGrant? result = null;
        await HomeEpochFenceTransaction.RunAsync(ctx, async () =>
        {
            var row = await ctx.Grants.FirstOrDefaultAsync(
                g => g.TenantId == tenantId.Value && g.GrantId == grantId.ToString(), ct)
                .ConfigureAwait(false);
            if (row is null) return;
            var current = ToGrant(row);
            var changed = change(current);
            result = changed;
            if (changed == current) return;
            // not_last_administrator() (L619). The population read runs inside the same BEGIN IMMEDIATE
            // transaction as the write, under the held write lock, so the count and the mutation are one act.
            if (LastAdministratorGuard.Guards(current))
                LastAdministratorGuard.EnsureNotLastAdministrator(current, changed, (await ctx.Grants
                    .AsNoTracking().Where(g => g.TenantId == tenantId.Value).ToArrayAsync(ct)
                    .ConfigureAwait(false)).Select(ToGrant));
            var nextOwnerVersion = checked(row.OwnerVersion + 1);
            ctx.Entry(row).CurrentValues.SetValues(ToRow(changed, row.SourceReference, nextOwnerVersion));
            await AdvanceEpochAsync(ctx, changed.TenantId, changed.Subject, ct).ConfigureAwait(false);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return result;
    }

    public async Task<long?> ReadAuthorizationEpochAsync(
        TenantId tenantId, ActorId principal, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await ctx.GrantAuthorizationEpochs.AsNoTracking()
            .Where(row => row.TenantId == tenantId.Value && row.PrincipalId == principal.Value)
            .Select(row => (long?)row.AuthorizationEpoch)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
    }

    /// <remarks>
    /// Rows already staged in this context are consulted BEFORE the database: a handover advances the epoch
    /// twice in one transaction, and a second query would miss the first leg's pending insert and add a
    /// duplicate key.
    /// </remarks>
    internal static async Task AdvanceEpochAsync(NodeLocalSearchDbContext ctx, TenantId tenant, ActorId subject, CancellationToken ct)
    {
        var row = ctx.GrantAuthorizationEpochs.Local.FirstOrDefault(
                x => x.TenantId == tenant.Value && x.PrincipalId == subject.Value)
            ?? await ctx.GrantAuthorizationEpochs.FirstOrDefaultAsync(
                x => x.TenantId == tenant.Value && x.PrincipalId == subject.Value, ct).ConfigureAwait(false);
        if (row is null) ctx.GrantAuthorizationEpochs.Add(new GrantAuthorizationEpochRow
            { TenantId = tenant.Value, PrincipalId = subject.Value, AuthorizationEpoch = 1 });
        else row.AuthorizationEpoch++;
        var tenantVersion = ctx.AuthorizationTenantVersions.Local.FirstOrDefault(x => x.TenantId == tenant.Value)
            ?? await ctx.AuthorizationTenantVersions.FirstOrDefaultAsync(
                x => x.TenantId == tenant.Value, ct).ConfigureAwait(false);
        if (tenantVersion is null) ctx.AuthorizationTenantVersions.Add(new AuthorizationTenantVersionRow
            { TenantId = tenant.Value, Version = 1 });
        else tenantVersion.Version++;
    }

    internal static GrantRow ToRow(AccessGrant grant, string? sourceReference, long ownerVersion = 1) => new()
    {
        GrantId = grant.GrantId.ToString(), TenantId = grant.TenantId.Value, SubjectId = grant.Subject.Value,
        RoleVocabulary = grant.Role.Vocabulary, RoleName = grant.Role.Name, ScopeType = (int)grant.Scope.Type,
        ScopeValue = grant.Scope.Value, Residency = (int)grant.Residency,
        ValidityFromUnixMs = grant.Validity.ValidFrom.ToUnixTimeMilliseconds(),
        ValidityUntilUnixMs = grant.Validity.ValidTo?.ToUnixTimeMilliseconds(), Status = (int)grant.Status,
        GranterKind = (int)grant.GranterKind, GrantedBy = grant.GrantedBy.Value,
        GrantedAtUnixMs = grant.GrantedAt.ToUnixTimeMilliseconds(), Source = (int)grant.Grant.Source,
        ReasonCode = grant.Grant.Reason.Code, ReasonReference = grant.Grant.Reason.Reference,
        Approver = grant.Grant.Approver.Value, LastReviewedAtUnixMs = grant.LastReviewedAt.ToUnixTimeMilliseconds(),
        LastReviewedBy = grant.LastReviewedBy?.Value,
        ValidityChangedBy = grant.ValidityChange?.ChangedBy.Value,
        ValidityChangeReasonCode = grant.ValidityChange?.Reason.Code,
        ValidityChangeReasonReference = grant.ValidityChange?.Reason.Reference,
        RevokedBy = grant.Revocation?.RevokedBy.Value, RevokedAtUnixMs = grant.Revocation?.RevokedAt.ToUnixTimeMilliseconds(),
        RevocationReasonCode = grant.Revocation?.Reason.Code, RevocationReasonReference = grant.Revocation?.Reason.Reference,
        SourceReference = sourceReference, OwnerVersion = ownerVersion,
    };

    private static AccessGrant ToGrant(GrantRow row)
    {
        GrantRevocation? revocation = row.RevokedAtUnixMs is null ? null : new GrantRevocation(
            new ActorId(row.RevokedBy!), DateTimeOffset.FromUnixTimeMilliseconds(row.RevokedAtUnixMs.Value),
            new GrantReason(row.RevocationReasonCode!, row.RevocationReasonReference));
        return new AccessGrant(new GrantId(Guid.Parse(row.GrantId)), TenantId.FromString(row.TenantId),
            new ActorId(row.SubjectId), new RoleReference(row.RoleVocabulary, row.RoleName),
            ScopeExpression.Parse(row.ScopeValue), (GrantResidency)row.Residency,
            new GrantValidity(DateTimeOffset.FromUnixTimeMilliseconds(row.ValidityFromUnixMs),
                row.ValidityUntilUnixMs is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(row.ValidityUntilUnixMs.Value)),
            (GranterKind)row.GranterKind, new ActorId(row.GrantedBy),
            DateTimeOffset.FromUnixTimeMilliseconds(row.GrantedAtUnixMs),
            new GrantProvenance((GrantSourceKind)row.Source, new GrantReason(row.ReasonCode, row.ReasonReference), new ActorId(row.Approver)),
            DateTimeOffset.FromUnixTimeMilliseconds(row.LastReviewedAtUnixMs), (GrantStatus)row.Status, revocation,
            row.ValidityChangedBy is null ? null : new GrantValidityChangeEvidence(
                new ActorId(row.ValidityChangedBy),
                new GrantReason(row.ValidityChangeReasonCode!, row.ValidityChangeReasonReference)),
            row.LastReviewedBy is null ? (ActorId?)null : new ActorId(row.LastReviewedBy));
    }

}
