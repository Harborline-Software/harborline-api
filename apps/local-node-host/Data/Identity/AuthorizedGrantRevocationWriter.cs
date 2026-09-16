using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.Search;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>Decision-bearing boundary for an admitted admin grant revocation.</summary>
internal interface IAuthorizedGrantRevocationWriter
{
    Task<AccessGrant?> RecordReviewAsync(TenantId tenant, GrantId grant, DateTimeOffset at, ActorId actor,
        AuthorizationDecision admittedDecision, CancellationToken cancellationToken = default);

    /// <summary>Atomically narrows a grant's scope without changing its role or subject.</summary>
    Task<GrantScopeNarrowing?> NarrowScopeAsync(
        TenantId tenant, GrantId current, ScopeExpression narrowed, GrantId successor,
        GrantRevocation revocation, AuthorizationDecision admittedDecision,
        CancellationToken cancellationToken = default);

    Task<AccessGrant?> RevokeAsync(
        TenantId tenant,
        GrantId grant,
        GrantRevocation revocation,
        AuthorizationDecision admittedDecision,
        CancellationToken cancellationToken = default);

    /// <summary>Ledger L618 — the atomic Administrator handover, under the same admitted decision.</summary>
    Task<AdministratorHandover?> HandoverAsync(
        TenantId tenant,
        GrantId current,
        AccessGrant successor,
        GrantRevocation revocation,
        AuthorizationDecision admittedDecision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ticket 362 — the atomic revoke-and-reissue that narrows a member's conferred grant, under the same
    /// admitted decision.
    /// </summary>
    Task<AdmissionGrantNarrowing?> NarrowAsync(
        TenantId tenant,
        GrantId current,
        PermissionSet narrowed,
        GrantRevocation revocation,
        Guid correlationId,
        AuthorizationDecision admittedDecision,
        CancellationToken cancellationToken = default);
}

/// <summary>Validates the carried decision against the write before reaching the raw grant store.</summary>
internal sealed class AuthorizedGrantRevocationWriter(
    IGrantStore grants,
    IDbContextFactory<NodeLocalSearchDbContext> grantFactory) : IAuthorizedGrantRevocationWriter
{
    private static readonly AuthorizationOperation MembersManage =
        AuthorizationOperation.Parse(TeamRolePermissions.MembersManage);

    public Task<AccessGrant?> RecordReviewAsync(TenantId tenant, GrantId grant, DateTimeOffset at, ActorId actor,
        AuthorizationDecision admittedDecision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admittedDecision);
        admittedDecision.RequireAllowedReaction(MembersManage, tenant, "members", grant.ToString());
        if (actor != admittedDecision.Request.Principal || at != admittedDecision.DecidedAt)
            throw new ArgumentException("Review attribution must match the admitted decision.", nameof(admittedDecision));
        return grants.RecordReviewAsync(tenant, grant, at, actor, cancellationToken);
    }

    public Task<GrantScopeNarrowing?> NarrowScopeAsync(
        TenantId tenant, GrantId current, ScopeExpression narrowed, GrantId successor,
        GrantRevocation revocation, AuthorizationDecision admittedDecision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admittedDecision);
        admittedDecision.RequireAllowedReaction(MembersManage, tenant, "members", current.ToString());
        return grants.NarrowScopeAsync(tenant, current, narrowed, successor, revocation, cancellationToken);
    }

    public Task<AccessGrant?> RevokeAsync(
        TenantId tenant,
        GrantId grant,
        GrantRevocation revocation,
        AuthorizationDecision admittedDecision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admittedDecision);
        admittedDecision.RequireAllowedReaction(MembersManage, tenant, "members", grant.ToString());
        return grants.RevokeAsync(tenant, grant, revocation, cancellationToken);
    }

    public Task<AdministratorHandover?> HandoverAsync(
        TenantId tenant,
        GrantId current,
        AccessGrant successor,
        GrantRevocation revocation,
        AuthorizationDecision admittedDecision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admittedDecision);
        admittedDecision.RequireAllowedReaction(MembersManage, tenant, "members", current.ToString());
        return grants.HandoverAdministratorAsync(tenant, current, successor, revocation, cancellationToken);
    }

    public Task<AdmissionGrantNarrowing?> NarrowAsync(
        TenantId tenant,
        GrantId current,
        PermissionSet narrowed,
        GrantRevocation revocation,
        Guid correlationId,
        AuthorizationDecision admittedDecision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admittedDecision);
        admittedDecision.RequireAllowedReaction(MembersManage, tenant, "members", current.ToString());
        // The ONE admission-grant derivation lives on the configuration store; it is built over the grant
        // factory this writer already holds, exactly as WebAdmittedMemberAtlasBridge does for the live
        // conferral -- no new constructor seam on the authority and no second derivation anywhere. The role
        // vocabulary is empty on purpose: StageAdmissionGrantAsync always supplies the admission's own.
        return new NodeEfAuthorizationConfigurationStore(grantFactory, new InMemoryRoleVocabulary([]))
            .NarrowAdmissionGrantAsync(tenant, current, narrowed, revocation, correlationId, cancellationToken);
    }
}
