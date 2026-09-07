using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Data.Search;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

internal sealed record AccountSetupInvitationIssueRequest(
    string TenantId,
    IReadOnlyCollection<string> RequestedPermissions,
    string IdempotencyKey);

internal interface IAccountSetupInvitationIssuer
{
    Task<AccountSetupInvitationIssueResult?> IssueAsync(
        string selectedSessionHandle,
        AccountSetupInvitationIssueRequest request,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Issues AccountSetup authority only after revalidating the selected-session account, Party,
/// signed roster membership, PBAC permission set, grant owner versions, and authorization epoch.
/// </summary>
internal sealed class AccountSetupInvitationIssuer(
    IDbContextFactory<NodeLocalWebSessionDbContext> sessionFactory,
    WebSelectedSessionStore selectedSessionStore,
    IDbContextFactory<NodeLocalInstallationIdentityDbContext> identityFactory,
    IDbContextFactory<NodeLocalSearchDbContext> grantFactory,
    ICanonicalPrincipalPartyReader partyReader,
    IVerifiedTenantRosterReader rosterReader,
    AccountSetupInvitationStore store,
    AuthorizationGate gate,
    TimeProvider timeProvider,
    AuthorizationRefusalAudit? refusalAudit = null) : IAccountSetupInvitationIssuer
{
    internal static readonly TimeSpan InvitationLifetime = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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
    private readonly AccountSetupInvitationStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly AuthorizationGate _gate =
        gate ?? throw new ArgumentNullException(nameof(gate));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    internal DateTimeOffset CurrentInstant => _timeProvider.GetUtcNow();

    public async Task<AccountSetupInvitationIssueResult?> IssueAsync(
        string selectedSessionHandle,
        AccountSetupInvitationIssueRequest request,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedSessionHandle);
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.TenantId, authority.Tenant.Value, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The invitation tenant does not match the write authority.", nameof(request));
        var coverage = await _gate.DecideAsync(
            authority.Request(AuthorizationOperation.Parse(TeamRolePermissions.MembersManage),
                "members", request.IdempotencyKey), cancellationToken).ConfigureAwait(false);
        if (refusalAudit is not null) await refusalAudit.RecordAsync(coverage, cancellationToken).ConfigureAwait(false);
        coverage.RequireAllowed();
        if (!Guid.TryParse(request.TenantId, out var parsedTenant) ||
            request.RequestedPermissions is null || request.RequestedPermissions.Count == 0 ||
            request.RequestedPermissions.Any(string.IsNullOrWhiteSpace) ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return null;
        }

        PermissionSet requested;
        try
        {
            requested = PermissionSet.From(request.RequestedPermissions);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var tenantId = parsedTenant.ToString("D");
        var now = authority.At;
        var session = await LoadSelectedSessionAsync(selectedSessionHandle, now, cancellationToken)
            .ConfigureAwait(false);
        if (session is null || !string.Equals(session.TenantId, tenantId, StringComparison.Ordinal))
        {
            return null;
        }
        if (!string.Equals(session.TenantPrincipalId, authority.Principal.Value, StringComparison.Ordinal))
            throw new ArgumentException("The selected-session principal does not match the write authority.", nameof(authority));

        if (!await AccountIsCurrentAsync(session, cancellationToken).ConfigureAwait(false) ||
            !await GrantPinsAreCurrentAsync(session, now, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var tenant = new TenantId(tenantId);
        var principal = new PrincipalUserId(session.TenantPrincipalId);
        var party = await _partyReader.ResolveAsync(tenant, principal, cancellationToken)
            .ConfigureAwait(false);
        if (party is null ||
            !string.Equals(party.PartyId.Value, session.CanonicalPartyReference, StringComparison.Ordinal))
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

        var decision = await _gate.DecideAsync(
            authority.Request(AuthorizationOperation.Parse(TeamRolePermissions.MembersManage),
                "members", request.IdempotencyKey) with
            {
                Roster = EffectiveMemberPermissions.Read(roster, party.PartyId.Value, authority.Principal) with
                {
                    RequireMember = true, RequireGrantCoverage = true,
                    RequiredPermissions = requested,
                }
            }, cancellationToken).ConfigureAwait(false);
        if (refusalAudit is not null) await refusalAudit.RecordAsync(decision, cancellationToken).ConfigureAwait(false);
        decision.RequireAllowed();

        var grantPin = session.PinnedGrantOwnerVersions.Single();
        var permissions = requested.Permissions.Order(StringComparer.Ordinal).ToArray();
        var permissionsJson = JsonSerializer.Serialize(permissions, Json);
        var fingerprint = Fingerprint(
            tenantId,
            session.AccountId,
            session.SessionCorrelationId,
            request.IdempotencyKey,
            permissions);
        var seed = new AccountSetupInvitationSeed(
            TenantId: tenantId,
            InviterAccountId: session.AccountId,
            InviterPrincipalId: session.TenantPrincipalId,
            InviterPartyId: party.PartyId.Value,
            InviterSessionCorrelationId: session.SessionCorrelationId,
            InviterMembershipId: session.MembershipId,
            InviterMembershipOwnerVersion: session.MembershipOwnerVersion,
            InviterGrantId: grantPin.GrantId,
            InviterGrantOwnerVersion: grantPin.OwnerVersion,
            InviterAuthorizationEpoch: session.AuthorizationEpoch,
            RequestedPermissionsJson: permissionsJson,
            CommandFingerprint: fingerprint,
            IssuedAtUtc: now,
            AbsoluteExpiresAtUtc: now + InvitationLifetime);
        return await _store.IssueAsync(seed, cancellationToken).ConfigureAwait(false);
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

    private static string Fingerprint(
        string tenantId,
        string accountId,
        string sessionCorrelationId,
        string idempotencyKey,
        IReadOnlyCollection<string> permissions)
    {
        var canonical = string.Join('\n',
            tenantId,
            accountId,
            sessionCorrelationId,
            idempotencyKey,
            string.Join('\n', permissions));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
