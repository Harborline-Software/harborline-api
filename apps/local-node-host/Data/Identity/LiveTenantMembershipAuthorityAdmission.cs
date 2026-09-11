using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Data.Search;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>
/// Effect-site admission for tenant membership authority. Plane-aware per Option A
/// (admiral-ruling-2026-07-23T0045Z, ADR 0160 R3-D): a web membership is <b>grant-anchored</b>. It
/// is admitted by the live Party binding plus a valid live grant + authorization epoch for that
/// binding — the signed atlas roster is NOT required at web-acceptance time (the joiner's atlas
/// admission is deferred to the #3107 first-wire-enrollment bridge; the atlas plane remains the
/// P2P/desktop authority). The grant / owner-version / epoch teeth are unchanged and stay MANDATORY
/// for every member (a member whose grant drifted, expired, or was revoked is still refused), so
/// grant revocation is the sufficient web-plane revocation lever. Persisted membership coordinates
/// are only pins; every check rereads the live Party, grant row, and authorization epoch.
/// </summary>
/// <remarks>
/// The roster-presence REFUSAL that previously gated admission is removed here (the widened
/// atlas-presence predicate). Because grant/epoch teeth were already mandatory for EVERY member
/// (proven by the drift-refusal tests), "signed-roster-presence OR valid-grant+epoch" with
/// mandatory grant teeth reduces to grant-anchored admission — the roster read no longer changes
/// any decision and is dropped. #2620's E2E asserts the web joiner's membership via this predicate;
/// #3107 later records atlas presence WITHOUT changing these semantics.
/// </remarks>
internal sealed class LiveTenantMembershipAuthorityAdmission(
    ICanonicalPrincipalPartyReader partyReader,
    IDbContextFactory<NodeLocalSearchDbContext> grantFactory,
    TimeProvider timeProvider) : ITenantMembershipAuthorityAdmission
{
    private readonly ICanonicalPrincipalPartyReader _partyReader = partyReader
        ?? throw new ArgumentNullException(nameof(partyReader));
    private readonly IDbContextFactory<NodeLocalSearchDbContext> _grantFactory = grantFactory
        ?? throw new ArgumentNullException(nameof(grantFactory));
    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    public Task ValidateMutationAsync(
        string actorAccountId,
        string authorityEvidenceDigest,
        string accountId,
        TenantMembershipMutation mutation,
        CancellationToken cancellationToken) =>
        ValidateAsync(
            mutation.TenantId,
            mutation.CanonicalPrincipalId,
            mutation.GrantId,
            mutation.ExpectedGrantOwnerVersion,
            mutation.AuthorizationEpoch,
            exactEpoch: true,
            cancellationToken);

    /// <inheritdoc />
    public Task<long> ValidateExistingAsync(
        string accountId,
        TenantMembershipSnapshot membership,
        CancellationToken cancellationToken) =>
        ValidateAsync(
            membership.TenantId,
            membership.CanonicalPrincipalId,
            membership.GrantId,
            membership.GrantOwnerVersion,
            membership.AuthorizationEpoch,
            exactEpoch: false,
            cancellationToken);

    private async Task<long> ValidateAsync(
        string tenantId,
        string principalId,
        string grantId,
        long grantOwnerVersion,
        long authorizationEpoch,
        bool exactEpoch,
        CancellationToken cancellationToken)
    {
        var tenant = new TenantId(Guid.Parse(tenantId).ToString("D"));
        var principal = new PrincipalUserId(principalId);
        var binding = await _partyReader.ResolveAsync(tenant, principal, cancellationToken)
            .ConfigureAwait(false);
        if (binding is null ||
            !binding.VerifiedTenant.Equals(tenant) ||
            !binding.PrincipalUserId.Equals(principal))
        {
            Refuse();
        }

        // Grant-anchored web membership (Option A). The live grant + authorization epoch for the
        // party binding IS the admission proof; the signed atlas roster is not consulted here.
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using var grants = await _grantFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var grant = await grants.Grants.AsNoTracking()
            .Where(LiveWebMembershipGrantQuery.ForTenantAt(tenant, now))
            .SingleOrDefaultAsync(row =>
                row.GrantId == grantId &&
                row.SubjectId == principal.Value,
            cancellationToken).ConfigureAwait(false);
        var epoch = await grants.GrantAuthorizationEpochs.AsNoTracking().SingleOrDefaultAsync(row =>
                row.TenantId == tenant.Value && row.PrincipalId == principal.Value,
            cancellationToken).ConfigureAwait(false);
        // The grant teeth stay EXACT for both arms: the membership names one grant row and one owner
        // version, and a drifted or revoked grant is refused. Only the epoch differs. An admission-time
        // mutation must match the epoch it claimed to have read (exactEpoch); an existing membership
        // admits a live epoch at or ABOVE its pin and the caller re-pins to it, because the narrowing
        // act advances that epoch with no writer to refresh the document. A live epoch BELOW the pin is
        // a rollback and is refused, so this can only move the fence forward.
        if (grant is null ||
            grant.OwnerVersion != grantOwnerVersion ||
            epoch is null ||
            (exactEpoch
                ? epoch.AuthorizationEpoch != authorizationEpoch
                : epoch.AuthorizationEpoch < authorizationEpoch))
        {
            Refuse();
        }

        return epoch!.AuthorizationEpoch;
    }

    private static void Refuse() =>
        throw new InvalidOperationException(
            "identity.membership_unavailable: live tenant authority did not admit the membership.");
}
