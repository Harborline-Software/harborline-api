using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Session;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>Authenticates one exact selected-session audience into immutable request authority.</summary>
internal interface IWebSelectedSessionPrincipalAuthority
{
    Task<SelectedSessionRequestPrincipal?> AuthenticateAsync(
        string? selectedHandle,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reloads every selected-session authority owner before constructing one request principal.
/// </summary>
internal sealed class WebSelectedSessionPrincipalAuthority : IWebSelectedSessionPrincipalAuthority
{
    private readonly WebSelectedSessionStore _sessions;
    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _identityFactory;
    private readonly InstallationIdentityCoordinatorService _membershipAuthority;
    private readonly ICanonicalPrincipalPartyReader _partyReader;
    private readonly SessionOptions _sessionOptions;
    private readonly TimeProvider _timeProvider;
    // PUBLIC, not internal: the DI container selects constructors with GetConstructors(),
    // which returns PUBLIC instance constructors only. An internal constructor on a
    // DI-registered type cannot be resolved at all -- the host dies at startup with
    // "a suitable constructor could not be located". The class stays internal sealed, so
    // this widens nothing outside the assembly.

    public WebSelectedSessionPrincipalAuthority(
        WebSelectedSessionStore sessions,
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> identityFactory,
        InstallationIdentityCoordinatorService membershipAuthority,
        ICanonicalPrincipalPartyReader partyReader,
        IOptions<SessionOptions> sessionOptions,
        TimeProvider timeProvider)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _identityFactory = identityFactory ?? throw new ArgumentNullException(nameof(identityFactory));
        _membershipAuthority = membershipAuthority
            ?? throw new ArgumentNullException(nameof(membershipAuthority));
        _partyReader = partyReader ?? throw new ArgumentNullException(nameof(partyReader));
        _sessionOptions = sessionOptions?.Value ?? throw new ArgumentNullException(nameof(sessionOptions));
        _sessionOptions.Validate();
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<SelectedSessionRequestPrincipal?> AuthenticateAsync(
        string? selectedHandle,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selectedHandle))
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(selectedHandle)));
        var session = await _sessions.FindForRecoveryAsync(digest, now, cancellationToken)
            .ConfigureAwait(false);
        if (session is null ||
            !await AccountIsCurrentAsync(session, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        session = await _sessions.FindActiveAsync(
                digest,
                session.AccountSecurityVersion,
                now,
                cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }

        TenantMembershipSnapshot? membership;
        try
        {
            membership = await _membershipAuthority.ResolveUsableMembershipAsync(
                    session.AccountId,
                    session.TenantId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
        {
            return null;
        }

        if (!MatchesPinnedAuthority(session, membership))
        {
            return null;
        }

        TenantId tenant;
        try
        {
            tenant = new TenantId(Guid.Parse(session.TenantId).ToString("D"));
        }
        catch (FormatException)
        {
            return null;
        }

        var principal = new PrincipalUserId(session.TenantPrincipalId);
        var party = await _partyReader.ResolveAsync(tenant, principal, cancellationToken)
            .ConfigureAwait(false);
        if (party is null ||
            !party.VerifiedTenant.Equals(tenant) ||
            !party.PrincipalUserId.Equals(principal) ||
            !string.Equals(
                party.PartyId.Value,
                session.CanonicalPartyReference,
                StringComparison.Ordinal) ||
            !await AccountIsCurrentAsync(session, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // Authority is current. Only now may the idle TTL slide. A concurrent revocation or touch
        // causes this CAS-backed operation to fail/reload without ever resurrecting the session.
        session = await _sessions.TouchAsync(
                session,
                now,
                _sessionOptions.IdleTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }

        return new SelectedSessionRequestPrincipal(
            session.AccountId,
            tenant,
            principal,
            party.PartyId,
            session.MembershipId,
            session.MembershipOwnerVersion,
            session.PinnedGrantOwnerVersions,
            session.AuthorizationEpoch,
            session.SessionCorrelationId,
            session.CoordinationCorrelationId);
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

    private static bool MatchesPinnedAuthority(
        WebUserSessionRecord session,
        TenantMembershipSnapshot? membership)
    {
        if (membership is null ||
            membership.Status != TenantMembershipStatus.Active ||
            !string.Equals(membership.AccountId, session.AccountId, StringComparison.Ordinal) ||
            !string.Equals(membership.TenantId, session.TenantId, StringComparison.Ordinal) ||
            !string.Equals(membership.MembershipId, session.MembershipId, StringComparison.Ordinal) ||
            membership.OwnerVersion != session.MembershipOwnerVersion ||
            !string.Equals(
                membership.CanonicalPrincipalId,
                session.TenantPrincipalId,
                StringComparison.Ordinal) ||
            membership.AuthorizationEpoch != session.AuthorizationEpoch)
        {
            return false;
        }

        var pinnedGrant = session.PinnedGrantOwnerVersions[0];
        return string.Equals(pinnedGrant.GrantId, membership.GrantId, StringComparison.Ordinal) &&
               pinnedGrant.OwnerVersion == membership.GrantOwnerVersion;
    }
}
