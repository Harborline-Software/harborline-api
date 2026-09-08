using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.IdentityAtlas.Enrollment;

namespace Harborline.Api.LocalNodeHost.Enrollment;

/// <summary>
/// The LOOPBACK / SAME-NODE wire transport for the joiner half of two-sided wire enrollment — POSTs the signed
/// <see cref="EnrollmentRequest"/> to an admitter's <c>POST {admitterBaseUrl}/api/local-node/admission/redeem</c>
/// route and deserializes the <see cref="EnrollmentResponse"/> bootstrap payload.
/// </summary>
/// <remarks>
/// <para>
/// <b>NOT the cross-machine path (#1301 F-1).</b> The redeem route is bound <c>127.0.0.1</c>
/// (<c>SharedHostedWebApp.ConfigureUrls</c>) and gated by the inc-4 listener-level caller-auth — so a REMOTE
/// joiner on another machine CANNOT reach it (the council F-1 finding: the in-proc test masked this). The
/// cross-machine joiner uses <see cref="SocketEnrollmentTransport"/> instead, which dials the admitter's 7473
/// network SYNC listener and rides the invite-gated PRE-TRUST enrollment phase there. This HTTP transport remains
/// only for SAME-NODE / local-app use where the loopback redeem route IS reachable.
/// </para>
/// <para>
/// <b>Where this fits.</b> <see cref="NodeWireEnrollmentClient"/> owns the protocol (derive transport key, sign,
/// validate, adopt); this owns the bytes-on-the-wire for the loopback path. The shared
/// <see cref="WireEnrollmentAdmitter"/> is the A-side admit core both the loopback route and the network channel
/// call.
/// </para>
/// <para>
/// <b>Caller-auth.</b> When the loopback redeem route requires the inc-4 session token (the app-hosted
/// posture), the optional <see cref="AdmitterToken"/> is sent as a Bearer. The SECURITY boundary that matters is
/// the SIGNED roster + the out-of-band anchor B validates — not this transport-level token (which only gates a
/// loopback caller, per inc-4). The cross-machine pre-trust enrollment is gated by the INVITE, not this token.
/// </para>
/// <para>
/// <b>Fail-closed.</b> Any non-success status / transport fault / malformed body returns null, so the client
/// reports <c>no_response</c> and adopts nothing (no partial trust).
/// </para>
/// </remarks>
public sealed class HttpEnrollmentTransport : IEnrollmentTransport
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Func<Uri> _admitterBaseUrl;
    private readonly ILogger<HttpEnrollmentTransport>? _logger;

    /// <summary>The optional admitter session token (Bearer) for a hardened deployment that gates the redeem route.
    /// Null/empty when the admitter's LAN listener does not require it (the single-office Tailscale posture).</summary>
    public string? AdmitterToken { get; init; }

    /// <summary>
    /// Construct the HTTP enrollment transport.
    /// </summary>
    /// <param name="httpClientFactory">The DI HTTP client factory (so this adapter is DI-resolvable per the
    /// vendor-adapter convention).</param>
    /// <param name="admitterBaseUrl">A factory for the admitter's base URL (e.g. http://&lt;tailscale-ip&gt;:&lt;port&gt;).
    /// Resolved per call so a changed peer address is picked up.</param>
    /// <param name="logger">Optional logger.</param>
    public HttpEnrollmentTransport(
        IHttpClientFactory httpClientFactory,
        Func<Uri> admitterBaseUrl,
        ILogger<HttpEnrollmentTransport>? logger = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _admitterBaseUrl = admitterBaseUrl ?? throw new ArgumentNullException(nameof(admitterBaseUrl));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<EnrollmentResponse?> SendAsync(EnrollmentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        Uri baseUrl;
        try
        {
            baseUrl = _admitterBaseUrl();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Enrollment: could not resolve the admitter base URL — aborting fail-closed.");
            return null;
        }

        var redeemUri = new Uri(baseUrl, "api/local-node/admission/redeem");
        // The wire body mirrors the route's RedeemInviteBody (signed enrollment request fields).
        var body = new RedeemRequestBody(
            TokenId: request.TokenId,
            JoiningPartyId: request.JoiningPartyId,
            JoiningPublicKey: request.JoiningPrincipalPublicKey,
            JoiningTransportKey: request.JoiningTransportPublicKey,
            JoiningDmKey: request.JoiningDmPublicKey,
            JoiningXWingKey: request.JoiningXWingPublicKey,
            Signature: request.Signature,
            IssuedAtUnixMs: request.IssuedAt.ToUnixTimeMilliseconds(),
            Nonce: request.Nonce.ToString());

        try
        {
            using var client = _httpClientFactory.CreateClient(nameof(HttpEnrollmentTransport));
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, redeemUri)
            {
                Content = JsonContent.Create(body),
            };
            if (!string.IsNullOrWhiteSpace(AdmitterToken))
            {
                httpRequest.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AdmitterToken.Trim());
            }

            using var resp = await client.SendAsync(httpRequest, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger?.LogWarning(
                    "Enrollment redeem returned {Status} from {Uri} — aborting fail-closed.",
                    (int)resp.StatusCode, redeemUri);
                return null;
            }

            var response = await resp.Content
                .ReadFromJsonAsync<EnrollmentResponse>(ct)
                .ConfigureAwait(false);
            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _logger?.LogWarning(ex, "Enrollment: transport/deserialize failure reaching {Uri} — aborting fail-closed.", redeemUri);
            return null;
        }
    }

    /// <summary>The wire body for the redeem POST — mirrors <c>AdmissionRoutes.RedeemInviteBody</c> (camelCase via
    /// the default System.Text.Json policy).</summary>
    private sealed record RedeemRequestBody(
        string TokenId,
        string JoiningPartyId,
        string JoiningPublicKey,
        string JoiningTransportKey,
        string JoiningDmKey,
        string JoiningXWingKey,
        string Signature,
        long IssuedAtUnixMs,
        string Nonce);
}
