using System.Buffers.Text;
using System.Data;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>A raw antiforgery token returned once alongside its digest-only durable state.</summary>
internal sealed record WebAntiforgeryIssue(
    string Token,
    WebAntiforgeryStateRecord State);

/// <summary>
/// Digest-only, one-time antiforgery persistence. A subject has at most one active state; rotating
/// it revokes the prior state before the replacement becomes visible.
/// </summary>
internal sealed class WebAntiforgeryStateStore
{
    private const int TokenByteLength = 32;
    private const int MaximumTokenLength = 256;
    private const int MaximumCoordinateLength = 128;

    private readonly IDbContextFactory<NodeLocalWebSessionDbContext> _contextFactory;
    private readonly TimeProvider _timeProvider;

    public WebAntiforgeryStateStore(
        IDbContextFactory<NodeLocalWebSessionDbContext> contextFactory,
        TimeProvider timeProvider)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Builds a fresh state for insertion in an authority transition's existing transaction.</summary>
    internal static WebAntiforgeryIssue CreateState(
        WebCookieAudience audience,
        string accountId,
        string subjectCorrelationId,
        string coordinationCorrelationId,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset absoluteExpiresAtUtc)
    {
        ValidateCoordinate(accountId, nameof(accountId));
        ValidateCoordinate(subjectCorrelationId, nameof(subjectCorrelationId));
        ValidateCoordinate(coordinationCorrelationId, nameof(coordinationCorrelationId));
        if (absoluteExpiresAtUtc <= issuedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(absoluteExpiresAtUtc));
        }

        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenByteLength));
        return new WebAntiforgeryIssue(
            token,
            new WebAntiforgeryStateRecord(
                AntiforgeryStateId: RandomHex(TokenByteLength),
                Audience: audience,
                AccountId: accountId,
                SubjectCorrelationId: subjectCorrelationId,
                TokenDigest: Digest(token),
                CoordinationCorrelationId: coordinationCorrelationId,
                IssuedAtUtc: issuedAtUtc,
                AbsoluteExpiresAtUtc: absoluteExpiresAtUtc,
                ConsumedAtUtc: null,
                RevokedAtUtc: null,
                OwnerVersion: 1));
    }

    /// <summary>
    /// Revokes the subject's active state and returns one fresh raw token, or <see langword="null"/>
    /// when a concurrent rotation wins the active-subject uniqueness fence.
    /// </summary>
    internal async Task<WebAntiforgeryIssue?> RotateAsync(
        WebCookieAudience audience,
        string accountId,
        string subjectCorrelationId,
        string coordinationCorrelationId,
        DateTimeOffset absoluteExpiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var replacement = CreateState(
            audience,
            accountId,
            subjectCorrelationId,
            coordinationCorrelationId,
            now,
            absoluteExpiresAtUtc);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);
        var active = await context.AntiforgeryStates
            .Where(row =>
                row.Audience == audience &&
                row.SubjectCorrelationId == subjectCorrelationId &&
                row.ConsumedAtUtc == null &&
                row.RevokedAtUtc == null)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var state in active)
        {
            context.Entry(state).CurrentValues.SetValues(state with
            {
                RevokedAtUtc = now,
                OwnerVersion = checked(state.OwnerVersion + 1),
            });
        }
        context.AntiforgeryStates.Add(replacement.State);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return replacement;
        }
        catch (DbUpdateException exception) when (IsActiveSubjectUniquenessConflict(exception))
        {
            return null;
        }
    }

    /// <summary>Consumes exactly one token when every audience and subject coordinate matches.</summary>
    internal async Task<bool> ConsumeAsync(
        WebCookieAudience audience,
        string accountId,
        string subjectCorrelationId,
        string? token,
        CancellationToken cancellationToken = default)
    {
        ValidateCoordinate(accountId, nameof(accountId));
        ValidateCoordinate(subjectCorrelationId, nameof(subjectCorrelationId));
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaximumTokenLength)
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        var tokenDigest = Digest(token);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);
        var state = await context.AntiforgeryStates.SingleOrDefaultAsync(
                row => row.TokenDigest == tokenDigest,
                cancellationToken)
            .ConfigureAwait(false);
        if (state is null ||
            state.Audience != audience ||
            !string.Equals(state.AccountId, accountId, StringComparison.Ordinal) ||
            !string.Equals(state.SubjectCorrelationId, subjectCorrelationId, StringComparison.Ordinal) ||
            state.ConsumedAtUtc is not null ||
            state.RevokedAtUtc is not null ||
            now < state.IssuedAtUtc ||
            now >= state.AbsoluteExpiresAtUtc)
        {
            return false;
        }

        context.Entry(state).CurrentValues.SetValues(state with
        {
            ConsumedAtUtc = now,
            OwnerVersion = checked(state.OwnerVersion + 1),
        });
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    internal static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void ValidateCoordinate(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumCoordinateLength)
        {
            throw new ArgumentException(
                "Antiforgery authority coordinates must be present and bounded.",
                parameterName);
        }
    }

    private static bool IsActiveSubjectUniquenessConflict(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 } sqlite &&
        sqlite.Message.Contains(
            "web_antiforgery_states.audience",
            StringComparison.Ordinal) &&
        sqlite.Message.Contains(
            "web_antiforgery_states.subject_correlation_id",
            StringComparison.Ordinal);

    private static string RandomHex(int byteLength) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(byteLength)).ToLowerInvariant();
}
