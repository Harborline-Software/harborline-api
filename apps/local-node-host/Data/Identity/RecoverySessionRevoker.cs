using System.Data;

using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>Per-audience count of the durable revocation evidence staged by one recovery.</summary>
internal sealed record RecoveryRevocationCounts(
    int SelectedSessions,
    int InstallationSessions,
    int AccountChallenges)
{
    public int Total => SelectedSessions + InstallationSessions + AccountChallenges;
}

/// <summary>
/// Stages durable revocation evidence for EVERY web session of one installation account, across ALL
/// tenants and ALL THREE cookie audiences (MTW-2 #3013, ADR 0160 R3-C/R3-E). This is the "assert
/// against the session store" evidence the recovery gate requires; the account-security-version bump
/// performed by the credential rotation is the primary lever that also makes every one of these
/// records fail live revalidation on its next request (each audience record pins
/// <c>AccountSecurityVersion</c>). Both together satisfy "revoke every audience before success":
/// the tombstones are staged durably here, and the version bump is what actually invalidates.
///
/// Idempotent on the revocation's <c>(Audience, SubjectCorrelationId)</c> unique key, so a resumed
/// recovery (F2) re-runs it as a no-op for anything already tombstoned.
/// </summary>
internal sealed class RecoverySessionRevoker(
    IDbContextFactory<NodeLocalWebSessionDbContext> contextFactory)
{
    private readonly IDbContextFactory<NodeLocalWebSessionDbContext> _contextFactory =
        contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    public async Task<RecoveryRevocationCounts> RevokeAllForAccountAsync(
        string accountId,
        string coordinationCorrelationId,
        string reasonCode,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(coordinationCorrelationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);

        var userSubjects = await context.UserSessions.AsNoTracking()
            .Where(row => row.AccountId == accountId)
            .Select(row => row.SessionCorrelationId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var installationSubjects = await context.InstallationSessions.AsNoTracking()
            .Where(row => row.AccountId == accountId)
            .Select(row => row.SessionCorrelationId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var challengeSubjects = await context.AccountAccessChallenges.AsNoTracking()
            .Where(row => row.AccountId == accountId)
            .Select(row => row.ChallengeId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

        var selected = await StageAsync(
            context, WebCookieAudience.SelectedSession, accountId, userSubjects,
            coordinationCorrelationId, reasonCode, revokedAtUtc, cancellationToken).ConfigureAwait(false);
        var installation = await StageAsync(
            context, WebCookieAudience.InstallationSession, accountId, installationSubjects,
            coordinationCorrelationId, reasonCode, revokedAtUtc, cancellationToken).ConfigureAwait(false);
        var challenge = await StageAsync(
            context, WebCookieAudience.AccountChallenge, accountId, challengeSubjects,
            coordinationCorrelationId, reasonCode, revokedAtUtc, cancellationToken).ConfigureAwait(false);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RecoveryRevocationCounts(selected, installation, challenge);
    }

    private static async Task<int> StageAsync(
        NodeLocalWebSessionDbContext context,
        WebCookieAudience audience,
        string accountId,
        IReadOnlyCollection<string> subjectIds,
        string coordinationCorrelationId,
        string reasonCode,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken)
    {
        if (subjectIds.Count == 0)
        {
            return 0;
        }

        var subjectArray = subjectIds.ToArray();
        var alreadyRevoked = (await context.Revocations.AsNoTracking()
                .Where(row => row.Audience == audience && subjectArray.Contains(row.SubjectCorrelationId))
                .Select(row => row.SubjectCorrelationId)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        var staged = 0;
        foreach (var subject in subjectArray)
        {
            if (alreadyRevoked.Contains(subject))
            {
                continue;
            }

            context.Revocations.Add(new WebSessionRevocationRecord(
                RevocationId: Guid.NewGuid().ToString("N"),
                Audience: audience,
                AccountId: accountId,
                SubjectCorrelationId: subject,
                SupersededByCorrelationId: null,
                ReasonCode: reasonCode,
                CoordinationCorrelationId: coordinationCorrelationId,
                RevokedAtUtc: revokedAtUtc,
                OwnerVersion: 1));
            staged++;
        }

        return staged;
    }
}
