using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>Decision-bearing boundary for an admitted admin grant revocation.</summary>
internal interface IAuthorizedGrantRevocationWriter
{
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
}

/// <summary>Validates the carried decision against the write before reaching the raw grant store.</summary>
internal sealed class AuthorizedGrantRevocationWriter(IGrantStore grants)
    : IAuthorizedGrantRevocationWriter
{
    private static readonly AuthorizationOperation MembersManage =
        AuthorizationOperation.Parse(TeamRolePermissions.MembersManage);

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
}
