using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.AccessGrant;

public sealed record VersionedAccessGrant(AccessGrant Grant, long OwnerVersion);

/// <summary>The two legs of one atomic Administrator handover (ledger L618).</summary>
public sealed record AdministratorHandover(AccessGrant Successor, AccessGrant Revoked);

public interface IGrantStore
{
    Task<AccessGrant> AppendAsync(TenantId tenantId, AccessGrant grant, string? sourceReference = null, CancellationToken ct = default);
    Task<AccessGrant?> FindAsync(TenantId tenantId, GrantId grantId, CancellationToken ct = default);
    Task<VersionedAccessGrant?> FindVersionedAsync(
        TenantId tenantId, GrantId grantId, CancellationToken ct = default);
    Task<AccessGrant?> FindBySourceReferenceAsync(TenantId tenantId, string sourceReference, CancellationToken ct = default);
    Task<IReadOnlyList<AccessGrant>> FindByPrincipalAsync(TenantId tenantId, ActorId principal, CancellationToken ct = default);
    Task<IReadOnlyList<VersionedAccessGrant>> FindVersionedByPrincipalAsync(
        TenantId tenantId, ActorId principal, CancellationToken ct = default);
    Task<IReadOnlyList<AccessGrant>> SnapshotAsync(TenantId tenantId, CancellationToken ct = default);
    Task<bool> HasAdministratorGrantEverAsync(CancellationToken ct = default);
    Task<AccessGrant?> ChangeValidityAsync(TenantId tenantId, GrantId grantId, GrantValidity validity, ActorId changedBy, GrantReason reason, CancellationToken ct = default);
    Task<AccessGrant?> RecordReviewAsync(TenantId tenantId, GrantId grantId, DateTimeOffset reviewedAt, ActorId reviewedBy, CancellationToken ct = default);
    Task<AccessGrant?> RevokeAsync(TenantId tenantId, GrantId grantId, GrantRevocation revocation, CancellationToken ct = default);

    /// <summary>
    /// Ledger L618 — appends <paramref name="successor"/> and revokes <paramref name="currentGrantId"/> as ONE
    /// unit of work, so the install is never left without an Administrator in force and neither leg can land
    /// alone. The <c>not_last_administrator()</c> guard runs on the revocation inside that same unit, over a
    /// population that already contains the successor, so a successor that is not in force at the revocation
    /// instant refuses the handover and rolls both legs back. Returns null when the current grant does not exist.
    /// </summary>
    Task<AdministratorHandover?> HandoverAdministratorAsync(
        TenantId tenantId,
        GrantId currentGrantId,
        AccessGrant successor,
        GrantRevocation revocation,
        CancellationToken ct = default);
}

/// <summary>Reads the freshness epoch minted atomically by a grant write without widening the grant-store API.</summary>
public interface IGrantAuthorizationEpochReader
{
    Task<long?> ReadAuthorizationEpochAsync(
        TenantId tenantId, ActorId principal, CancellationToken ct = default);
}
