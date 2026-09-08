using System.Buffers.Text;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>Validates and rotates exact-audience, one-time web antiforgery state.</summary>
public interface IWebAntiforgeryPolicy
{
    /// <summary>Issues browser-bound anonymous state when no authenticated audience is present.</summary>
    Task<bool> IssueAnonymousAsync(HttpContext context);

    /// <summary>Consumes browser-bound anonymous state exactly once.</summary>
    Task<bool> ConsumeAnonymousAsync(HttpContext context);

    /// <summary>Consumes account-challenge state exactly once.</summary>
    Task<bool> ConsumeChallengeAsync(HttpContext context, string challengeHandle);

    /// <summary>Consumes selected-session state exactly once.</summary>
    Task<bool> ConsumeSelectedAsync(HttpContext context, string selectedHandle);

    /// <summary>Consumes installation-session state exactly once.</summary>
    Task<bool> ConsumeInstallationAsync(HttpContext context, string installationHandle);

    /// <summary>Revokes and replaces the active account-challenge state.</summary>
    Task<bool> RotateChallengeAsync(HttpContext context, string challengeHandle);

    /// <summary>Revokes and replaces the active selected-session state.</summary>
    /// <remarks>
    /// <para>
    /// <b>Where a consuming route should call this (earlier repository ticket #3311).</b> Immediately after a successful
    /// <see cref="ConsumeSelectedAsync"/>, NOT on the route's success branch. The token is single-use, so
    /// every path below the consume has already spent it; re-issuing at the top means each one — success,
    /// business refusal, fault — answers with a usable token, and a later early return cannot silently
    /// stop doing so.
    /// </para>
    /// <para>
    /// <b>What this is and is not worth.</b> It is NOT today a fix for a wedged browser. Every current
    /// web-client caller of a mutating route fetches a fresh token from <c>GET /api/session/antiforgery</c>
    /// immediately beforehand and ignores the response header, and that route rotates on a live selected
    /// cookie while requiring no token itself — so a spent token cannot strand a live session. What it buys
    /// is a round-trip the caller may skip, agreement with the contract the clients already document, and
    /// removal of a latent trap: the tenant picker DOES thread the response header, so a client that
    /// adopted that pattern against a route which re-issued only on success would wedge on every refusal.
    /// </para>
    /// <para>
    /// <b>Safe on a failure path.</b> A caller past the consume already presented a valid token, and the
    /// replacement binds to the SAME handle with the session's own absolute expiry, so nothing is granted
    /// and no session is extended. It is itself fail-closed: rotation re-resolves the subject through the
    /// session row and the revocation table, so a revoked or expired session cannot be rotated at all.
    /// Re-issue therefore never has to be withheld as a way of ending a session — revocation is enforced
    /// at consume time.
    /// </para>
    /// <para>
    /// The one route that must NOT follow this shape is the tenant switch: it mints a NEW selected handle
    /// on success, so its token has to be bound after the switch rather than before it.
    /// </para>
    /// </remarks>
    Task<bool> RotateSelectedAsync(HttpContext context, string selectedHandle);

    /// <summary>Emits a newly issued raw token in the antiforgery response header.</summary>
    void EmitToken(HttpResponse response, string token);

    /// <summary>Expires the anonymous browser-binding cookie.</summary>
    void ExpireAnonymousBinding(HttpResponse response);
}

/// <summary>
/// Exact-audience antiforgery policy. Anonymous login is browser-bound by a separate HttpOnly
/// cookie; authenticated tokens bind to the exact digest-resolved cookie subject and never fall
/// through to another audience.
/// </summary>
internal sealed class WebAntiforgeryPolicy : IWebAntiforgeryPolicy
{
    internal const string HeaderName = "X-Harborline-Antiforgery";
    internal static readonly TimeSpan AnonymousLifetime = TimeSpan.FromMinutes(5);

    private const string AnonymousAccountId = "anonymous";

    private readonly IDbContextFactory<NodeLocalWebSessionDbContext> _contextFactory;
    private readonly WebSelectedSessionStore _selectedSessionStore;
    private readonly WebAntiforgeryStateStore _store;
    private readonly TimeProvider _timeProvider;

    public WebAntiforgeryPolicy(
        IDbContextFactory<NodeLocalWebSessionDbContext> contextFactory,
        WebSelectedSessionStore selectedSessionStore,
        WebAntiforgeryStateStore store,
        TimeProvider timeProvider)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _selectedSessionStore =
            selectedSessionStore ?? throw new ArgumentNullException(nameof(selectedSessionStore));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<bool> IssueAnonymousAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (HasAnyAuthenticatedAudience(context.Request))
        {
            return false;
        }

        var binding = context.Request.Cookies[WebSessionCookieNames.AnonymousAntiforgery];
        if (!IsBoundedToken(binding))
        {
            binding = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        }
        var expiresAt = _timeProvider.GetUtcNow() + AnonymousLifetime;
        var issue = await _store.RotateAsync(
                WebCookieAudience.AccountChallenge,
                AnonymousAccountId,
                WebAntiforgeryStateStore.Digest(binding!),
                RandomHex(32),
                expiresAt,
                context.RequestAborted)
            .ConfigureAwait(false);
        if (issue is null)
        {
            return false;
        }
        context.Response.Cookies.Append(
            WebSessionCookieNames.AnonymousAntiforgery,
            binding!,
            WebSessionCookieNames.For(expiresAt));
        EmitToken(context.Response, issue.Token);
        return true;
    }

    public Task<bool> ConsumeAnonymousAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (HasAnyAuthenticatedAudience(context.Request))
        {
            return Task.FromResult(false);
        }
        var binding = context.Request.Cookies[WebSessionCookieNames.AnonymousAntiforgery];
        return !IsBoundedToken(binding)
            ? Task.FromResult(false)
            : _store.ConsumeAsync(
                WebCookieAudience.AccountChallenge,
                AnonymousAccountId,
                WebAntiforgeryStateStore.Digest(binding!),
                ReadToken(context.Request),
                context.RequestAborted);
    }

    public Task<bool> ConsumeChallengeAsync(HttpContext context, string challengeHandle) =>
        ConsumeAudienceAsync(context, WebCookieAudience.AccountChallenge, challengeHandle);

    public Task<bool> ConsumeSelectedAsync(HttpContext context, string selectedHandle) =>
        ConsumeAudienceAsync(context, WebCookieAudience.SelectedSession, selectedHandle);

    public Task<bool> ConsumeInstallationAsync(HttpContext context, string installationHandle) =>
        ConsumeAudienceAsync(context, WebCookieAudience.InstallationSession, installationHandle);

    public Task<bool> RotateChallengeAsync(HttpContext context, string challengeHandle) =>
        RotateAudienceAsync(context, WebCookieAudience.AccountChallenge, challengeHandle);

    public Task<bool> RotateSelectedAsync(HttpContext context, string selectedHandle) =>
        RotateAudienceAsync(context, WebCookieAudience.SelectedSession, selectedHandle);

    private async Task<bool> RotateAudienceAsync(
        HttpContext context,
        WebCookieAudience audience,
        string handle)
    {
        ArgumentNullException.ThrowIfNull(context);
        var subject = await ResolveSubjectAsync(
                audience,
                handle,
                context.RequestAborted)
            .ConfigureAwait(false);
        if (subject is null)
        {
            return false;
        }
        var issue = await _store.RotateAsync(
                subject.Audience,
                subject.AccountId,
                subject.SubjectCorrelationId,
                RandomHex(32),
                subject.ExpiresAtUtc,
                context.RequestAborted)
            .ConfigureAwait(false);
        if (issue is null)
        {
            return false;
        }
        EmitToken(context.Response, issue.Token);
        return true;
    }

    public void EmitToken(HttpResponse response, string token)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        response.Headers[HeaderName] = token;
    }

    public void ExpireAnonymousBinding(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Cookies.Delete(
            WebSessionCookieNames.AnonymousAntiforgery,
            WebSessionCookieNames.ForDeletion());
    }

    private async Task<bool> ConsumeAudienceAsync(
        HttpContext context,
        WebCookieAudience audience,
        string handle)
    {
        ArgumentNullException.ThrowIfNull(context);
        var subject = await ResolveSubjectAsync(audience, handle, context.RequestAborted)
            .ConfigureAwait(false);
        return subject is not null && await _store.ConsumeAsync(
                subject.Audience,
                subject.AccountId,
                subject.SubjectCorrelationId,
                ReadToken(context.Request),
                context.RequestAborted)
            .ConfigureAwait(false);
    }

    private async Task<AntiforgerySubject?> ResolveSubjectAsync(
        WebCookieAudience audience,
        string? handle,
        CancellationToken cancellationToken)
    {
        if (!IsBoundedToken(handle))
        {
            return null;
        }
        var digest = WebAntiforgeryStateStore.Digest(handle!);
        var now = _timeProvider.GetUtcNow();
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        switch (audience)
        {
            case WebCookieAudience.AccountChallenge:
            {
                var challenge = await context.AccountAccessChallenges.AsNoTracking()
                    .SingleOrDefaultAsync(row => row.HandleDigest == digest, cancellationToken)
                    .ConfigureAwait(false);
                return challenge is null ||
                       challenge.ConsumedAtUtc is not null ||
                       challenge.RevokedAtUtc is not null ||
                       now < challenge.IssuedAtUtc ||
                       challenge.IsExpired(now)
                    ? null
                    : new AntiforgerySubject(
                        audience,
                        challenge.AccountId,
                        challenge.ChallengeId,
                        challenge.AbsoluteExpiresAtUtc);
            }
            case WebCookieAudience.SelectedSession:
            {
                var session = await _selectedSessionStore.FindStoredAsync(
                        context,
                        digest,
                        track: false,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (session is null || now < session.IssuedAtUtc ||
                    now >= session.IdleExpiresAtUtc || now >= session.AbsoluteExpiresAtUtc ||
                    await IsRevokedAsync(context, audience, session.SessionCorrelationId, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return null;
                }
                return new AntiforgerySubject(
                    audience,
                    session.AccountId,
                    session.SessionCorrelationId,
                    session.AbsoluteExpiresAtUtc);
            }
            case WebCookieAudience.InstallationSession:
            {
                var session = await context.InstallationSessions.AsNoTracking()
                    .SingleOrDefaultAsync(row => row.HandleDigest == digest, cancellationToken)
                    .ConfigureAwait(false);
                if (session is null || now < session.IssuedAtUtc || now >= session.AbsoluteExpiresAtUtc ||
                    await IsRevokedAsync(context, audience, session.SessionCorrelationId, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return null;
                }
                return new AntiforgerySubject(
                    audience,
                    session.AccountId,
                    session.SessionCorrelationId,
                    session.AbsoluteExpiresAtUtc);
            }
            default:
                return null;
        }
    }

    private static Task<bool> IsRevokedAsync(
        NodeLocalWebSessionDbContext context,
        WebCookieAudience audience,
        string subjectCorrelationId,
        CancellationToken cancellationToken) =>
        context.Revocations.AsNoTracking().AnyAsync(row =>
                row.Audience == audience && row.SubjectCorrelationId == subjectCorrelationId,
            cancellationToken);

    private static bool HasAnyAuthenticatedAudience(HttpRequest request) =>
        request.Cookies.ContainsKey(WebSessionCookieNames.Challenge) ||
        request.Cookies.ContainsKey(WebSessionCookieNames.Selected) ||
        request.Cookies.ContainsKey(WebSessionCookieNames.Installation);

    private static string? ReadToken(HttpRequest request)
    {
        var values = request.Headers[HeaderName];
        return values.Count == 1 && IsBoundedToken(values[0]) ? values[0] : null;
    }

    private static bool IsBoundedToken(string? value) =>
        value is { Length: > 0 and <= 256 } && !string.IsNullOrWhiteSpace(value);

    private static string RandomHex(int byteLength) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(byteLength)).ToLowerInvariant();

    private sealed record AntiforgerySubject(
        WebCookieAudience Audience,
        string AccountId,
        string SubjectCorrelationId,
        DateTimeOffset ExpiresAtUtc);
}
