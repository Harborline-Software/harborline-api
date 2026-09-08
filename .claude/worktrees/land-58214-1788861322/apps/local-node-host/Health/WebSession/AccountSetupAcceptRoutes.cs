using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>
/// The pre-tenant account-setup invitation acceptance route (MTW-2 #2614). It is the ONE route that
/// reaches <see cref="AccountSetupAcceptanceService"/>. Anonymous / pre-caller-auth allowlisted: the
/// joiner has no account or session yet — the single-use invitation code IS the authority. The
/// acceptance authority runs both gates and mints the joiner account, Party binding, grant, and web
/// membership; on success the joiner traverses the standard account-challenge → select flow to
/// establish a tenant-bound session (this route mints no session — no bypass of the session authority).
/// </summary>
/// <remarks>
/// Non-enumerating refusal: an unknown/expired/consumed/revoked code and a lapsed inviter mandate
/// return the SAME generic 401, so the surface never reveals whether the code or the inviter's
/// authority was the reason. Only the joiner-chosen username/credential validation surfaces
/// distinguishable conflicts.
/// </remarks>
public static class AccountSetupAcceptRoutes
{
    /// <summary>Exact pre-auth route; the invitation code is the only authority it accepts.</summary>
    public const string AcceptPath = "/api/session/account-setup-accept";

    /// <summary>
    /// Acceptance request. Only <see cref="Code"/> proves authority; the username and password are
    /// the joiner's own account material.
    /// </summary>
    /// <remarks>
    /// ADR 0160 D3: "Redemption chooses a password." The joiner sends the password they chose, over
    /// the same same-origin channel <c>/api/session/account-challenge</c> already carries a password
    /// on, and the node mints the canonical Argon2id artifact from it. That artifact was never a
    /// value a browser could supply: it carries the installation's own cost parameters, and its
    /// accepted encoding is PADDED Base64, which the standard PHC encoding does not emit. A future
    /// configured pepper would make it impossible outright — which is why this contract must not
    /// depend on the pepper being absent. See <see cref="IWebChosenCredentialFactory"/> for the full
    /// reasoning (earlier repository ticket #3338).
    /// </remarks>
    public sealed record AcceptRequest(
        string? Code,
        string? TenantId,
        string? Username,
        string? Password)
    {
        /// <summary>
        /// Keeps the two secret-bearing members out of <see cref="object.ToString"/>. A positional
        /// record prints every property, so one structured-logging call or exception message
        /// carrying <c>{Request}</c> would put the joiner's chosen password AND their single-use
        /// invitation code in the node log — the code is bearer authority until it is consumed, and
        /// the password is a live credential. Tenant and username are non-secret: one is a workspace
        /// identifier the inviter hands out, the other is the name the joiner is about to be known by.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The installation's own secret-bearing bootstrap command (under <c>Data/Identity</c>) does
        /// the same thing for the same reason; it is described rather than named here because a
        /// listener-route file that names that authority's symbols trips the dormancy arch fence in
        /// <c>InstallationIdentityDormancyArchTests</c> — correctly, since "no route reaches the
        /// founder bootstrap authority" is precisely the claim that fence exists to hold.
        /// </para>
        /// <para>
        /// Declared <c>private</c>, not <c>protected override</c>: for a sealed record whose base
        /// type is <see cref="object"/> the compiler synthesizes <c>PrintMembers</c> as private, and
        /// a user declaration must match that signature or the type does not compile — CS8879
        /// ("Record member … must be private"), plus CS0115 for the absent base member.
        /// </para>
        /// </remarks>
        private bool PrintMembers(StringBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Append("Code = <redacted>, TenantId = ");
            builder.Append(TenantId);
            builder.Append(", Username = ");
            builder.Append(Username);
            builder.Append(", Password = <redacted>");
            return true;
        }
    }

    private sealed record AcceptResponse(string TenantId);

    private sealed record ErrorResponse(string Error, string Message);

    /// <summary>Maps the acceptance route closed over the outer-container authority (bug-2849).</summary>
    public static void Map(
        IEndpointRouteBuilder app,
        IAccountSetupAcceptanceAuthority authority,
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
            AcceptPath,
            (AcceptRequest request, HttpContext context) =>
                AcceptAsync(authority, credentials, antiforgery, rateLimiter, request, context));
    }

    internal static async Task<IResult> AcceptAsync(
        IAccountSetupAcceptanceAuthority authority,
        IWebChosenCredentialFactory credentials,
        IWebAntiforgeryPolicy antiforgery,
        PairingRedeemRateLimiter rateLimiter,
        AcceptRequest? request,
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

        // Reuse the pairing-redeem limiter before the memory-hard credential derivation. The first
        // scope is an opaque fingerprint of the invitation rather than the source address: colleagues
        // redeeming distinct invitations behind one NAT do not consume each other's per-invitation
        // allowance. The second scope is fixed to this route rather than the browser-supplied tenant id,
        // so cycling guessed codes or tenant ids cannot evade the node-wide load bound.
        if (!rateLimiter.TryAcquireScopes(
                InvitationScope(request?.Code),
                $"route:{AcceptPath}"))
        {
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        // Mint the credential BEFORE the authority runs. GATE 1 inside the acceptance saga consumes
        // the invitation single-use and irreversibly, so a credential refused after that point would
        // spend the human's one code on an input error they could otherwise simply correct.
        var credential = credentials.Create(request?.Password);
        if (credential is null)
        {
            _ = await antiforgery.IssueAnonymousAsync(context).ConfigureAwait(false);
            return Results.Json(
                new ErrorResponse("credential_rejected", "The chosen password cannot be used."),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await authority.AcceptAsync(
                new AccountSetupAcceptCommand(
                    request?.Code ?? string.Empty,
                    request?.TenantId ?? string.Empty,
                    request?.Username ?? string.Empty,
                    credential.CredentialHash,
                    credential.CredentialCeremonyId),
                context.RequestAborted)
            .ConfigureAwait(false);

        switch (result.Status)
        {
            case AccountSetupAcceptStatus.Accepted:
                antiforgery.ExpireAnonymousBinding(context.Response);
                return Results.Ok(new AcceptResponse(Guid.Parse(request!.TenantId!).ToString("D")));
            case AccountSetupAcceptStatus.UsernameConflict:
                _ = await antiforgery.IssueAnonymousAsync(context).ConfigureAwait(false);
                return Results.Json(
                    new ErrorResponse("username_taken", "The chosen username is not available."),
                    statusCode: StatusCodes.Status409Conflict);
            case AccountSetupAcceptStatus.ChangedReplay:
                return Results.Json(
                    new ErrorResponse(
                        "changed_replay",
                        "The invitation was already accepted with different account material."),
                    statusCode: StatusCodes.Status409Conflict);
            case AccountSetupAcceptStatus.MembershipUnavailable:
                return Results.Json(
                    new ErrorResponse(
                        "membership_pending",
                        "Your account was created, but membership setup did not complete. " +
                        "Ask an administrator for help."),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            // InvitationRefused and AuthorityRefused are deliberately indistinguishable: the response
            // never reveals whether the code or the inviter's mandate was the reason (non-enumerating).
            default:
                _ = await antiforgery.IssueAnonymousAsync(context).ConfigureAwait(false);
                return Refused();
        }
    }

    private static IResult Refused() =>
        Results.Json(
            new ErrorResponse("acceptance_failed", "Invitation acceptance failed."),
            statusCode: StatusCodes.Status401Unauthorized);

    private static string InvitationScope(string? code)
    {
        var normalized = string.IsNullOrWhiteSpace(code) ? "<missing>" : code.Trim();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"invitation:{Convert.ToHexString(digest)}";
    }
}
