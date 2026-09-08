using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.AccessGrant;

/// <summary>
/// The refusal a grant mutation raises when it would leave the install with no Administrator in force
/// (ledger L619). Thrown from inside the store's atomic mutation, so the refusal rolls the write back.
/// </summary>
public sealed class LastAdministratorRefusedException(TenantId tenant, GrantId grant, DateTimeOffset at)
    : InvalidOperationException(
        $"Refused: grant {grant} is the last Administrator in force for tenant {tenant.Value} at {at:O}.")
{
    /// <summary>The tenant the refusal protects.</summary>
    public TenantId Tenant { get; } = tenant;

    /// <summary>The grant whose mutation was refused.</summary>
    public GrantId Grant { get; } = grant;

    /// <summary>The instant at which the mutation would have stranded the install.</summary>
    public DateTimeOffset At { get; } = at;
}

/// <summary>
/// The authority-model invariant <c>not_last_administrator()</c> (ledger L619): no grant mutation may
/// take the last in-force Administrator out of force.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where it runs.</b> Every revocation, expiry edit, validity edit, import and workflow revocation
/// reaches the durable grant record through <see cref="IGrantStore"/>, and every <see cref="IGrantStore"/>
/// implementation funnels its mutations through ONE private method that runs inside the store's atomic
/// unit of work (the <c>BEGIN IMMEDIATE</c> fence transaction on the node, the store-wide lock in memory).
/// The guard is called from inside that funnel, on the population read under the same lock, so the count
/// and the mutation are one transaction — two concurrent revocations of the last two Administrators are
/// serialized and the second one is refused. <c>LastAdministratorGuardArchTests</c> fails when a mutation
/// path bypasses it.
/// </para>
/// <para>
/// <b>"In force"</b> is ticket 205's effective-grant reading: the Administrator role, at a scope that
/// covers the whole install (a record-scoped Administrator administers a record, not the installation),
/// effective-dated and not expired and not revoked at the instant in question
/// (<see cref="AccessGrant.IsActiveAt"/>).
/// </para>
/// <para>
/// Ticket 211 slice 2's handover creates the successor's Administrator grant in the same unit of work
/// BEFORE the current holder's revocation, so the handover passes this guard rather than bypassing it;
/// there is deliberately no override parameter.
/// </para>
/// </remarks>
public static class LastAdministratorGuard
{
    private static readonly ScopeExpression InstallRoot = ScopeExpression.Parse("/");

    /// <summary>Whether <paramref name="grant"/> confers Administrator over the whole install at <paramref name="at"/>.</summary>
    public static bool IsAdministratorInForce(AccessGrant grant, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return grant.Role == RoleReference.Administrator
            && grant.Scope.Contains(InstallRoot)
            && grant.IsActiveAt(at);
    }

    /// <summary>
    /// Whether a mutation of <paramref name="grant"/> can strand the install, and therefore needs the
    /// population read. A grant that never conferred install-wide Administrator cannot.
    /// </summary>
    public static bool Guards(AccessGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return grant.Role == RoleReference.Administrator && grant.Scope.Contains(InstallRoot);
    }

    /// <summary>
    /// Refuses <paramref name="after"/> when it takes the last in-force Administrator out of force.
    /// <paramref name="tenantGrants"/> is the tenant's whole grant population, read inside the same
    /// transaction as the write it guards.
    /// </summary>
    /// <exception cref="LastAdministratorRefusedException">The mutation would leave zero Administrators in force.</exception>
    public static void EnsureNotLastAdministrator(
        AccessGrant before,
        AccessGrant after,
        IEnumerable<AccessGrant> tenantGrants)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(tenantGrants);

        var at = StrandInstant(before, after);
        if (!IsAdministratorInForce(before, at) || IsAdministratorInForce(after, at))
            return;

        foreach (var other in tenantGrants)
        {
            if (other.GrantId == before.GrantId || other.TenantId != before.TenantId)
                continue;
            if (IsAdministratorInForce(other, at))
                return;
        }

        throw new LastAdministratorRefusedException(before.TenantId, before.GrantId, at);
    }

    /// <summary>
    /// The instant the mutation first takes the grant out of force, read from the mutation's own evidence
    /// rather than from a clock: the stamped revocation instant, the start of a window a later
    /// <c>ValidFrom</c> abandons, or the earlier end a narrowed <c>ValidTo</c> imposes. A mutation that
    /// narrows nothing answers an instant at which both sides are in force, so the guard is a no-op.
    /// </summary>
    /// <remarks>
    /// The window a grant is in force starts at the LATER of its <c>ValidFrom</c> and its
    /// <c>GrantedAt</c> (<see cref="AccessGrant.IsActiveAt"/> requires both), so a backdated grant --
    /// one issued at approval time with an earlier requested <c>ValidFrom</c>, which
    /// <c>GrantIssuanceHandler</c> produces -- is read from its <c>GrantedAt</c>. Reading the bare
    /// <c>ValidFrom</c> answered an instant at which the grant was not yet in force, and the guard
    /// silently checked nothing.
    /// </remarks>
    private static DateTimeOffset StrandInstant(AccessGrant before, AccessGrant after)
    {
        var inForceFrom = before.Validity.ValidFrom > before.GrantedAt
            ? before.Validity.ValidFrom
            : before.GrantedAt;
        return after.Revocation is { } revocation ? revocation.RevokedAt
            : after.Validity.ValidFrom > before.Validity.ValidFrom ? inForceFrom
            : after.Validity.ValidTo is { } end
                && (before.Validity.ValidTo is null || end < before.Validity.ValidTo.Value) ? end
            : inForceFrom;
    }
}
