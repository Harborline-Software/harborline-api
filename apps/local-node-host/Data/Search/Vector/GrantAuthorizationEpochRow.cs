namespace Harborline.Api.LocalNodeHost.Data.Search.Vector;

/// <summary>
/// Live grant-authority freshness state for one principal in one tenant. The grant writer creates or
/// increments this row atomically with each committed grant create, update, or revoke.
/// </summary>
public sealed class GrantAuthorizationEpochRow
{
    /// <summary>The tenant half of the authority-owner key.</summary>
    public required string TenantId { get; set; }

    /// <summary>The principal half of the authority-owner key.</summary>
    public required string PrincipalId { get; set; }

    /// <summary>The monotonic epoch owned by the grant authority.</summary>
    public required long AuthorizationEpoch { get; set; }
}
