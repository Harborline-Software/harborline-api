using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>Digest-only selected-session lookup and exact audience revocation.</summary>
internal sealed class WebSelectedSessionStore
{
    private const int Sha256HexLength = 64;
    private const int MaximumCoordinateLength = 128;
    private const string InvalidSessionMessage =
        "identity.selected_session_invalid: durable session evidence is malformed.";

    private readonly IDbContextFactory<NodeLocalWebSessionDbContext> _contextFactory;
    private readonly ILogger<WebSelectedSessionStore> _logger;
    // PUBLIC, not internal: the DI container selects constructors with GetConstructors(),
    // which returns PUBLIC instance constructors only. An internal constructor on a
    // DI-registered type cannot be resolved at all -- the host dies at startup with
    // "a suitable constructor could not be located". The class stays internal sealed, so
    // this widens nothing outside the assembly.

    public WebSelectedSessionStore(
        IDbContextFactory<NodeLocalWebSessionDbContext> contextFactory,
        ILogger<WebSelectedSessionStore>? logger = null)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _logger = logger ?? NullLogger<WebSelectedSessionStore>.Instance;
    }

    /// <summary>
    /// Resolves one active selected session. Revocation is checked after record and TTL validity and
    /// before any session authority is returned to a caller.
    /// </summary>
    internal async Task<WebUserSessionRecord?> FindActiveAsync(
        string handleDigest,
        long currentAccountSecurityVersion,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken = default)
    {
        var resolved = await FindForRecoveryAsync(
                handleDigest,
                currentAccountSecurityVersion,
                atUtc,
                cancellationToken)
            .ConfigureAwait(false);
        if (resolved is null)
        {
            return null;
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var revoked = await context.Revocations.AsNoTracking().AnyAsync(row =>
                row.Audience == WebCookieAudience.SelectedSession &&
                row.SubjectCorrelationId == resolved.SessionCorrelationId,
            cancellationToken).ConfigureAwait(false);
        return revoked ? null : resolved;
    }

    /// <summary>
    /// Resolves structurally valid, unexpired session evidence without treating revocation as absence.
    /// This is reserved for idempotent recovery of an already-committed logout.
    /// </summary>
    internal async Task<WebUserSessionRecord?> FindForRecoveryAsync(
        string handleDigest,
        long currentAccountSecurityVersion,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken = default)
    {
        if (currentAccountSecurityVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentAccountSecurityVersion));
        }

        var session = await FindForRecoveryAsync(handleDigest, atUtc, cancellationToken)
            .ConfigureAwait(false);
        return session is not null && session.AccountSecurityVersion == currentAccountSecurityVersion
            ? session
            : null;
    }

    internal async Task<WebUserSessionRecord?> FindForRecoveryAsync(
        string handleDigest,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken = default)
    {
        var session = await FindStoredAsync(handleDigest, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }
        return atUtc < session.IssuedAtUtc ||
               atUtc >= session.IdleExpiresAtUtc ||
               atUtc >= session.AbsoluteExpiresAtUtc
            ? null
            : session;
    }

    internal async Task<WebUserSessionRecord?> FindStoredAsync(
        string handleDigest,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await FindStoredAsync(context, handleDigest, track: false, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<WebUserSessionRecord?> FindStoredAsync(
        NodeLocalWebSessionDbContext context,
        string handleDigest,
        bool track,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateDigest(handleDigest, nameof(handleDigest));
        WebUserSessionRecord? session;
        try
        {
            var sessions = track
                ? context.UserSessions
                : context.UserSessions.AsNoTracking();
            session = await sessions
                .SingleOrDefaultAsync(row => row.HandleDigest == handleDigest, cancellationToken)
                .ConfigureAwait(false);
            if (session is not null)
            {
                ValidateStoredSession(session);
            }
        }
        catch (Exception exception) when (IsMalformedSession(exception))
        {
            _logger.LogError(
                exception,
                "{RefusalCode} Stored selected-session evidence is treated as absent.",
                InvalidSessionMessage);
            return null;
        }
        return session;
    }

    /// <summary>
    /// Slides one already-revalidated session's idle TTL without resurrecting revoked authority.
    /// Concurrent touches converge on the furthest valid expiry; any authority-coordinate mutation
    /// refuses instead of being overwritten.
    /// </summary>
    internal async Task<WebUserSessionRecord?> TouchAsync(
        WebUserSessionRecord revalidated,
        DateTimeOffset atUtc,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken = default)
    {
        ValidateStoredSession(revalidated);
        if (idleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            var durable = await FindStoredAsync(
                    context,
                    revalidated.HandleDigest,
                    track: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (durable is null)
            {
                return null;
            }
            if (!HasSameAuthorityCoordinates(revalidated, durable) ||
                atUtc < durable.IssuedAtUtc ||
                atUtc >= durable.IdleExpiresAtUtc ||
                atUtc >= durable.AbsoluteExpiresAtUtc ||
                await context.Revocations.AsNoTracking().AnyAsync(row =>
                        row.Audience == WebCookieAudience.SelectedSession &&
                        row.SubjectCorrelationId == durable.SessionCorrelationId,
                    cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var desiredIdleExpiry = atUtc + idleTimeout;
            if (desiredIdleExpiry > durable.AbsoluteExpiresAtUtc)
            {
                desiredIdleExpiry = durable.AbsoluteExpiresAtUtc;
            }
            if (desiredIdleExpiry <= durable.IdleExpiresAtUtc)
            {
                return durable;
            }

            var touched = durable with
            {
                IdleExpiresAtUtc = desiredIdleExpiry,
                OwnerVersion = checked(durable.OwnerVersion + 1),
            };
            context.Entry(durable).CurrentValues.SetValues(touched);
            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return touched;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another request touched the same session. Reload and converge; never overwrite
                // a changed authority coordinate or durable revocation.
                if (attempt == 2)
                {
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>Returns exact durable revocation evidence for recovery, when present.</summary>
    internal async Task<WebSessionRevocationRecord?> FindRevocationAsync(
        string subjectCorrelationId,
        CancellationToken cancellationToken = default)
    {
        ValidateCoordinate(subjectCorrelationId, nameof(subjectCorrelationId));
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await context.Revocations.AsNoTracking().SingleOrDefaultAsync(row =>
                row.Audience == WebCookieAudience.SelectedSession &&
                row.SubjectCorrelationId == subjectCorrelationId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Appends exact, idempotent selected-audience revocation evidence.</summary>
    internal async Task<bool> RevokeAsync(
        WebUserSessionRecord session,
        string coordinationCorrelationId,
        string reasonCode,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateStoredSession(session);
        ValidateCoordinate(coordinationCorrelationId, nameof(coordinationCorrelationId));
        ValidateCoordinate(reasonCode, nameof(reasonCode));
        if (revokedAtUtc < session.IssuedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(revokedAtUtc));
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var durable = await FindStoredAsync(
                context,
                session.HandleDigest,
                track: false,
                cancellationToken)
            .ConfigureAwait(false);
        if (durable is null || !string.Equals(
                durable.SessionCorrelationId,
                session.SessionCorrelationId,
                StringComparison.Ordinal))
        {
            return false;
        }

        var existing = await context.Revocations.AsNoTracking().SingleOrDefaultAsync(row =>
                row.Audience == WebCookieAudience.SelectedSession &&
                row.SubjectCorrelationId == session.SessionCorrelationId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            RequireSameRevocation(existing, coordinationCorrelationId, reasonCode);
            return false;
        }

        context.Revocations.Add(new WebSessionRevocationRecord(
            RevocationId: Guid.NewGuid().ToString("N"),
            Audience: WebCookieAudience.SelectedSession,
            AccountId: session.AccountId,
            SubjectCorrelationId: session.SessionCorrelationId,
            SupersededByCorrelationId: null,
            ReasonCode: reasonCode,
            CoordinationCorrelationId: coordinationCorrelationId,
            RevokedAtUtc: revokedAtUtc,
            OwnerVersion: 1));
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            existing = await context.Revocations.AsNoTracking().SingleOrDefaultAsync(row =>
                    row.Audience == WebCookieAudience.SelectedSession &&
                    row.SubjectCorrelationId == session.SessionCorrelationId,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                RequireSameRevocation(existing, coordinationCorrelationId, reasonCode);
                return false;
            }
            throw;
        }
    }

    private static void RequireSameRevocation(
        WebSessionRevocationRecord existing,
        string coordinationCorrelationId,
        string reasonCode)
    {
        if (!string.Equals(existing.CoordinationCorrelationId, coordinationCorrelationId,
                StringComparison.Ordinal) ||
            !string.Equals(existing.ReasonCode, reasonCode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "identity.selected_session_revocation_conflict: session has different revocation evidence.");
        }
    }

    private static bool HasSameAuthorityCoordinates(
        WebUserSessionRecord expected,
        WebUserSessionRecord actual) =>
        string.Equals(expected.SessionCorrelationId, actual.SessionCorrelationId, StringComparison.Ordinal) &&
        string.Equals(expected.AccountId, actual.AccountId, StringComparison.Ordinal) &&
        expected.AccountSecurityVersion == actual.AccountSecurityVersion &&
        string.Equals(expected.TenantId, actual.TenantId, StringComparison.Ordinal) &&
        string.Equals(expected.MembershipId, actual.MembershipId, StringComparison.Ordinal) &&
        expected.MembershipOwnerVersion == actual.MembershipOwnerVersion &&
        string.Equals(expected.TenantPrincipalId, actual.TenantPrincipalId, StringComparison.Ordinal) &&
        string.Equals(
            expected.CanonicalPartyReference,
            actual.CanonicalPartyReference,
            StringComparison.Ordinal) &&
        expected.PinnedGrantOwnerVersions.SequenceEqual(actual.PinnedGrantOwnerVersions) &&
        expected.AuthorizationEpoch == actual.AuthorizationEpoch &&
        string.Equals(expected.HandleDigest, actual.HandleDigest, StringComparison.Ordinal) &&
        string.Equals(expected.AntiforgeryStateId, actual.AntiforgeryStateId, StringComparison.Ordinal) &&
        string.Equals(
            expected.CoordinationCorrelationId,
            actual.CoordinationCorrelationId,
            StringComparison.Ordinal) &&
        expected.IssuedAtUtc == actual.IssuedAtUtc &&
        expected.AbsoluteExpiresAtUtc == actual.AbsoluteExpiresAtUtc;

    private static void ValidateStoredSession(WebUserSessionRecord session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateCoordinate(session.SessionCorrelationId, nameof(session.SessionCorrelationId));
        ValidateCoordinate(session.AccountId, nameof(session.AccountId), 64);
        ValidateCoordinate(session.TenantId, nameof(session.TenantId), 64);
        ValidateCoordinate(session.MembershipId, nameof(session.MembershipId), 64);
        ValidateCoordinate(session.TenantPrincipalId, nameof(session.TenantPrincipalId), 256);
        ValidateCoordinate(
            session.CanonicalPartyReference,
            nameof(session.CanonicalPartyReference),
            256);
        ValidateCoordinate(session.AntiforgeryStateId, nameof(session.AntiforgeryStateId), 64);
        ValidateCoordinate(session.CoordinationCorrelationId, nameof(session.CoordinationCorrelationId));
        ValidateDigest(session.HandleDigest, nameof(session.HandleDigest));
        if (session.AccountSecurityVersion <= 0 ||
            session.MembershipOwnerVersion <= 0 ||
            session.AuthorizationEpoch <= 0 ||
            session.OwnerVersion <= 0 ||
            session.PinnedGrantOwnerVersions.Any(grant =>
                string.IsNullOrWhiteSpace(grant.GrantId) ||
                grant.GrantId.Length > 64 ||
                grant.OwnerVersion <= 0) ||
            session.IdleExpiresAtUtc <= session.IssuedAtUtc ||
            session.AbsoluteExpiresAtUtc < session.IdleExpiresAtUtc)
        {
            throw new InvalidOperationException(InvalidSessionMessage);
        }
    }

    private static bool IsMalformedSession(Exception exception)
    {
        for (Exception? candidate = exception; candidate is not null; candidate = candidate.InnerException)
        {
            if (candidate is ArgumentException argument &&
                string.Equals(
                    argument.ParamName,
                    nameof(WebUserSessionRecord.PinnedGrantOwnerVersions),
                    StringComparison.Ordinal))
            {
                return true;
            }
            if (candidate is System.Text.Json.JsonException)
            {
                return true;
            }
            if (candidate is InvalidOperationException invalid &&
                string.Equals(invalid.Message, InvalidSessionMessage, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateDigest(string digest, string parameterName)
    {
        if (digest is not { Length: Sha256HexLength } ||
            digest.Any(character => character is not (>= '0' and <= '9') and
                not (>= 'A' and <= 'F')))
        {
            throw new ArgumentException(
                "Selected sessions require one uppercase SHA-256 digest.",
                parameterName);
        }
    }

    private static void ValidateCoordinate(
        string value,
        string parameterName,
        int maximumLength = MaximumCoordinateLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new ArgumentException("A bounded non-empty identity coordinate is required.", parameterName);
        }
    }
}
