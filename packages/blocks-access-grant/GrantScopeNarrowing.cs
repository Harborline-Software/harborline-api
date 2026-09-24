using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.AccessGrant;

/// <summary>The two durable legs of one scope-only revoke-and-reissue.</summary>
public sealed record GrantScopeNarrowing(AccessGrant Reissued, AccessGrant Revoked)
{
    /// <summary>Checks the domain transition without making an authorization decision.</summary>
    public static GrantScopeNarrowing Prepare(
        AccessGrant current, ScopeExpression narrowed, GrantId successorId, GrantRevocation revocation)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(narrowed);
        ArgumentNullException.ThrowIfNull(revocation);
        if (current.Status == GrantStatus.Revoked || !current.IsActiveAt(revocation.RevokedAt)
            || current.Scope == narrowed || !current.Scope.Contains(narrowed)
            || successorId == current.GrantId || successorId.Value == Guid.Empty)
            throw new InvalidOperationException("A live grant can only be replaced by a new grant at a strictly narrower scope.");
        var reissued = current with
        {
            GrantId = successorId,
            Scope = narrowed,
            GrantedBy = revocation.RevokedBy,
            GrantedAt = revocation.RevokedAt,
            GranterKind = GranterKind.Person,
            Validity = new GrantValidity(revocation.RevokedAt, current.Validity.ValidTo),
            Grant = new GrantProvenance(GrantSourceKind.Manual,
                new GrantReason(GrantReasonCodes.Manual, current.GrantId.ToString()), revocation.RevokedBy),
            LastReviewedAt = revocation.RevokedAt,
            LastReviewedBy = revocation.RevokedBy,
            ValidityChange = null,
        };
        return new(reissued, current with { Status = GrantStatus.Revoked, Revocation = revocation });
    }
}
