using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>
/// One immutable, live-revalidated selected-session principal for exactly one HTTP request.
/// </summary>
/// <remarks>
/// The browser cannot supply any property on this object. The selected-session authority reloads
/// every owning account, membership, Party, trust, grant-version, authorization-epoch, revocation,
/// and TTL fact before construction. Downstream request facades consume this same instance through
/// <c>HttpContext.Features</c>; it is never stored in a singleton, ambient, or AsyncLocal context holder.
/// </remarks>
internal sealed class SelectedSessionRequestPrincipal
{
    internal SelectedSessionRequestPrincipal(
        string accountId,
        TenantId tenantId,
        PrincipalUserId principalUserId,
        CanonicalPartyReference canonicalParty,
        string membershipId,
        long membershipOwnerVersion,
        IReadOnlyList<PinnedGrantOwnerVersion> pinnedGrantOwnerVersions,
        long authorizationEpoch,
        string sessionCorrelationId,
        string coordinationCorrelationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(membershipId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionCorrelationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(coordinationCorrelationId);
        if (tenantId == default)
        {
            throw new ArgumentException("A selected-session tenant is required.", nameof(tenantId));
        }
        if (membershipOwnerVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(membershipOwnerVersion));
        }
        if (authorizationEpoch <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(authorizationEpoch));
        }
        ArgumentNullException.ThrowIfNull(pinnedGrantOwnerVersions);
        if (pinnedGrantOwnerVersions.Count != 1)
        {
            throw new ArgumentException(
                "Exactly one live grant pin is required.",
                nameof(pinnedGrantOwnerVersions));
        }

        AccountId = accountId;
        TenantId = tenantId;
        PrincipalUserId = principalUserId;
        CanonicalParty = canonicalParty;
        MembershipId = membershipId;
        MembershipOwnerVersion = membershipOwnerVersion;
        PinnedGrantOwnerVersions = Array.AsReadOnly(pinnedGrantOwnerVersions.ToArray());
        AuthorizationEpoch = authorizationEpoch;
        SessionCorrelationId = sessionCorrelationId;
        CoordinationCorrelationId = coordinationCorrelationId;
    }

    internal string AccountId { get; }

    internal TenantId TenantId { get; }

    internal PrincipalUserId PrincipalUserId { get; }

    internal CanonicalPartyReference CanonicalParty { get; }

    internal string MembershipId { get; }

    internal long MembershipOwnerVersion { get; }

    internal IReadOnlyList<PinnedGrantOwnerVersion> PinnedGrantOwnerVersions { get; }

    internal long AuthorizationEpoch { get; }

    internal string SessionCorrelationId { get; }

    internal string CoordinationCorrelationId { get; }
}
