using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>The existing record kind from which a read-side grant was projected.</summary>
public enum GrantProjectionSource
{
    InstallationAccessGrantRecord,
    AccessGrant,
    TeamMembershipPermissions,
}

/// <summary>The structural home of a projected grant.</summary>
public enum GrantProjectionScopeKind
{
    Installation,
    Tenant,
}

/// <summary>
/// Structural grant scope. Installation scope has no editable identifier; tenant scope carries its
/// tenant home and the existing access-grant record filter.
/// </summary>
public sealed record ProjectedAuthorityScope(
    GrantProjectionScopeKind Kind,
    TenantId? TenantId,
    ScopeExpression? RecordScope)
{
    public static ProjectedAuthorityScope Installation { get; } =
        new(GrantProjectionScopeKind.Installation, TenantId: null, RecordScope: null);

    public static ProjectedAuthorityScope ForTenant(TenantId tenantId, ScopeExpression recordScope) =>
        new(GrantProjectionScopeKind.Tenant, tenantId, recordScope);
}

/// <summary>
/// ADR 0065 clause 7's grant shape, plus read-side provenance. Nullable metadata identifies fields
/// the projected legacy row does not store; the projection never fabricates a reason or review.
/// </summary>
/// <remarks>
/// Ticket 293 slice 5 — the <c>LegacyPermissions</c> field is GONE. It carried a raw permission set
/// projected out of a legacy row (an installation grant's JSON, or a membership edge's
/// <c>EffectivePermissions</c>), and its single reader was this file's own disagreement diagnostic: it
/// decided nothing, and a set that decides nothing is a set a future reader can mistake for authority.
/// A projected grant's authority is its <see cref="Role"/> and <see cref="Grant"/> provenance; what a
/// principal may actually do is decided by <c>AuthorizationGate</c> over the grant store, never here.
/// </remarks>
public sealed record ProjectedGrant(
    string PrincipalId,
    RoleReference? Role,
    GrantProvenance? Grant,
    ProjectedAuthorityScope Scope,
    string? GrantedBy,
    string? Reason,
    DateTimeOffset? GrantedAt,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastReviewedAt,
    GrantProjectionSource Source,
    string SourceRecordId);

/// <summary>The portions of overlapping projected grants that disagree.</summary>
[Flags]
public enum GrantProjectionDisagreementKind
{
    None = 0,
    PermissionSet = 1,
    Validity = 2,
}

/// <summary>A structured defect finding between two source rows for one principal and scope.</summary>
public sealed record GrantProjectionDisagreement(
    string PrincipalId,
    ProjectedAuthorityScope Scope,
    GrantProjectionSource FirstSource,
    string FirstSourceRecordId,
    GrantProjectionSource SecondSource,
    string SecondSourceRecordId,
    GrantProjectionDisagreementKind Kind);

/// <summary>The unmerged projected rows and any disagreements found between them.</summary>
public sealed record GrantProjectionResolution(
    IReadOnlyList<ProjectedGrant> Grants,
    IReadOnlyList<GrantProjectionDisagreement> Disagreements);

/// <summary>Pure read-side projections from ADR 0066's three existing authority records.</summary>
public static class GrantRecordProjections
{
    public static ProjectedGrant Project(InstallationAccessGrantRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new ProjectedGrant(
            PrincipalId: record.AccountId,
            Role: null,
            Grant: null,
            Scope: ProjectedAuthorityScope.Installation,
            GrantedBy: record.IssuerId,
            Reason: null,
            GrantedAt: record.CreatedAtUtc,
            ValidFrom: record.CreatedAtUtc,
            ExpiresAt: null,
            RevokedAt: record.Status == InstallationAccessGrantStatus.Revoked
                ? record.UpdatedAtUtc
                : null,
            LastReviewedAt: null,
            Source: GrantProjectionSource.InstallationAccessGrantRecord,
            SourceRecordId: record.GrantId);
    }

    public static ProjectedGrant Project(AccessGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return new ProjectedGrant(
            PrincipalId: grant.Subject.Value,
            Role: grant.Role,
            Grant: grant.Grant,
            Scope: ProjectedAuthorityScope.ForTenant(grant.TenantId, grant.Scope),
            GrantedBy: grant.GrantedBy.Value,
            Reason: grant.Grant.Reason.Code,
            GrantedAt: grant.GrantedAt,
            ValidFrom: grant.Validity.ValidFrom,
            ExpiresAt: grant.Validity.ValidTo,
            RevokedAt: grant.Revocation?.RevokedAt,
            LastReviewedAt: grant.LastReviewedAt,
            Source: GrantProjectionSource.AccessGrant,
            SourceRecordId: grant.GrantId.ToString());
    }

    public static ProjectedGrant Project(ActorId principal, TeamMembership membership)
    {
        ArgumentNullException.ThrowIfNull(membership);
        var admission = membership.AdmissionSignature;
        var tenantId = new TenantId(membership.TeamId.ToString("D"));

        return new ProjectedGrant(
            PrincipalId: principal.Value,
            Role: null,
            Grant: null,
            Scope: ProjectedAuthorityScope.ForTenant(tenantId, ScopeExpression.Parse("/")),
            GrantedBy: admission?.AdmittedByPartyId,
            Reason: null,
            GrantedAt: admission?.IssuedAt,
            ValidFrom: admission?.IssuedAt,
            ExpiresAt: null,
            RevokedAt: null,
            LastReviewedAt: null,
            Source: GrantProjectionSource.TeamMembershipPermissions,
            SourceRecordId: membership.TeamId.ToString("D"));
    }
}

/// <summary>Read-only access to installation grant rows for one projected principal.</summary>
public interface IInstallationAccessGrantProjectionReader
{
    Task<IReadOnlyList<InstallationAccessGrantRecord>> FindByPrincipalAsync(
        string principalId,
        CancellationToken cancellationToken = default);
}

/// <summary>EF read-side implementation over the existing installation authority database.</summary>
public sealed class InstallationAccessGrantProjectionReader(
    IDbContextFactory<NodeLocalInstallationIdentityDbContext> contextFactory)
    : IInstallationAccessGrantProjectionReader
{
    public async Task<IReadOnlyList<InstallationAccessGrantRecord>> FindByPrincipalAsync(
        string principalId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.InstallationAccessGrants.AsNoTracking()
            .Where(record => record.AccountId == principalId)
            .ToArrayAsync(cancellationToken);
    }
}

/// <summary>
/// The single ADR 0066 migration-step-2 resolver. It reads all three record kinds, returns their
/// provenance-preserving union, and reports disagreement without merging or preferring a source.
/// </summary>
public sealed partial class GrantProjectionResolver(
    IInstallationAccessGrantProjectionReader installationGrants,
    IGrantStore tenantGrants,
    ITeamRegistry memberships,
    ILogger<GrantProjectionResolver> logger)
{
    public async Task<GrantProjectionResolution> ResolveAsync(
        string principalId,
        TenantId tenantId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        var actor = new ActorId(principalId);
        var installationRows = await installationGrants.FindByPrincipalAsync(principalId, cancellationToken);
        var tenantRows = await tenantGrants.FindByPrincipalAsync(tenantId, actor, cancellationToken);
        var membershipRows = await memberships.GetMembershipsAsync(actor, cancellationToken);

        var grants = new List<ProjectedGrant>(installationRows.Count + tenantRows.Count + membershipRows.Count);
        grants.AddRange(installationRows.Select(GrantRecordProjections.Project));
        grants.AddRange(tenantRows.Select(GrantRecordProjections.Project));
        grants.AddRange(membershipRows
            .Where(membership => string.Equals(
                membership.TeamId.ToString("D"),
                tenantId.Value,
                StringComparison.OrdinalIgnoreCase))
            .Select(membership => GrantRecordProjections.Project(actor, membership)));

        var disagreements = FindDisagreements(grants);
        foreach (var disagreement in disagreements)
        {
            GrantProjectionLog.Disagreement(
                logger,
                disagreement.PrincipalId,
                FormatScope(disagreement.Scope),
                disagreement.FirstSource,
                disagreement.FirstSourceRecordId,
                disagreement.SecondSource,
                disagreement.SecondSourceRecordId,
                disagreement.Kind);
        }

        return new GrantProjectionResolution(grants, disagreements);
    }

    private static IReadOnlyList<GrantProjectionDisagreement> FindDisagreements(
        IReadOnlyList<ProjectedGrant> grants)
    {
        var findings = new List<GrantProjectionDisagreement>();
        for (var firstIndex = 0; firstIndex < grants.Count; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < grants.Count; secondIndex++)
            {
                var first = grants[firstIndex];
                var second = grants[secondIndex];
                if (first.Source == second.Source ||
                    !string.Equals(first.PrincipalId, second.PrincipalId, StringComparison.Ordinal) ||
                    !ScopesEqual(first.Scope, second.Scope))
                {
                    continue;
                }

                var kind = GrantProjectionDisagreementKind.None;
                // Ticket 293 slice 5 — two rows disagree about authority when they name different roles.
                // The raw-set comparison is gone with LegacyPermissions; the set was never the authority.
                if (first.Role != second.Role)
                {
                    kind |= GrantProjectionDisagreementKind.PermissionSet;
                }

                if (first.ValidFrom != second.ValidFrom ||
                    first.ExpiresAt != second.ExpiresAt ||
                    first.RevokedAt != second.RevokedAt)
                {
                    kind |= GrantProjectionDisagreementKind.Validity;
                }

                if (kind != GrantProjectionDisagreementKind.None)
                {
                    findings.Add(new GrantProjectionDisagreement(
                        first.PrincipalId,
                        first.Scope,
                        first.Source,
                        first.SourceRecordId,
                        second.Source,
                        second.SourceRecordId,
                        kind));
                }
            }
        }

        return findings;
    }

    private static bool ScopesEqual(ProjectedAuthorityScope first, ProjectedAuthorityScope second)
    {
        if (first.Kind != second.Kind || first.TenantId != second.TenantId)
        {
            return false;
        }

        if (first.RecordScope is null || second.RecordScope is null)
        {
            return first.RecordScope is null && second.RecordScope is null;
        }

        return first.RecordScope == second.RecordScope;
    }

    private static string FormatScope(ProjectedAuthorityScope scope) => scope.Kind switch
    {
        GrantProjectionScopeKind.Installation => "installation",
        _ => $"tenant:{scope.TenantId}",
    };

    private static partial class GrantProjectionLog
    {
        [LoggerMessage(
            EventId = 66,
            Level = LogLevel.Warning,
            Message = "ADR 0066 grant projection disagreement for principal {PrincipalId} in {Scope}: " +
                "{FirstSource}/{FirstSourceRecordId} versus {SecondSource}/{SecondSourceRecordId}; " +
                "differences={Kind}")]
        public static partial void Disagreement(
            ILogger logger,
            string principalId,
            string scope,
            GrantProjectionSource firstSource,
            string firstSourceRecordId,
            GrantProjectionSource secondSource,
            string secondSourceRecordId,
            GrantProjectionDisagreementKind kind);
    }
}
