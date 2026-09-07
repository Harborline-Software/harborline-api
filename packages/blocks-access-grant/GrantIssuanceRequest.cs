using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.AccessGrant;

public sealed record GrantIssuanceRequest(
    Guid GrantId,
    string TenantId,
    string PrincipalId,
    RoleReference Role,
    string GranterId,
    GranterKind GranterKind,
    ScopeExpression ScopeExpression,
    GrantResidency Residency,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidUntil,
    GrantProvenance Grant,
    DateTimeOffset LastReviewedAt,
    byte[]? VendorIdentityPublicKey = null);
