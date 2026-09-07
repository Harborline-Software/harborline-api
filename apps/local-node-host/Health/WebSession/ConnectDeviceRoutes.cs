using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>
/// MTW-2 #3167 — the authenticated "CONNECT YOUR DEVICE" mint route: the front door by which a web-admitted member,
/// present and authenticated in their own web session, mints a single-use device-pairing token for their FIRST
/// device (admiral-ruling-2026-07-24T1150Z R1; ADR 0113/0117 multi-device pairing). The device then presents that
/// token at wire enrollment, where the token-gated pairing admitter re-verifies the web-plane pins and records the
/// signed atlas admission (the redeem side).
/// </summary>
/// <remarks>
/// <para>
/// <b>Minted under the member's OWN session authority (R3-D), never pre-signed / node-standing.</b> Like
/// <see cref="FounderBindRoutes"/>, this route is admitted by the listener's selected-session cookie gate, which
/// revalidates every owning account / membership / Party / grant / epoch fact and publishes one immutable
/// <see cref="SelectedSessionRequestPrincipal"/> before the handler runs. The FOUR web-plane pins the mint binds
/// come straight off that principal — the browser supplies none of them, and no caller-passed membership snapshot
/// is trusted. Only the opaque token id, its bound Party, and the public team anchor cross back to the device.
/// </para>
/// <para>
/// <b>NOT in the pre-auth allowlist.</b> The route is reachable only on the authenticated selected-session path; a
/// missing cookie / principal / antiforgery is refused identically (no existence oracle), exactly as the founder-bind
/// route. A refused mint (e.g. an unexpected grant cardinality — the single-grant bound of ADR 0160 R3) collapses to
/// the same opaque refusal.
/// </para>
/// </remarks>
internal static class ConnectDeviceRoutes
{
    internal const string ConnectPath = "/api/session/connect-device";

    /// <summary>The short default pairing-token lifetime (minutes) when the caller supplies none. R5's accepted
    /// timing residual leans on a SHORT-TTL single-shot token, so the default is deliberately small.</summary>
    internal const double DefaultTtlMinutes = 15;

    /// <summary>The MAXIMUM pairing-token lifetime (minutes) a caller may request. A caller-supplied TTL is CLAMPED
    /// to this ceiling — never trusted raw — so an authenticated session cannot mint a years-long single-use bearer
    /// token (adversarial M1). The bearer weakness is bounded by single-use + short-TTL; an unbounded TTL would
    /// erode the short-TTL half.</summary>
    internal const double MaxTtlMinutes = 60;

    /// <summary>connect-device body — an optional caller-supplied TTL (minutes). Null ⇒ the mint's short default.</summary>
    internal sealed record ConnectRequest(double? TtlMinutes);

    private sealed record ConnectResponse(
        string TokenId,
        string JoiningPartyId,
        string TeamId,
        string GenesisPartyId,
        string GenesisPublicKey,
        string ExpiresAt);

    private sealed record ErrorResponse(string Error, string Message);

    internal static void Map(
        IEndpointRouteBuilder app,
        WebAdmittedMemberPairingTokenMint mint,
        Harborline.Api.LocalNodeHost.Enrollment.NodeTeamRoster roster,
        IWebAntiforgeryPolicy antiforgery)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(mint);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(antiforgery);
        app.MapPost(
            ConnectPath,
            (ConnectRequest? request, HttpContext context) =>
                ConnectAsync(mint, roster, antiforgery, request, context));
    }

    internal static async Task<IResult> ConnectAsync(
        WebAdmittedMemberPairingTokenMint mint,
        Harborline.Api.LocalNodeHost.Enrollment.NodeTeamRoster roster,
        IWebAntiforgeryPolicy antiforgery,
        ConnectRequest? request,
        HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(mint);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(antiforgery);
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Headers.CacheControl = "no-store";

        var selectedHandle = context.Request.Cookies[WebSessionCookieNames.Selected];
        if (string.IsNullOrWhiteSpace(selectedHandle))
        {
            return Refused();
        }

        // Defense in depth: the listener gate publishes this feature before the handler runs, so a missing principal
        // means the route was reached off its intended path. Refuse identically (no existence oracle).
        var principal = context.Features.Get<SelectedSessionRequestPrincipal>();
        if (principal is null)
        {
            return Refused();
        }

        if (!await antiforgery.ConsumeSelectedAsync(context, selectedHandle).ConfigureAwait(false))
        {
            return Results.Json(
                new ErrorResponse("antiforgery_failed", "Antiforgery validation failed."),
                statusCode: StatusCodes.Status400BadRequest);
        }

        // earlier repository ticket #3311 — re-issue the spent token HERE, not on the success branch, so every exit
        // path below answers with a usable one. Rationale on IWebAntiforgeryPolicy.RotateSelectedAsync.
        _ = await antiforgery.RotateSelectedAsync(context, selectedHandle).ConfigureAwait(false);

        // M1 — clamp the caller-supplied TTL to [>0, MaxTtlMinutes]; absent / non-positive / non-finite → the short
        // default. Over-ceiling MINTS a ceiling-TTL token (does not refuse — the member still gets a usable token,
        // just capped). This also defensively neutralizes NaN/Infinity/overflow before TimeSpan.FromMinutes.
        PairingTokenMintOutcome outcome;
        try
        {
            outcome = mint.MintForSession(principal, roster.Current, ClampMintTtl(request?.TtlMinutes));
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not a mint fault. Both sibling containment sites in this subsystem
            // (PairingRedeemDispatch, PairingTokenGatedAdmitter) rethrow it deliberately; swallowing it
            // here would report an aborted request as a refused pairing.
            throw;
        }
        catch (Exception ex)
        {
            // M2 — the real HTTP route is the containment boundary. A malformed roster snapshot, durable token
            // issue, or pairing-binding database fault must remain indistinguishable from an ordinary refused mint.
            // In particular, never let a Bind() fault escape as a 500 after the token was issued locally.
            //
            // OPAQUE TO THE CALLER, NOT TO THE OPERATOR. This used to record nothing at all, so a durable-store
            // outage was completely invisible: every member saw "Device pairing could not be started" and the host
            // emitted no signal. Both sibling sites already record the cause before refusing. Only the exception
            // TYPE is logged -- no token id, no principal, no roster contents -- so the wire response is unchanged
            // and the log adds no correlation handle.
            context.RequestServices
                .GetService<ILoggerFactory>()
                ?.CreateLogger("Harborline.Api.LocalNodeHost.Health.WebSession.ConnectDeviceRoutes")
                .LogWarning("connect-device mint faulted ({Fault}); refusing opaquely.", ex.GetType().Name);
            return Refused();
        }
        if (!outcome.Minted || outcome.Token is null)
        {
            // A refused mint (e.g. unexpected grant cardinality) is opaque — never disclose the internal reason.
            return Refused();
        }

        var token = outcome.Token;
        return Results.Ok(new ConnectResponse(
            TokenId: token.TokenId,
            JoiningPartyId: principal.CanonicalParty.Value,
            TeamId: token.Anchor.TeamId,
            GenesisPartyId: token.Anchor.GenesisPartyId,
            GenesisPublicKey: token.Anchor.GenesisPublicKey,
            ExpiresAt: token.ExpiresAt.ToString("O")));
    }

    /// <summary>
    /// M1 — clamp a caller-supplied pairing-token TTL (minutes) to a sane, short bounded lifetime. Absent
    /// (<c>null</c>), non-positive, or non-finite (NaN / ±∞) → <see cref="DefaultTtlMinutes"/>; a finite positive
    /// value is capped at <see cref="MaxTtlMinutes"/>. Never returns an unbounded or non-positive TTL, so
    /// <c>AdmissionToken.Mint</c> (which only enforces &gt; 0) can never receive an attacker-chosen years-long span.
    /// </summary>
    internal static TimeSpan ClampMintTtl(double? requestedMinutes)
    {
        var minutes = requestedMinutes is { } m && double.IsFinite(m) && m > 0
            ? Math.Min(m, MaxTtlMinutes)
            : DefaultTtlMinutes;
        return TimeSpan.FromMinutes(minutes);
    }

    private static IResult Refused() =>
        Results.Json(
            new ErrorResponse("connect_device_failed", "Device pairing could not be started."),
            statusCode: StatusCodes.Status401Unauthorized);
}
