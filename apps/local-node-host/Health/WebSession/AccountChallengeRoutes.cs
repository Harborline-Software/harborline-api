using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>The pre-tenant installation-account credential exchange.</summary>
/// <remarks>
/// <b>The ADR 0160 R3-H cutover gate lives on the AUTHORITY, not here.</b>
/// <see cref="IWebAccountAccessChallengeIssuer"/>'s implementation consults
/// <c>IInstallationIdentityV1AuthorityGate</c> for the <c>AccountChallenge</c> legacy bearer audience
/// before it mints anything, so every caller of that seam is covered rather than only this transport
/// — and a refusal arrives here as a typed outcome, answered with the same generic 401 as a
/// credential miss. Do not re-report this route as an ungated accept path, and do not add a second
/// check: the paired CONSUME half is gated in <c>WebTenantSelectionAuthority.SelectAsync</c>, and
/// both are proved against a real committed marker by
/// <c>LegacyV1CutoverAuthorityProof.ProvePostCutoverV1BearerIsRejected</c>.
/// </remarks>
public static class AccountChallengeRoutes
{
    /// <summary>Exact pre-auth route; descendants remain caller-credential gated.</summary>
    public const string IssuePath = "/api/session/account-challenge";

    /// <summary>Credential request. Both unknown users and wrong passwords receive one generic refusal.</summary>
    public sealed record IssueRequest(string? Username, string? Password);

    /// <summary>
    /// One workspace this account may select. The ID ONLY, deliberately.
    /// </summary>
    /// <remarks>
    /// A display label is NOT sent. <c>InstallationTenantCandidate.DisplayLabel</c> is currently the
    /// tenant GUID itself, so a label field would duplicate the id and carry zero information — and its
    /// own doc says that holds only "until a tenant-owned label reader is available". The moment that
    /// reader lands, a mapped-through label would begin emitting tenant-owned text on this route with
    /// no re-review, because the file is already on the locator's ratified-consumer allowlist. Adding
    /// it later is a deliberate act; leaving it now is a silent widening. The client renders the id.
    /// </remarks>
    private sealed record TenantCandidateView(string TenantId);

    /// <summary>
    /// The challenge result, plus the caller's OWN 0/1/N membership classification (earlier repository ticket #3329 step 2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is not enumeration.</b> The 2026-07-20 client-boundary ruling forbids the CLIENT
    /// discovering tenants, and named obtaining this classification as the deferred "SES-07 challenge
    /// transport" follow-up — which is this. The password has already been verified by the time these
    /// fields are populated, and they describe only memberships this very account holds. Nobody else's
    /// tenancy is observable, and an unauthenticated caller never reaches this branch: a refusal returns
    /// before it, with the same generic 401 for an unknown user and a wrong password.
    /// </para>
    /// <para>
    /// <b>Why the client needs it.</b> <c>POST /api/session/select</c> resolves a null tenant id only when
    /// the account has EXACTLY ONE candidate. With 0 or ≥2 the null form is refused, so a browser that can
    /// only ever send null could not sign such an account in at all — and, worse, its failed select
    /// strands a challenge cookie. The picker needs the 0/1/N shape injected because it performs no
    /// discovery of its own.
    /// </para>
    /// <para>
    /// <b>Fail-soft, deliberately.</b> Classification is an affordance, not authority: the server still
    /// re-resolves candidates on select and is the only thing that decides what is usable. If the locator
    /// is unavailable the challenge still succeeds with <c>unknown</c>, and the client falls back to the
    /// null-tenant Single path — exactly the behaviour before this field existed. A classification failure
    /// must never cost a sign-in that would otherwise work.
    /// </para>
    /// </remarks>
    private sealed record IssueResponse(
        DateTimeOffset ExpiresAt,
        string Classification,
        IReadOnlyList<TenantCandidateView> Candidates);

    private sealed record ErrorResponse(string Error, string Message);

    /// <summary>Maps the challenge route closed over the outer-container issuer.</summary>
    public static void Map(
        IEndpointRouteBuilder app,
        IWebAccountAccessChallengeIssuer issuer,
        IWebAntiforgeryPolicy antiforgery,
        WebLoginRateLimiter loginRateLimiter)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(antiforgery);
        ArgumentNullException.ThrowIfNull(loginRateLimiter);

        app.MapPost(
            IssuePath,
            (IssueRequest request, HttpContext context) =>
                IssueAsync(issuer, antiforgery, loginRateLimiter, request, context));
    }

    internal static async Task<IResult> IssueAsync(
        IWebAccountAccessChallengeIssuer issuer,
        IWebAntiforgeryPolicy antiforgery,
        WebLoginRateLimiter loginRateLimiter,
        IssueRequest request,
        HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(loginRateLimiter);
        context.Response.Headers.CacheControl = "no-store";
        if (!await antiforgery.ConsumeAnonymousAsync(context).ConfigureAwait(false))
        {
            return Results.Json(
                new ErrorResponse("antiforgery_failed", "Antiforgery validation failed."),
                statusCode: StatusCodes.Status400BadRequest);
        }

        // The account issuer resolves usernames as Trim + FormKC + uppercase. Use that exact
        // identity for the limiter so case/whitespace variants share one budget. The legacy route
        // intentionally passes its raw Ordinal identity because its authority resolves differently.
        var resolutionIdentity = WebUsernameNormalizer.TryNormalize(request?.Username);
        var source = context.Connection.RemoteIpAddress?.MapToIPv6().ToString();
        if (!loginRateLimiter.CheckAttempt(resolutionIdentity, source).IsAllowed)
        {
            _ = await antiforgery.IssueAnonymousAsync(context).ConfigureAwait(false);
            return Results.Json(
                new ErrorResponse("authentication_failed", "Authentication failed."),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var settled = false;
        try
        {
            var result = await issuer
                .IssueAsync(request?.Username, request?.Password, context.RequestAborted)
                .ConfigureAwait(false);
            var challenge = result.Challenge;
            if (challenge is null)
            {
                // Refusal scope is determined by the issuer's verification boundary, not only by
                // the reason enum. Any refusal after VerifyRan paid the Argon2id cost and consumes
                // this challenge-route budget, including malformed usernames and disabled or
                // legacy-algorithm accounts. Pre-verify identity-independent refusals release the
                // reservation. The legacy route intentionally retains its reason-only rule because
                // its malformed input returns before hashing.
                if (result.FailureReason == WebLoginFailureReason.CredentialMismatch ||
                    result.VerifyRan)
                {
                    loginRateLimiter.RecordFailure(resolutionIdentity, source);
                    settled = true;
                }

                _ = await antiforgery.IssueAnonymousAsync(context).ConfigureAwait(false);
                return Results.Json(
                    new ErrorResponse("authentication_failed", "Authentication failed."),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            loginRateLimiter.RecordSuccess(resolutionIdentity, source);
            settled = true;

            context.Response.Cookies.Append(
                WebSessionCookieNames.Challenge,
                challenge.Handle,
                WebSessionCookieNames.For(challenge.ExpiresAtUtc));
            antiforgery.EmitToken(context.Response, challenge.AntiforgeryToken);
            antiforgery.ExpireAnonymousBinding(context.Response);

            var (classification, candidates) = await ClassifyAsync(
                    challenge.AccountId,
                    context)
                .ConfigureAwait(false);
            return Results.Ok(new IssueResponse(challenge.ExpiresAtUtc, classification, candidates));
        }
        finally
        {
            if (!settled)
            {
                // Cancellation or a transient issuer/store fault must not hold the budget until the
                // full sliding window expires. Completed outcomes already dequeued their reservation.
                loginRateLimiter.ReleaseAttempt(resolutionIdentity, source);
            }
        }
    }

    /// <summary>
    /// The caller's own 0/1/N classification. Resolved from the request container rather than a
    /// constructor parameter so the public <see cref="Map"/> signature does not have to expose an
    /// internal locator type.
    /// </summary>
    private static async Task<(string Classification, IReadOnlyList<TenantCandidateView> Candidates)>
        ClassifyAsync(string accountId, HttpContext context)
    {
        var locator = context.RequestServices.GetService<IInstallationTenantCandidateLocator>();
        if (locator is null)
        {
            return ("unknown", Array.Empty<TenantCandidateView>());
        }

        try
        {
            var candidates = await locator
                .ListForAccountAsync(new PrincipalUserId(accountId), context.RequestAborted)
                .ConfigureAwait(false);
            return candidates.Count switch
            {
                0 => ("none", Array.Empty<TenantCandidateView>()),
                1 => ("single", Array.Empty<TenantCandidateView>()),
                _ => ("multiple", candidates
                        .Select(c => new TenantCandidateView(c.TenantId.Value))
                        .ToArray()),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail SOFT, but never SILENT. The challenge itself succeeded and the cookie is already
            // set; refusing here would turn a working sign-in into a failure over an affordance, so the
            // client falls back to the null-tenant Single path exactly as it did before this field.
            //
            // The log matters because the locator has ONE deliberate fail-loud path: a corrupt completed
            // receipt fails the whole enumeration rather than being skipped, precisely so
            // authority-evidence damage is not hidden. Swallowing that to "unknown" with no signal would
            // defeat it. Only the exception TYPE is recorded — no account id, no tenant, no candidate —
            // so the wire response is unchanged and this adds no correlation handle.
            context.RequestServices
                .GetService<ILoggerFactory>()
                ?.CreateLogger("Harborline.Api.LocalNodeHost.Health.WebSession.AccountChallengeRoutes")
                .LogWarning(
                    "Tenant classification failed ({Fault}); degrading to unknown.", ex.GetType().Name);
            return ("unknown", Array.Empty<TenantCandidateView>());
        }
    }
}
