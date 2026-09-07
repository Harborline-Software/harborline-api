using System.Collections.Immutable;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Ship.Common;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.CompromisedDeviceResponse;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Data.Search;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>Which authority plane a listed member is currently admitted through (MTW-2 #2617).</summary>
public enum TeamMemberSource
{
    /// <summary>Signed atlas-roster member (the P2P/desktop authority plane).</summary>
    Roster,

    /// <summary>
    /// Grant-anchored web member — holds a valid live grant but is not (yet) present in the signed atlas
    /// roster (admiral-ruling-2026-07-23T0045Z Option A; bridged into the roster later by #3107). The
    /// roster reader alone under-reports these, so the admin list unions them in.
    /// </summary>
    Grant,

    /// <summary>A live grant without a unique live Party binding in this tenant.</summary>
    Unattributed,
}

/// <summary>One member row for the admin Team &amp; access list. Non-secret projection only.</summary>
public sealed record TeamMemberView(
    string PartyId,
    TeamMemberSource Source,
    IReadOnlyList<string> Capabilities,
    string? GrantId,
    string? AttributionFailure = null);

/// <summary>The tenant's members — signed roster UNIONed with grant-anchored web members.</summary>
public sealed record AdminTeamMembersResult(IReadOnlyList<TeamMemberView> Members);

/// <summary>One still-pending AccountSetup invitation, projected for the admin surface (non-secret).</summary>
public sealed record AdminPendingInvitation(
    string InvitationId,
    string InviterPartyId,
    IReadOnlyList<string> RequestedPermissions,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset AbsoluteExpiresAtUtc);

/// <summary>The tenant's still-pending AccountSetup invitations.</summary>
public sealed record AdminPendingInvitationsResult(IReadOnlyList<AdminPendingInvitation> Invitations);

/// <summary>A freshly-issued invitation — the raw code is returned exactly once and never persisted raw.</summary>
public sealed record AdminIssuedInvitation(
    string InvitationId,
    string Code,
    string TenantId,
    DateTimeOffset AbsoluteExpiresAtUtc);

/// <summary>Outcome of an admin member (grant) revocation.</summary>
public enum AdminRevokeMemberStatus
{
    /// <summary>The grant was revoked (or was already revoked — idempotent).</summary>
    Revoked,

    /// <summary>The grant is not a live grant in this tenant, or the input was malformed.</summary>
    NotFound,

    /// <summary>Refused: an admin cannot revoke the grant backing their own live session.</summary>
    SelfRevocationRefused,

    /// <summary>Refused: the grant is the last Administrator in force for the install (ledger L619).</summary>
    LastAdministratorRefused,

    /// <summary>
    /// The Administrator role was handed over: the successor's grant and this revocation landed as one
    /// transaction (ledger L618).
    /// </summary>
    HandedOver,

    /// <summary>
    /// Refused: the nominated successor is not a live principal of this tenant, is the outgoing holder, or
    /// already holds Administrator in force. Deliberately one status — a caller cannot enumerate which.
    /// </summary>
    SuccessorRefused,
}

/// <summary>The result of an admin member (grant) revocation.</summary>
/// <param name="SuccessorGrantId">The Administrator grant minted by a handover; null otherwise.</param>
public sealed record AdminRevokeMemberResult(AdminRevokeMemberStatus Status, string? SuccessorGrantId = null);

/// <summary>Outcome of an admin member permission-bundle update.</summary>
public enum AdminUpdateMemberPermissionsStatus
{
    Updated,
    NotFound,
    SelfUpdateRefused,
}

/// <summary>The result of an admin member permission-bundle update.</summary>
public sealed record AdminUpdateMemberPermissionsResult(AdminUpdateMemberPermissionsStatus Status);

/// <summary>
/// The members:manage-gated authority behind the admin Team &amp; access surface (MTW-2 #2617): list
/// members, list + issue pending AccountSetup invitations, and revoke a grant-anchored web member.
/// A null return is a non-enumerating refusal (caller failed the gate).
/// </summary>
public interface IAdminTeamAccessAuthority
{
    /// <summary>Lists the tenant's members (signed roster UNIONed with grant-anchored web members).</summary>
    Task<AdminTeamMembersResult?> ListMembersAsync(
        string selectedSessionHandle,
        string tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists still-pending AccountSetup invitations for the tenant.</summary>
    Task<AdminPendingInvitationsResult?> ListPendingInvitationsAsync(
        string selectedSessionHandle,
        string tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>Issues a new AccountSetup invitation for the tenant (delegates to the invitation issuer).</summary>
    Task<AdminIssuedInvitation?> IssueInvitationAsync(
        string selectedSessionHandle,
        string tenantId,
        IReadOnlyCollection<string> requestedPermissions,
        string idempotencyKey,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default);

    /// <summary>Revokes a grant-anchored web member's access by revoking the identified grant.</summary>
    /// <param name="successorPrincipalId">
    /// When present, the revocation is an Administrator handover (ledger L618): the successor's grant is
    /// created and this grant is revoked in ONE transaction. When absent, the last-Administrator guard
    /// (L619) refuses a revocation that would strand the install.
    /// </param>
    Task<AdminRevokeMemberResult?> RevokeMemberGrantAsync(
        string selectedSessionHandle,
        string tenantId,
        string grantId,
        AuthorizationWriteContext authority,
        string? successorPrincipalId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces a live grant's PBAC permission bundle through the fenced mutation path.</summary>
    Task<AdminUpdateMemberPermissionsResult?> UpdateMemberPermissionsAsync(
        string selectedSessionHandle,
        string tenantId,
        string grantId,
        IReadOnlyCollection<string> requestedPermissions,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The single members:manage-gated authority behind the admin Team &amp; access surface (MTW-2 #2617):
/// list members (signed atlas roster UNIONed with grant-anchored web members per the Option A ruling),
/// list/issue pending AccountSetup invitations, and revoke a grant-anchored web member's access via grant
/// revocation — the ruled sufficient web-plane revocation lever (grant revocation cascades to session
/// death because every selected-session revalidation re-reads the grant owner version + authorization
/// epoch).
/// </summary>
/// <remarks>
/// The caller gate mirrors <see cref="AccountSetupInvitationIssuer"/> EXACTLY — load the selected session
/// by handle digest, revalidate account/security-version, revalidate grant owner-version + authorization
/// epoch, resolve the live canonical Party, read the signed roster, and require the caller to hold
/// <see cref="TeamRolePermissions.MembersManage"/> IN the roster for THIS tenant. Admin authority is
/// therefore roster-anchored (the atlas plane remains the admin-authority plane; grant-anchored web
/// members are regular members pending #3107). Every failure returns a non-enumerating null/NotFound.
/// Issuance is delegated to <see cref="IAccountSetupInvitationIssuer"/>, which independently runs the
/// identical gate + the requested-permissions subset check.
/// </remarks>
internal sealed class AdminTeamAccessAuthority(
    IDbContextFactory<NodeLocalWebSessionDbContext> sessionFactory,
    WebSelectedSessionStore selectedSessionStore,
    IDbContextFactory<NodeLocalInstallationIdentityDbContext> identityFactory,
    IDbContextFactory<NodeLocalSearchDbContext> grantFactory,
    ICanonicalPrincipalPartyReader partyReader,
    IVerifiedTenantRosterReader rosterReader,
    AccountSetupInvitationStore invitationStore,
    IAccountSetupInvitationIssuer invitationIssuer,
    IGrantStore grantStore,
    IAuthorizedGrantRevocationWriter grantRevocations,
    IAuthorizationClosureReader authorization,
    AuthorizationGate gate,
    TimeProvider timeProvider,
    INodeRosterMemberRevocationAuthority memberRevocations,
    IAuthorizedAuditTrail audit,
    IOperationSigner signer,
    ITenantIdentityAuthorityPartitionResolver? partitions = null,
    InstallationIdentityCoordinatorService? coordinator = null,
    AuthorizationRefusalAudit? refusalAudit = null) : IAdminTeamAccessAuthority
{
    private readonly IDbContextFactory<NodeLocalWebSessionDbContext> _sessionFactory =
        sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
    private readonly WebSelectedSessionStore _selectedSessionStore =
        selectedSessionStore ?? throw new ArgumentNullException(nameof(selectedSessionStore));
    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _identityFactory =
        identityFactory ?? throw new ArgumentNullException(nameof(identityFactory));
    private readonly IDbContextFactory<NodeLocalSearchDbContext> _grantFactory =
        grantFactory ?? throw new ArgumentNullException(nameof(grantFactory));
    private readonly ICanonicalPrincipalPartyReader _partyReader =
        partyReader ?? throw new ArgumentNullException(nameof(partyReader));
    private readonly IVerifiedTenantRosterReader _rosterReader =
        rosterReader ?? throw new ArgumentNullException(nameof(rosterReader));
    private readonly AccountSetupInvitationStore _invitationStore =
        invitationStore ?? throw new ArgumentNullException(nameof(invitationStore));
    private readonly IAccountSetupInvitationIssuer _invitationIssuer =
        invitationIssuer ?? throw new ArgumentNullException(nameof(invitationIssuer));
    private readonly IGrantStore _grantStore =
        grantStore ?? throw new ArgumentNullException(nameof(grantStore));
    private readonly IAuthorizedGrantRevocationWriter _grantRevocations =
        grantRevocations ?? throw new ArgumentNullException(nameof(grantRevocations));
    private readonly IAuthorizationClosureReader _authorization =
        authorization ?? throw new ArgumentNullException(nameof(authorization));
    private readonly AuthorizationGate _gate =
        gate ?? throw new ArgumentNullException(nameof(gate));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly INodeRosterMemberRevocationAuthority _memberRevocations =
        memberRevocations ?? throw new ArgumentNullException(nameof(memberRevocations));
    private readonly IAuthorizedAuditTrail _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    private readonly IOperationSigner _signer = signer ?? throw new ArgumentNullException(nameof(signer));
    private readonly SemaphoreSlim _grantAuditGate = new(1, 1);

    internal DateTimeOffset CurrentInstant => _timeProvider.GetUtcNow();
    private readonly ITenantIdentityAuthorityPartitionResolver? _partitions = partitions;
    private readonly InstallationIdentityCoordinatorService? _coordinator = coordinator;

    /// <inheritdoc />
    public async Task<AdminTeamMembersResult?> ListMembersAsync(
        string selectedSessionHandle,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAdminAsync(selectedSessionHandle, tenantId, at: null, cancellationToken)
            .ConfigureAwait(false);
        if (context is null)
        {
            return null;
        }

        var members = new List<TeamMemberView>();
        var seenParties = new HashSet<string>(StringComparer.Ordinal);

        // Signed atlas roster members (the P2P/desktop authority plane).
        foreach (var rosterMember in context.Roster.Members)
        {
            if (seenParties.Add(rosterMember.PartyId))
            {
                members.Add(new TeamMemberView(
                    rosterMember.PartyId,
                    TeamMemberSource.Roster,
                    rosterMember.Permissions.Permissions.Order(StringComparer.Ordinal).ToArray(),
                    GrantId: null));
            }
        }

        // Grant-anchored web members (Option A): a valid live grant while atlas-roster-absent. Resolve each
        // grant's principal to its live Party; include only those NOT already surfaced by the roster.
        var tenant = new TenantId(context.CanonicalTenantId);
        var now = _timeProvider.GetUtcNow();
        var snapshot = await _grantStore.SnapshotAsync(tenant, cancellationToken).ConfigureAwait(false);
        foreach (var grant in snapshot)
        {
            if (!grant.IsActiveAt(now))
            {
                continue;
            }

            var party = await _partyReader
                .ResolveAsync(tenant, new PrincipalUserId(grant.Subject.Value), cancellationToken)
                .ConfigureAwait(false);
            if (party is null)
            {
                // The reader deliberately returns no identity for any failed binding. Keep each grant
                // addressable by its existing id; UNATTRIBUTED is a display label, never a party key.
                members.Add(new TeamMemberView(
                    "UNATTRIBUTED", TeamMemberSource.Unattributed,
                    await ProjectUnattributedGrantCapabilitiesAsync(grant, cancellationToken).ConfigureAwait(false),
                    grant.GrantId.Value.ToString(),
                    "No unique live party binding in this tenant: missing, tombstoned, detached, duplicated or wrong-tenant."));
                continue;
            }
            if (!seenParties.Add(party.PartyId.Value))
            {
                continue;
            }

            members.Add(new TeamMemberView(
                party.PartyId.Value,
                TeamMemberSource.Grant,
                await ProjectGrantCapabilitiesAsync(grant, now, cancellationToken).ConfigureAwait(false),
                grant.GrantId.Value.ToString()));
        }

        return new AdminTeamMembersResult(members);
    }

    // An unattributed row names one grant, so show only that role's atoms within that grant's scope.
    private async ValueTask<IReadOnlyList<string>> ProjectUnattributedGrantCapabilitiesAsync(
        AccessGrant grant, CancellationToken ct) =>
        (await _authorization.RolePermissionsAsync(grant.TenantId, grant.Role, ct).ConfigureAwait(false))
            .Atoms.Where(atom => atom.Scope.Intersect(grant.Scope) is not null)
            .Select(atom => atom.Operation.Value).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();

    private async ValueTask<IReadOnlyList<string>> ProjectGrantCapabilitiesAsync(
        AccessGrant grant, DateTimeOffset at, CancellationToken ct) =>
        (await _authorization.UserPermissionsAsync(grant.TenantId, grant.Subject, at, ct).ConfigureAwait(false))
            .Atoms.Select(atom => atom.Operation.Value).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();

    /// <inheritdoc />
    public async Task<AdminPendingInvitationsResult?> ListPendingInvitationsAsync(
        string selectedSessionHandle,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAdminAsync(selectedSessionHandle, tenantId, at: null, cancellationToken)
            .ConfigureAwait(false);
        if (context is null)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        var pending = await _invitationStore
            .ListPendingAsync(context.CanonicalTenantId, now, cancellationToken)
            .ConfigureAwait(false);
        return new AdminPendingInvitationsResult(pending
            .Select(p => new AdminPendingInvitation(
                p.InvitationId,
                p.InviterPartyId,
                DeserializePermissions(p.RequestedPermissionsJson),
                p.IssuedAtUtc,
                p.AbsoluteExpiresAtUtc))
            .ToArray());
    }

    /// <inheritdoc />
    public async Task<AdminIssuedInvitation?> IssueInvitationAsync(
        string selectedSessionHandle,
        string tenantId,
        IReadOnlyCollection<string> requestedPermissions,
        string idempotencyKey,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selectedSessionHandle) ||
            !Guid.TryParse(tenantId, out var parsedTenant) ||
            requestedPermissions is null || requestedPermissions.Count == 0 ||
            string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return null;
        }

        // The issuer runs the identical selected-session + roster members:manage gate PLUS the
        // requested-permissions subset check; no separate ResolveAdminAsync is needed here.
        var result = await _invitationIssuer.IssueAsync(
                selectedSessionHandle,
                new AccountSetupInvitationIssueRequest(
                    parsedTenant.ToString("D"),
                    requestedPermissions,
                    idempotencyKey),
                authority,
                cancellationToken)
            .ConfigureAwait(false);
        return result is null
            ? null
            : new AdminIssuedInvitation(
                result.InvitationId,
                result.RawCode,
                result.TenantId,
                result.AbsoluteExpiresAtUtc);
    }

    /// <inheritdoc />
    public async Task<AdminRevokeMemberResult?> RevokeMemberGrantAsync(
        string selectedSessionHandle,
        string tenantId,
        string grantId,
        AuthorizationWriteContext authority,
        string? successorPrincipalId = null,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthorityTenant(tenantId, authority);
        var context = await ResolveAdminAsync(selectedSessionHandle, tenantId, authority.At, cancellationToken,
                authority.Request(AuthorizationOperation.Parse(TeamRolePermissions.MembersManage), "members", grantId))
            .ConfigureAwait(false);
        if (context is null)
        {
            return null;
        }

        var decision = context.Decision;
        if (string.IsNullOrWhiteSpace(grantId) || !Guid.TryParse(grantId, out var parsedGrant))
        {
            return new AdminRevokeMemberResult(AdminRevokeMemberStatus.NotFound);
        }

        // Self-lockout guard: an admin cannot revoke the grant backing their own live session.
        if (context.Session.PinnedGrantOwnerVersions.Any(pin =>
                string.Equals(pin.GrantId, parsedGrant.ToString("D"), StringComparison.OrdinalIgnoreCase)))
        {
            return new AdminRevokeMemberResult(AdminRevokeMemberStatus.SelfRevocationRefused);
        }

        var tenant = new TenantId(context.CanonicalTenantId);
        var target = new GrantId(parsedGrant);
        var existing = await _grantStore.FindAsync(tenant, target, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return new AdminRevokeMemberResult(AdminRevokeMemberStatus.NotFound);
        }

        if (!string.Equals(context.Session.TenantPrincipalId, authority.Principal.Value, StringComparison.Ordinal))
            throw new ArgumentException("The selected-session principal does not match the write authority.", nameof(authority));

        var revocation = new GrantRevocation(
            new ActorId(context.Session.TenantPrincipalId), authority.At,
            new GrantReason(GrantReasonCodes.RevocationOffboarding, target.ToString()));

        if (!string.IsNullOrWhiteSpace(successorPrincipalId))
        {
            return await HandOverAdministratorAsync(
                    context, tenant, target, existing, revocation, successorPrincipalId, decision,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // not_last_administrator() (L619) read ahead of the roster leg, so a refusal leaves no half-done
        // revocation. The authoritative guard is the one inside the grant store's atomic mutation; this
        // call is the SAME method, so the two cannot answer differently.
        if (LastAdministratorGuard.Guards(existing))
        {
            var population = await _grantStore.SnapshotAsync(tenant, cancellationToken).ConfigureAwait(false);
            try
            {
                LastAdministratorGuard.EnsureNotLastAdministrator(
                    existing,
                    existing with { Status = GrantStatus.Revoked, Revocation = revocation },
                    population);
            }
            catch (LastAdministratorRefusedException)
            {
                await AppendGrantAuditAsync(
                        tenant, target, decision, RevocationRefused, "last-administrator", Guid.NewGuid(),
                        successorGrant: null, cancellationToken)
                    .ConfigureAwait(false);
                return new AdminRevokeMemberResult(AdminRevokeMemberStatus.LastAdministratorRefused);
            }
        }

        var revokedParty = await _partyReader.ResolveAsync(
                tenant, new PrincipalUserId(existing.Subject.Value), cancellationToken)
            .ConfigureAwait(false);
        // Preserve roster refusal ordering when attributable; a missing party never blocks the grant leg.
        if (revokedParty is not null)
        {
            await _memberRevocations.RevokeAsync(
                    tenant, grantId, revokedParty.PartyId.Value, context.CallerPartyId,
                    MemberRevocationReasons.Offboarding, correlationId: null, decision, cancellationToken)
                .ConfigureAwait(false);
        }
        if (existing.Status is not GrantStatus.Revoked)
        {
            var revoked = await _grantRevocations.RevokeAsync(tenant, target, revocation,
                    decision, cancellationToken)
                .ConfigureAwait(false);
            if (revoked is null) return new AdminRevokeMemberResult(AdminRevokeMemberStatus.NotFound);
        }
        await AppendGrantAuditAsync(
                tenant, target, decision, AuditEventType.CapabilityRevoked, MemberRevocationReasons.Offboarding,
                Guid.NewGuid(), successorGrant: null, cancellationToken)
            .ConfigureAwait(false);
        return new AdminRevokeMemberResult(AdminRevokeMemberStatus.Revoked);
    }

    /// <inheritdoc />
    public async Task<AdminUpdateMemberPermissionsResult?> UpdateMemberPermissionsAsync(
        string selectedSessionHandle,
        string tenantId,
        string grantId,
        IReadOnlyCollection<string> requestedPermissions,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthorityTenant(tenantId, authority);
        if (requestedPermissions is null || requestedPermissions.Count == 0)
        {
            return new AdminUpdateMemberPermissionsResult(AdminUpdateMemberPermissionsStatus.NotFound);
        }

        var context = await ResolveAdminAsync(selectedSessionHandle, tenantId, authority.At, cancellationToken,
                authority.Request(AuthorizationOperation.Parse(TeamRolePermissions.MembersManage), "members", grantId))
            .ConfigureAwait(false);
        if (context is null || _partitions is null || _coordinator is null)
        {
            return null;
        }

        var decision = context.Decision;
        if (string.IsNullOrWhiteSpace(grantId) || !Guid.TryParse(grantId, out var parsedGrant))
        {
            return new AdminUpdateMemberPermissionsResult(AdminUpdateMemberPermissionsStatus.NotFound);
        }

        if (context.Session.PinnedGrantOwnerVersions.Any(pin =>
                string.Equals(pin.GrantId, parsedGrant.ToString("D"), StringComparison.OrdinalIgnoreCase)))
        {
            return new AdminUpdateMemberPermissionsResult(
                AdminUpdateMemberPermissionsStatus.SelfUpdateRefused);
        }

        var nextPermissions = PermissionSet.From(requestedPermissions);
        if (!nextPermissions.IsSubsetOf(context.CallerPermissions))
        {
            return null;
        }

        // Ticket 204 retires permission-bundle mutation. The compatibility route remains fail-closed
        // while clients move to revoke-and-reissue; it cannot reach the old coordinator mutation path.
        return new AdminUpdateMemberPermissionsResult(AdminUpdateMemberPermissionsStatus.NotFound);
    }

    /// <summary>The refusal event type; the only one the idempotence scan does not de-duplicate.</summary>
    private static readonly AuditEventType RevocationRefused = new("CapabilityRevocationRefused");

    /// <summary>The audit reason both legs of a handover carry.</summary>
    private const string HandoverReason = "administrator-handover";

    /// <summary>
    /// Ledger L618 — the atomic Administrator handover. The successor's grant and the outgoing holder's
    /// revocation are ONE store transaction (<see cref="IGrantStore.HandoverAdministratorAsync"/>), so the
    /// install never has zero Administrators in force and neither leg can land alone; the L619 guard runs
    /// inside it over a population that already carries the successor. The roster leg and both audit rows
    /// follow the committed transaction and carry the same correlation id.
    /// </summary>
    private async Task<AdminRevokeMemberResult> HandOverAdministratorAsync(
        AdminSessionContext context,
        TenantId tenant,
        GrantId target,
        AccessGrant existing,
        GrantRevocation revocation,
        string successorPrincipalId,
        AuthorizationDecision decision,
        CancellationToken cancellationToken)
    {
        var at = revocation.RevokedAt;
        var successorPrincipal = new ActorId(successorPrincipalId);
        var successorParty = await _partyReader
            .ResolveAsync(tenant, new PrincipalUserId(successorPrincipalId), cancellationToken)
            .ConfigureAwait(false);
        var population = await _grantStore.SnapshotAsync(tenant, cancellationToken).ConfigureAwait(false);

        // The successor must be a live principal of this tenant, must not be the outgoing holder, and must
        // not already hold Administrator in force (that hands over nothing); and only an install-wide
        // Administrator grant is handed over at all. One status for every refusal — no enumeration.
        // Ticket 211 slice 3 - a handover that leaves the successor unable to manage members satisfies
        // L618 in grant state and strands the install at the surface. The signed roster edge is
        // authoritative where it exists, so a successor the roster narrows or has ejected is refused here
        // rather than handed a role that does nothing.
        var successorInputs = successorParty is null ? null : EffectiveMemberPermissions.Read(
            context.Roster, successorParty.PartyId.Value, successorPrincipal);
        var successorDecision = successorInputs is null ? null : await _gate.DecideAsync(
            new AuthorizationWriteContext(successorPrincipal, tenant, at)
                .Request(AuthorizationOperation.Parse(TeamRolePermissions.MembersManage), "members", "handover")
                with { Roster = successorInputs with
                    { ProspectiveAdministratorGrant = true,
                        Permissions = successorInputs.Permissions ?? PermissionSet.Of(TeamRolePermissions.MembersManage) } },
            cancellationToken).ConfigureAwait(false);
        if (successorDecision is not null && refusalAudit is not null)
            await refusalAudit.RecordAsync(successorDecision, cancellationToken).ConfigureAwait(false);
        if (successorParty is null || successorDecision?.Verdict != AuthorizationVerdict.Allowed
            || successorPrincipal == existing.Subject
            || !LastAdministratorGuard.Guards(existing)
            || population.Any(grant => grant.Subject == successorPrincipal
                && LastAdministratorGuard.IsAdministratorInForce(grant, at)))
        {
            return new AdminRevokeMemberResult(AdminRevokeMemberStatus.SuccessorRefused);
        }

        var revokedParty = await _partyReader
            .ResolveAsync(tenant, new PrincipalUserId(existing.Subject.Value), cancellationToken)
            .ConfigureAwait(false);
        var correlationId = Guid.NewGuid();
        var granter = new ActorId(context.Session.TenantPrincipalId);
        var successor = new AccessGrant(
            GrantId.New(), tenant, successorPrincipal, RoleReference.Administrator, existing.Scope,
            existing.Residency, new GrantValidity(at), GranterKind.Person, granter, at,
            new GrantProvenance(
                GrantSourceKind.Manual,
                new GrantReason(GrantReasonCodes.Manual, correlationId.ToString("D")),
                granter),
            at);

        var handover = await _grantRevocations
            .HandoverAsync(tenant, target, successor, revocation, decision, cancellationToken)
            .ConfigureAwait(false);
        if (handover is null) return new AdminRevokeMemberResult(AdminRevokeMemberStatus.NotFound);

        if (revokedParty is not null)
        {
            await _memberRevocations.RevokeAsync(
                    tenant, target.ToString(), revokedParty.PartyId.Value, context.CallerPartyId,
                    MemberRevocationReasons.Offboarding, correlationId.ToString("D"), decision, cancellationToken)
                .ConfigureAwait(false);
        }
        // Both legs are audited against the act's OWN target -- the grant the decision admitted -- so the
        // carried decision is never re-pointed at a record it did not permit; the successor's grant id
        // travels in the payload.
        await AppendGrantAuditAsync(
                tenant, target, decision, AuditEventType.CapabilityDelegated, HandoverReason, correlationId,
                handover.Successor.GrantId, cancellationToken)
            .ConfigureAwait(false);
        await AppendGrantAuditAsync(
                tenant, target, decision, AuditEventType.CapabilityRevoked, HandoverReason, correlationId,
                handover.Successor.GrantId, cancellationToken)
            .ConfigureAwait(false);
        return new AdminRevokeMemberResult(
            AdminRevokeMemberStatus.HandedOver, handover.Successor.GrantId.ToString());
    }

    /// <summary>
    /// One permanent audit row for one leg of a grant act, carrying the SAME admitted decision the act was
    /// gated on. <paramref name="correlationId"/> ties the two legs of a handover (L618) together.
    /// </summary>
    private async ValueTask AppendGrantAuditAsync(
        TenantId tenant,
        GrantId grantId,
        AuthorizationDecision admittedDecision,
        AuditEventType eventType,
        string reason,
        Guid correlationId,
        GrantId? successorGrant,
        CancellationToken cancellationToken)
    {
        await _grantAuditGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var recordId = grantId.Value.ToString("D");
            var operation = AuthorizationOperation.Parse(TeamRolePermissions.MembersManage);
            var reaction = new AuthorizationWriteContext(
                admittedDecision.Request.Principal, tenant, admittedDecision.DecidedAt)
                .Request(operation, "members", recordId);
            admittedDecision.RequireAllowedReaction(
                operation, tenant, reaction.Target.RecordKind, reaction.Target.RecordId);
            if (eventType != RevocationRefused)
            {
                await foreach (var existing in _audit.QueryAsync(
                                   new AuditQuery(tenant, eventType), cancellationToken)
                                   .ConfigureAwait(false))
                {
                    if (existing.Target == reaction.Target) return;
                }
            }
            var payload = await _signer.SignAsync(new AuditPayload(new Dictionary<string, object?>
            {
                ["grant_id"] = recordId,
                ["reason"] = reason,
                ["correlation_id"] = correlationId.ToString("D"),
                ["successor_grant_id"] = successorGrant?.Value.ToString("D"),
            }), admittedDecision.DecidedAt, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
            await _audit.AppendAuthorizedAsync(new AuditRecord(
                Guid.NewGuid(), tenant, eventType,
                admittedDecision.DecidedAt, payload,
                ImmutableArray<AttestingSignature>.Empty, Actor: admittedDecision.Request.Principal,
                Target: reaction.Target, Act: reaction.Act), admittedDecision, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _grantAuditGate.Release();
        }
    }

    /// <summary>
    /// The shared caller gate: revalidates the selected session end-to-end and requires roster
    /// members:manage for THIS tenant. Returns the validated context, or null on ANY failure.
    /// </summary>
    private async Task<AdminSessionContext?> ResolveAdminAsync(
        string selectedSessionHandle,
        string tenantId,
        DateTimeOffset? at,
        CancellationToken cancellationToken,
        AuthorizationGateRequest? request = null)
    {
        if (string.IsNullOrWhiteSpace(selectedSessionHandle) ||
            !Guid.TryParse(tenantId, out var parsedTenant))
        {
            return null;
        }

        var canonicalTenantId = parsedTenant.ToString("D");
        var now = at ?? _timeProvider.GetUtcNow();
        var session = await LoadSelectedSessionAsync(selectedSessionHandle, now, cancellationToken)
            .ConfigureAwait(false);
        if (session is null ||
            !string.Equals(session.TenantId, canonicalTenantId, StringComparison.Ordinal))
        {
            return null;
        }

        if (!await AccountIsCurrentAsync(session, cancellationToken).ConfigureAwait(false) ||
            !await GrantPinsAreCurrentAsync(session, now, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var tenant = new TenantId(canonicalTenantId);
        var principal = new PrincipalUserId(session.TenantPrincipalId);
        var party = await _partyReader.ResolveAsync(tenant, principal, cancellationToken)
            .ConfigureAwait(false);
        if (party is null ||
            !string.Equals(
                party.PartyId.Value, session.CanonicalPartyReference, StringComparison.Ordinal))
        {
            return null;
        }

        MemberRoster roster;
        try
        {
            roster = await _rosterReader.ReadAsync(tenant, cancellationToken).ConfigureAwait(false);
        }
        catch (VerifiedTenantRosterRefusedException)
        {
            return null;
        }

        var inputs = await EffectiveMemberPermissions.ReadAsync(
            _authorization, roster, party.PartyId.Value, tenant,
            new ActorId(session.TenantPrincipalId), now, cancellationToken).ConfigureAwait(false);
        request ??= new AuthorizationWriteContext(new ActorId(session.TenantPrincipalId), tenant, now)
            .Request(AuthorizationOperation.Parse(TeamRolePermissions.MembersManage), "members", "list");
        if (request.Principal.Value != session.TenantPrincipalId)
            throw new ArgumentException("The selected-session principal does not match the write authority.");
        var decision = await _gate.DecideAsync(request with { Roster = inputs with
            { RequireGrantCoverage = at is not null } }, cancellationToken).ConfigureAwait(false);
        if (refusalAudit is not null) await refusalAudit.RecordAsync(decision, cancellationToken).ConfigureAwait(false);
        if (decision.Verdict == AuthorizationVerdict.Denied) return null;
        return new AdminSessionContext(session, canonicalTenantId, party.PartyId.Value, roster,
            PermissionSet.From(decision.AtomsConsidered.Select(atom => atom.Operation.Value)), decision);
    }

    private static void EnsureAuthorityTenant(string tenantId, AuthorizationWriteContext authority)
    {
        if (!Guid.TryParse(tenantId, out var parsed)
            || !string.Equals(parsed.ToString("D"), authority.Tenant.Value, StringComparison.Ordinal))
            throw new ArgumentException("The requested tenant does not match the write authority.", nameof(tenantId));
    }

    private async Task<WebUserSessionRecord?> LoadSelectedSessionAsync(
        string rawHandle,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var digest = AccountSetupInvitationStore.Digest(rawHandle);
        await using var sessions = await _sessionFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var session = await _selectedSessionStore.FindStoredAsync(
                sessions,
                digest,
                track: false,
                cancellationToken)
            .ConfigureAwait(false);
        if (session is null || now < session.IssuedAtUtc || now >= session.IdleExpiresAtUtc ||
            now >= session.AbsoluteExpiresAtUtc || session.PinnedGrantOwnerVersions.Count != 1)
        {
            return null;
        }

        var revoked = await sessions.Revocations.AsNoTracking().AnyAsync(row =>
                row.Audience == WebCookieAudience.SelectedSession &&
                row.AccountId == session.AccountId &&
                row.SubjectCorrelationId == session.SessionCorrelationId,
            cancellationToken).ConfigureAwait(false);
        return revoked ? null : session;
    }

    private async Task<bool> AccountIsCurrentAsync(
        WebUserSessionRecord session,
        CancellationToken cancellationToken)
    {
        await using var identity = await _identityFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await identity.Accounts.AsNoTracking().AnyAsync(row =>
                row.AccountId == session.AccountId &&
                row.Status == InstallationAccountStatus.Active &&
                row.SecurityVersion == session.AccountSecurityVersion,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> GrantPinsAreCurrentAsync(
        WebUserSessionRecord session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pin = session.PinnedGrantOwnerVersions.Single();
        var nowUnixMs = now.ToUnixTimeMilliseconds();
        await using var grants = await _grantFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var grant = await grants.Grants.AsNoTracking().SingleOrDefaultAsync(row =>
                row.TenantId == session.TenantId &&
                row.SubjectId == session.TenantPrincipalId &&
                row.GrantId == pin.GrantId,
            cancellationToken).ConfigureAwait(false);
        var epoch = await grants.GrantAuthorizationEpochs.AsNoTracking().SingleOrDefaultAsync(row =>
                row.TenantId == session.TenantId && row.PrincipalId == session.TenantPrincipalId,
            cancellationToken).ConfigureAwait(false);
        return grant is not null && grant.OwnerVersion == pin.OwnerVersion &&
            grant.RevokedAtUnixMs is null && grant.ValidityFromUnixMs <= nowUnixMs &&
            (grant.ValidityUntilUnixMs is null || grant.ValidityUntilUnixMs > nowUnixMs) &&
            epoch is not null && epoch.AuthorizationEpoch == session.AuthorizationEpoch;
    }

    private static IReadOnlyList<string> DeserializePermissions(string permissionsJson)
    {
        if (string.IsNullOrWhiteSpace(permissionsJson))
        {
            return Array.Empty<string>();
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<string[]>(permissionsJson)
                ?? Array.Empty<string>();
        }
        catch (System.Text.Json.JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private sealed record AdminSessionContext(
        WebUserSessionRecord Session,
        string CanonicalTenantId,
        string CallerPartyId,
        MemberRoster Roster,
        PermissionSet CallerPermissions,
        AuthorizationDecision Decision);
}
