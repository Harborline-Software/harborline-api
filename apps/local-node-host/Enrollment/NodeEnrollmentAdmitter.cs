using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Kernel.Sync.Handshake;
using Harborline.Api.LocalNodeHost.Diagnostics;

namespace Harborline.Api.LocalNodeHost.Enrollment;

/// <summary>
/// The A-side <see cref="IPreTrustEnrollmentHandler"/> the gossip daemon's NETWORK sync listener invokes for a
/// PRE-TRUST enrollment exchange (#1301 F-1 — the cross-machine transport the in-proc test masked). It is the
/// bridge between kernel-sync's OPAQUE bytes and the reviewed-sound enrollment protocol: it decodes the request
/// payload, runs the shared <see cref="WireEnrollmentAdmitter"/> admit-and-respond core (invite-gated), and
/// encodes the bootstrap response — all fail-closed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where it sits.</b> kernel-sync owns the network listener + the pre-trust phase but knows nothing of the
/// enrollment protocol; this host type owns the protocol bridge. Registered as <c>IPreTrustEnrollmentHandler</c>
/// in the host's OUTER provider; the per-team registrar resolves it from there and wires it into the per-team
/// daemon (the same dependency-direction trick as <c>ITrustedMemberKeyProvider</c>). So the enrollment surface
/// appears on the 7473 listener ONLY for a host that wired this — a sync-only deployment exposes none.
/// </para>
/// <para>
/// <b>AUTH is the invite, kept narrow + no-leak.</b> The admit core is gated entirely by the single-use,
/// roster-admin-signed, TTL invite in the decoded request — NOT the F1 loopback session token (that gates the
/// loopback transport, untouched here) and NOT roster membership (the joiner is JOINING). On ANY failure — a
/// malformed payload, an invalid / missing / expired / replayed invite, a forged signature, a not-ready team —
/// it returns a bare <see cref="PreTrustEnrollmentResult.Reject"/> with a coarse opaque reason and NO payload, so
/// an invalid-invite caller learns nothing about the roster / genesis. It does only the enrollment exchange and
/// nothing else: no /admission/* surface, no other route is reachable on the network listener.
/// </para>
/// </remarks>
public sealed class NodeEnrollmentAdmitter : IPreTrustEnrollmentHandler
{
    private readonly WireEnrollmentAdmitter _admitter;
    private readonly IEnrollmentRedeemDispatch _pairing;
    private readonly ILogger<NodeEnrollmentAdmitter>? _logger;
    private readonly CommsDiagnostics _diag;

    internal NodeEnrollmentAdmitter(
        WireEnrollmentAdmitter admitter,
        IEnrollmentRedeemDispatch pairing,
        ILogger<NodeEnrollmentAdmitter>? logger = null,
        CommsDiagnostics? diag = null)
    {
        _admitter = admitter ?? throw new ArgumentNullException(nameof(admitter));
        _pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
        _logger = logger;
        _diag = diag ?? CommsDiagnostics.Disabled;
    }

    /// <inheritdoc />
    public async Task<PreTrustEnrollmentResult> HandleAsync(
        byte[] requestPayload,
        string source,
        CancellationToken ct)
    {
        // Decode the opaque request bytes → the protocol request. A malformed / truncated payload is a
        // fail-closed reject (a stranger probing the listener learns only that it was rejected).
        EnrollmentRequest request;
        try
        {
            request = EnrollmentWireCodec.DecodeRequest(requestPayload ?? Array.Empty<byte>());
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException)
        {
            _logger?.LogWarning("Pre-trust enrollment: malformed request payload — rejecting fail-closed.");
            return PreTrustEnrollmentResult.Reject("malformed_request");
        }

        // R5 + R2 — the SAME dispatch the loopback HTTP path uses. It rate-limits the server-observed source and
        // active tenant before any token lookup, routes a bound token through the receipt-gated pairing admitter,
        // and refuses the plain path when a live web membership grant enables web-plane admission.
        var pairing = await _pairing.DispatchAsync(
            request,
            string.IsNullOrWhiteSpace(source) ? "unknown" : source,
            ct).ConfigureAwait(false);
        switch (pairing.Kind)
        {
            case PairingDispatchKind.RateLimited:
                RecordRefusal(pairing, request.JoiningPartyId);
                return PreTrustEnrollmentResult.Reject("rate_limited");
            case PairingDispatchKind.Refused:
                RecordRefusal(pairing, request.JoiningPartyId);
                return PreTrustEnrollmentResult.Reject("enrollment_refused");
            case PairingDispatchKind.Accepted:
                return PreTrustEnrollmentResult.Accept(EnrollmentWireCodec.EncodeResponse(pairing.Response!));
            case PairingDispatchKind.PlainPath:
                break;
            default:
                throw new InvalidOperationException($"Unknown pairing dispatch outcome '{pairing.Kind}'.");
        }

        // Plain mode only: run the shared admit core (verify → invite-redeem + admit → wire trust → publish → SoD
        // audit → build bootstrap). The shared pairing dispatch above has already proven that web-plane admission
        // is disabled for this tenant; the 7473 channel therefore cannot bypass R2.
        var outcome = await _admitter.AdmitAsync(request, ct).ConfigureAwait(false);
        if (!outcome.Accepted || outcome.Response is null)
        {
            return PreTrustEnrollmentResult.Reject(outcome.RejectReason ?? "enrollment_rejected");
        }

        // Accept — encode the bootstrap response to the opaque payload the daemon writes back to the joiner.
        var responsePayload = EnrollmentWireCodec.EncodeResponse(outcome.Response);
        return PreTrustEnrollmentResult.Accept(responsePayload);
    }

    /// <summary>
    /// #258 — record WHY the shared dispatch refused, carrying the SAME decision object's
    /// <see cref="PairingDispatchOutcome.InternalReason"/> (never re-decided here). The loopback route already does
    /// this (<c>AdmissionRoutes.TryPairingDispatchAsync</c> → <c>RouteRejectMapping</c>); the network handler used to
    /// drop it, so a cross-machine refusal named no cause on ANY surface — log, diagnostic or wire — and the only
    /// way to tell <c>plain_path_disabled</c> from <c>pairing_dispatch_faulted:*</c> was to guess.
    /// The WIRE stays opaque (the F4-a byte-identical no-leak posture is deliberate and unchanged): the reason goes
    /// to the operator-side log and the comms diagnostic ONLY. The reasons are coarse and non-secret by
    /// construction (<see cref="PairingDispatchOutcome.Refuse"/> callers pass a fixed token or an exception TYPE
    /// name); no key, token id, signature or payload is formatted.
    /// </summary>
    private void RecordRefusal(PairingDispatchOutcome pairing, string? joiningPartyId)
    {
        var reason = pairing.InternalReason ?? "unspecified";
        _logger?.LogWarning(
            "Pre-trust enrollment: the shared pairing dispatch REFUSED the invite — kind={Kind} reason={Reason} "
            + "joiningParty={Party}. The wire reply stays opaque; this line is the operator-side record.",
            pairing.Kind,
            reason,
            joiningPartyId ?? "<none>");
        _diag.AdmitOutcome(accepted: false, reason, joiningPartyId);
    }
}
