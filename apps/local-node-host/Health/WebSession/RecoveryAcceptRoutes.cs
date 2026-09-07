using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>
/// The pre-login account-recovery redemption route (MTW-2 #3013). It is the ONE route that reaches
/// <see cref="AccountCredentialRecoveryService"/>. Anonymous / pre-caller-auth allowlisted: recovery
/// runs when the human is locked out, so the single-use recovery code IS the authority. The human
/// sends the password they chose and the NODE mints the credential artifact from it (ADR 0160 D3,
/// earlier repository ticket #3366). Redemption rotates the target account credential and revokes every account
/// session across all tenants; it mints no session (no bypass of the session authority) — the
/// recovered human re-authenticates through the standard login flow with the new password.
/// </summary>
/// <remarks>
/// Non-enumerating refusal: an unknown / expired / consumed-and-completed / revoked code and a
/// missing-or-inactive target account all return the SAME generic 401, so the surface never reveals
/// whether the code or the target account was the reason. Only a changed-replay (the same code
/// resumed under a different new credential) surfaces a distinguishable 409, exactly like account
/// setup.
/// </remarks>
public static class RecoveryAcceptRoutes
{
    /// <summary>Exact pre-auth route; the recovery code is the only authority it accepts.</summary>
    public const string RecoverPath = "/api/session/recovery-accept";

    /// <summary>
    /// Recovery request. Only <see cref="Code"/> proves authority; the password is the account
    /// holder's own new account material.
    /// </summary>
    /// <remarks>
    /// ADR 0160 D3: "Redemption chooses a password." The human sends the password they chose, over
    /// the same same-origin channel <c>/api/session/account-challenge</c> already carries a password
    /// on, and the node mints the canonical Argon2id artifact from it. That artifact was never a
    /// value a browser could supply: it carries the installation's own cost parameters, and its
    /// accepted encoding is PADDED Base64, which the standard PHC encoding does not emit. A future
    /// configured pepper would make it impossible outright — which is why this contract must not
    /// depend on the pepper being absent. See <see cref="IWebChosenCredentialFactory"/> for the full
    /// reasoning (earlier repository ticket #3366, the recovery twin of earlier repository ticket #3338).
    /// </remarks>
    public sealed record RecoverRequest(
        string? Code,
        string? Password)
    {
        /// <summary>
        /// Keeps both members out of <see cref="object.ToString"/>. A positional record prints every
        /// property, so one structured-logging call or exception message carrying <c>{Request}</c>
        /// would put the human's chosen password AND their single-use recovery code in the node log
        /// — the code is bearer authority until it is consumed, and the password is a live
        /// credential. Unlike account setup this record has no non-secret member, so the redaction
        /// covers the whole shape.
        /// </summary>
        /// <remarks>
        /// Declared <c>private</c>, not <c>protected override</c>: for a sealed record whose base
        /// type is <see cref="object"/> the compiler synthesizes <c>PrintMembers</c> as private, and
        /// a user declaration must match that signature or the type does not compile — CS8879
        /// ("Record member … must be private"), plus CS0115 for the absent base member.
        /// </remarks>
        private bool PrintMembers(StringBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Append("Code = <redacted>, Password = <redacted>");
            return true;
        }
    }

    private sealed record RecoverResponse(string Status);

    private sealed record ErrorResponse(string Error, string Message);

    /// <summary>Maps the recovery route closed over the outer-container authority (bug-2849).</summary>
    public static void Map(
        IEndpointRouteBuilder app,
        IAccountRecoveryAuthority authority,
        IWebChosenCredentialFactory credentials,
        IWebAntiforgeryPolicy antiforgery,
        PairingRedeemRateLimiter rateLimiter)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(antiforgery);
        ArgumentNullException.ThrowIfNull(rateLimiter);

        app.MapPost(
            RecoverPath,
            (RecoverRequest request, HttpContext context) =>
                RecoverAsync(authority, credentials, antiforgery, rateLimiter, request, context));
    }

    internal static async Task<IResult> RecoverAsync(
        IAccountRecoveryAuthority authority,
        IWebChosenCredentialFactory credentials,
        IWebAntiforgeryPolicy antiforgery,
        PairingRedeemRateLimiter rateLimiter,
        RecoverRequest? request,
        HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(antiforgery);
        ArgumentNullException.ThrowIfNull(rateLimiter);
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Headers.CacheControl = "no-store";

        if (!await antiforgery.ConsumeAnonymousAsync(context).ConfigureAwait(false))
        {
            return Results.Json(
                new ErrorResponse("antiforgery_failed", "Antiforgery validation failed."),
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Bound the memory-hard derivation this route now performs, exactly as account-setup
        // acceptance does. Minting server-side is what makes an anonymous request cost an Argon2id
        // hash, so the throttle arrives with the mint rather than after it. The first scope is an
        // opaque fingerprint of the recovery code rather than the source address: two people
        // recovering behind one NAT do not consume each other's per-code allowance. The second scope
        // is fixed to this route, so cycling guessed codes cannot evade the node-wide load bound.
        if (!rateLimiter.TryAcquireScopes(RecoveryScope(request?.Code), $"route:{RecoverPath}"))
        {
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        // Mint the credential BEFORE the authority runs. The saga consumes the recovery code
        // single-use and irreversibly (STEP 1), so a credential refused after that point would spend
        // the human's one code on an input error they could otherwise simply correct — and this is
        // the path they reach precisely because they are already locked out.
        var credential = credentials.Create(request?.Password);
        if (credential is null)
        {
            _ = await antiforgery.IssueAnonymousAsync(context).ConfigureAwait(false);
            return Results.Json(
                new ErrorResponse("credential_rejected", "The chosen password cannot be used."),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await authority.RecoverAsync(
                new AccountRecoveryCommand(
                    request?.Code ?? string.Empty,
                    credential.CredentialHash,
                    credential.CredentialCeremonyId),
                context.RequestAborted)
            .ConfigureAwait(false);

        switch (result.Status)
        {
            case AccountRecoveryStatus.Recovered:
                antiforgery.ExpireAnonymousBinding(context.Response);
                // No session is minted: the human re-authenticates through the standard login flow.
                return Results.Ok(new RecoverResponse("recovered"));
            case AccountRecoveryStatus.ChangedReplay:
                return Results.Json(
                    new ErrorResponse(
                        "changed_replay",
                        "The recovery code was already used with different account material."),
                    statusCode: StatusCodes.Status409Conflict);
            // InvitationRefused and AccountUnavailable are deliberately indistinguishable: the response
            // never reveals whether the code or the target account was the reason (non-enumerating).
            default:
                _ = await antiforgery.IssueAnonymousAsync(context).ConfigureAwait(false);
                return Refused();
        }
    }

    private static IResult Refused() =>
        Results.Json(
            new ErrorResponse("recovery_failed", "Account recovery failed."),
            statusCode: StatusCodes.Status401Unauthorized);

    private static string RecoveryScope(string? code)
    {
        var normalized = string.IsNullOrWhiteSpace(code) ? "<missing>" : code.Trim();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"recovery:{Convert.ToHexString(digest)}";
    }
}
