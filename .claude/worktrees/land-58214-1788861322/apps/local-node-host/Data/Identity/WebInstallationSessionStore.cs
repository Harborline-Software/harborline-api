using System.Data;

using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>
/// Dormant digest-only persistence primitives for the installation-administration audience.
/// Minting, authentication, cookies, routes, and installation-grant admission belong to later IAS cards.
/// </summary>
internal sealed class WebInstallationSessionStore
{
    internal static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(15);

    private const int Sha256HexLength = 64;
    private const int MaximumCoordinateLength = 128;

    private readonly IDbContextFactory<NodeLocalWebSessionDbContext> _contextFactory;

    internal WebInstallationSessionStore(
        IDbContextFactory<NodeLocalWebSessionDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    /// <summary>Persists an already-digested, purpose-bound installation session.</summary>
    internal async Task CreateAsync(
        WebInstallationSessionRecord session,
        CancellationToken cancellationToken = default)
    {
        ValidateStoredSession(session);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        context.InstallationSessions.Add(session);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves one active session by digest and refuses expiry, revocation, or account-version drift.
    /// </summary>
    internal async Task<WebInstallationSessionRecord?> FindActiveAsync(
        string handleDigest,
        long currentAccountSecurityVersion,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateDigest(handleDigest, nameof(handleDigest));
        if (currentAccountSecurityVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentAccountSecurityVersion));
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var session = await context.InstallationSessions.AsNoTracking()
            .SingleOrDefaultAsync(row => row.HandleDigest == handleDigest, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }

        ValidateStoredSession(session);
        if (session.AccountSecurityVersion != currentAccountSecurityVersion ||
            atUtc < session.IssuedAtUtc || atUtc >= session.AbsoluteExpiresAtUtc)
        {
            return null;
        }

        var revoked = await context.Revocations.AsNoTracking().AnyAsync(row =>
                row.Audience == WebCookieAudience.InstallationSession &&
                row.SubjectCorrelationId == session.SessionCorrelationId,
            cancellationToken).ConfigureAwait(false);
        return revoked ? null : session;
    }

    /// <summary>Appends exact, idempotent revocation evidence for one digest.</summary>
    internal async Task<bool> RevokeAsync(
        string handleDigest,
        string coordinationCorrelationId,
        string reasonCode,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateDigest(handleDigest, nameof(handleDigest));
        ValidateCoordinate(coordinationCorrelationId, nameof(coordinationCorrelationId));
        ValidateCoordinate(reasonCode, nameof(reasonCode));

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var session = await context.InstallationSessions.AsNoTracking()
            .SingleOrDefaultAsync(row => row.HandleDigest == handleDigest, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return false;
        }

        ValidateStoredSession(session);
        if (revokedAtUtc < session.IssuedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(revokedAtUtc));
        }

        if (await IsRevokedAsync(context, session.SessionCorrelationId, cancellationToken)
            .ConfigureAwait(false))
        {
            return false;
        }

        context.Revocations.Add(CreateRevocation(
            session,
            coordinationCorrelationId,
            reasonCode,
            revokedAtUtc));
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            if (await IsRevokedAsync(context, session.SessionCorrelationId, cancellationToken)
                .ConfigureAwait(false))
            {
                return false;
            }
            throw;
        }
    }

    /// <summary>
    /// Revokes every installation session for an account that does not pin the current security version.
    /// </summary>
    internal async Task<int> RevokeStaleAccountSecurityVersionsAsync(
        string accountId,
        long currentAccountSecurityVersion,
        string coordinationCorrelationId,
        string reasonCode,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateCoordinate(accountId, nameof(accountId), 64);
        ValidateCoordinate(coordinationCorrelationId, nameof(coordinationCorrelationId));
        ValidateCoordinate(reasonCode, nameof(reasonCode));
        if (currentAccountSecurityVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentAccountSecurityVersion));
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);
        var stale = await context.InstallationSessions.AsNoTracking()
            .Where(row => row.AccountId == accountId &&
                          row.AccountSecurityVersion != currentAccountSecurityVersion)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var session in stale)
        {
            ValidateStoredSession(session);
            if (revokedAtUtc < session.IssuedAtUtc)
            {
                throw new ArgumentOutOfRangeException(nameof(revokedAtUtc));
            }
        }

        var subjectIds = stale.Select(row => row.SessionCorrelationId).ToArray();
        var revokedSubjects = subjectIds.Length == 0
            ? []
            : await context.Revocations.AsNoTracking()
                .Where(row => row.Audience == WebCookieAudience.InstallationSession &&
                              subjectIds.Contains(row.SubjectCorrelationId))
                .Select(row => row.SubjectCorrelationId)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        var alreadyRevoked = revokedSubjects.ToHashSet(StringComparer.Ordinal);
        var revocations = stale
            .Where(session => !alreadyRevoked.Contains(session.SessionCorrelationId))
            .Select(session => CreateRevocation(
                session,
                coordinationCorrelationId,
                reasonCode,
                revokedAtUtc))
            .ToArray();
        if (revocations.Length == 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        context.Revocations.AddRange(revocations);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return revocations.Length;
    }

    private static WebSessionRevocationRecord CreateRevocation(
        WebInstallationSessionRecord session,
        string coordinationCorrelationId,
        string reasonCode,
        DateTimeOffset revokedAtUtc) =>
        new(
            RevocationId: Guid.NewGuid().ToString("N"),
            Audience: WebCookieAudience.InstallationSession,
            AccountId: session.AccountId,
            SubjectCorrelationId: session.SessionCorrelationId,
            SupersededByCorrelationId: null,
            ReasonCode: reasonCode,
            CoordinationCorrelationId: coordinationCorrelationId,
            RevokedAtUtc: revokedAtUtc,
            OwnerVersion: 1);

    private static Task<bool> IsRevokedAsync(
        NodeLocalWebSessionDbContext context,
        string subjectCorrelationId,
        CancellationToken cancellationToken) =>
        context.Revocations.AsNoTracking().AnyAsync(row =>
                row.Audience == WebCookieAudience.InstallationSession &&
                row.SubjectCorrelationId == subjectCorrelationId,
            cancellationToken);

    private static void ValidateStoredSession(WebInstallationSessionRecord session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateCoordinate(session.SessionCorrelationId, nameof(session.SessionCorrelationId));
        ValidateCoordinate(session.AccountId, nameof(session.AccountId), 64);
        ValidateCoordinate(session.InstallationGrantId, nameof(session.InstallationGrantId), 64);
        ValidateCoordinate(session.AntiforgeryStateId, nameof(session.AntiforgeryStateId), 64);
        ValidateCoordinate(
            session.CoordinationCorrelationId,
            nameof(session.CoordinationCorrelationId));
        ValidateDigest(session.HandleDigest, nameof(session.HandleDigest));
        if (session.AccountSecurityVersion <= 0 ||
            session.InstallationGrantOwnerVersion <= 0 ||
            session.AuthorizationEpoch <= 0 ||
            session.OwnerVersion <= 0 ||
            session.AbsoluteExpiresAtUtc <= session.IssuedAtUtc ||
            session.AbsoluteExpiresAtUtc - session.IssuedAtUtc > MaximumLifetime)
        {
            throw new InvalidOperationException(
                "identity.installation_session_invalid: durable session evidence is malformed.");
        }
    }

    private static void ValidateDigest(string digest, string parameterName)
    {
        if (digest is not { Length: Sha256HexLength } ||
            digest.Any(character => character is not (>= '0' and <= '9') and
                not (>= 'A' and <= 'F')))
        {
            throw new ArgumentException(
                "Installation sessions require one uppercase SHA-256 digest.",
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
            throw new ArgumentException("Installation-session coordinate is invalid.", parameterName);
        }
    }
}
