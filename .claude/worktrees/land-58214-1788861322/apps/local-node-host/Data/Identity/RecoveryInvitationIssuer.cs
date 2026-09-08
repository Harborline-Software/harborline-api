using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Data.Search;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

internal sealed record RecoveryInvitationIssueRequest(
    string TenantId,
    string TargetUsername,
    string IdempotencyKey);

internal interface IRecoveryInvitationIssuer
{
    Task<RecoveryInvitationIssueResult?> IssueAsync(
        string selectedSessionHandle,
        RecoveryInvitationIssueRequest request,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Issues an account-recovery invitation (MTW-2 #3013, ADR 0160 R3-E/D3) targeting an EXISTING
/// installation account, only after revalidating the issuing administrator's selected-session
/// account, live canonical Party, signed roster <c>members:manage</c> on THIS tenant, grant owner
/// versions, and authorization epoch — the same proven gate chain as
/// <see cref="AccountSetupInvitationIssuer"/>. Recovery is "administrator-issued" (D3): a
/// <c>members:manage</c> holder starts it, but the account holder redeems it by choosing the new
/// credential; the administrator never sets or learns it. It reserves and confers nothing beyond the
/// opaque code and never touches tenant Party/trust/role/grant facts.
/// </summary>
internal sealed class RecoveryInvitationIssuer(
    IDbContextFactory<NodeLocalWebSessionDbContext> sessionFactory,
    WebSelectedSessionStore selectedSessionStore,
    IDbContextFactory<NodeLocalInstallationIdentityDbContext> identityFactory,
    IDbContextFactory<NodeLocalSearchDbContext> grantFactory,
    ICanonicalPrincipalPartyReader partyReader,
    IVerifiedTenantRosterReader rosterReader,
    RecoveryInvitationStore store,
    AuthorizationGate gate,
    AuthorizationRefusalAudit? refusalAudit = null) : IRecoveryInvitationIssuer
{
    internal static readonly TimeSpan InvitationLifetime = TimeSpan.FromHours(1);

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
    private readonly RecoveryInvitationStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly AuthorizationGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    public async Task<RecoveryInvitationIssueResult?> IssueAsync(
        string selectedSessionHandle,
        RecoveryInvitationIssueRequest request,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedSessionHandle);
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.TenantId, authority.Tenant.Value, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The recovery tenant does not match the write authority.", nameof(request));
        var coverage = await _gate.DecideAsync(
            authority.Request(AuthorizationOperation.Parse(TeamRolePermissions.MembersManage),
                "members", request.IdempotencyKey), cancellationToken).ConfigureAwait(false);
        if (refusalAudit is not null) await refusalAudit.RecordAsync(coverage, cancellationToken).ConfigureAwait(false);
        coverage.RequireAllowed();
        var normalizedTarget = WebUsernameNormalizer.TryNormalize(request.TargetUsername);
        if (!Guid.TryParse(request.TenantId, out var parsedTenant) ||
            normalizedTarget is null ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey))
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
                }
            }, cancellationToken).ConfigureAwait(false);
        if (refusalAudit is not null) await refusalAudit.RecordAsync(decision, cancellationToken).ConfigureAwait(false);
        decision.RequireAllowed();

        // Resolve the EXISTING target account. Non-enumerating: any miss returns the same null the
        // authority failures above return.
        var target = await ResolveTargetAccountAsync(normalizedTarget, cancellationToken)
            .ConfigureAwait(false);
        if (target is null)
        {
            return null;
        }

        var fingerprint = Fingerprint(
            tenantId, session.AccountId, session.SessionCorrelationId, request.IdempotencyKey, target.AccountId);
        var seed = new RecoveryInvitationSeed(
            TenantId: tenantId,
            IssuerAccountId: session.AccountId,
            IssuerPrincipalId: session.TenantPrincipalId,
            TargetAccountId: target.AccountId,
            TargetNormalizedUsername: target.NormalizedUsername,
            CommandFingerprint: fingerprint,
            IssuedAtUtc: now,
            AbsoluteExpiresAtUtc: now + InvitationLifetime);
        return await _store.IssueAsync(seed, cancellationToken).ConfigureAwait(false);
    }

    private async Task<InstallationAccountRecord?> ResolveTargetAccountAsync(
        string normalizedUsername,
        CancellationToken cancellationToken)
    {
        await using var identity = await _identityFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await identity.Accounts.AsNoTracking().SingleOrDefaultAsync(
            row => row.NormalizedUsername == normalizedUsername &&
                   row.Status == InstallationAccountStatus.Active,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<WebUserSessionRecord?> LoadSelectedSessionAsync(
        string rawHandle,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var digest = RecoveryInvitationStore.Digest(rawHandle);
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
        string issuerAccountId,
        string sessionCorrelationId,
        string idempotencyKey,
        string targetAccountId)
    {
        var canonical = string.Join('\n',
            tenantId,
            issuerAccountId,
            sessionCorrelationId,
            idempotencyKey,
            targetAccountId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
