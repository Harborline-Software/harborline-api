using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Kernel.Sync.Protocol;

namespace Harborline.Api.LocalNodeHost.Enrollment;

/// <summary>
/// The B-side (joiner) wire transport for two-sided wire enrollment over a REAL SOCKET (#1301 F-1) — the
/// cross-machine path that replaces the loopback-only HTTP <c>/admission/redeem</c> POST. It dials the admitter's
/// NETWORK sync listener (the 7473 TCP endpoint, reachable over LAN / Tailscale — the SAME endpoint the trusted
/// HELLO uses), sends the enrollment request as the FIRST frame (an <c>ENROLL_REQUEST</c>), reads the
/// <c>ENROLL_RESPONSE</c>, and returns A's bootstrap payload (or null fail-closed).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a socket, not loopback HTTP (the F-1 fix).</b> The HTTP transport POSTs to A's
/// <c>/admission/redeem</c>, which is bound <c>127.0.0.1</c> (loopback) + gated by the F1 caller-auth token — so a
/// REMOTE B can't reach it. This transport instead opens a TCP connection to A's <c>0.0.0.0:7473</c> sync
/// listener (which A binds for cross-machine sync anyway) and rides the pre-trust enrollment phase on it. AUTH is
/// the INVITE the request carries (the joiner has no session token + is not yet in the roster), so this is the
/// channel that actually works between two machines.
/// </para>
/// <para>
/// <b>Pre-trust by construction.</b> This is the ONE call B makes before it is trusted: a fresh
/// <see cref="TcpSyncDaemonTransport"/> dial, a single ENROLL_REQUEST frame, a single ENROLL_RESPONSE read, then
/// close. No HELLO, no trust state — the response is validated by the protocol client
/// (<see cref="NodeWireEnrollmentClient"/>) against the out-of-band anchor, and only THEN does B adopt + connect
/// with the trusted HELLO.
/// </para>
/// <para>
/// <b>Fail-closed.</b> Any dial failure / non-accept response / malformed payload returns null → the client
/// reports <c>no_response</c> and adopts nothing (no partial trust). A read deadline bounds a hung admitter.
/// </para>
/// </remarks>
public sealed class SocketEnrollmentTransport : IEnrollmentTransport
{
    private readonly Func<string> _admitterEndpoint;
    private readonly TimeSpan _timeout;
    private readonly ILogger<SocketEnrollmentTransport>? _logger;

    /// <summary>
    /// Construct the socket enrollment transport.
    /// </summary>
    /// <param name="admitterEndpoint">A factory for the admitter's reachable sync endpoint
    /// (<c>tcp://&lt;host&gt;:7473</c> or bare <c>host:7473</c>; the operator-supplied LAN / Tailscale address).
    /// Resolved per call so a changed peer address is picked up.</param>
    /// <param name="timeout">Per-exchange deadline (dial + send + receive). Defaults to 15s.</param>
    /// <param name="logger">Optional logger.</param>
    public SocketEnrollmentTransport(
        Func<string> admitterEndpoint,
        TimeSpan? timeout = null,
        ILogger<SocketEnrollmentTransport>? logger = null)
    {
        _admitterEndpoint = admitterEndpoint ?? throw new ArgumentNullException(nameof(admitterEndpoint));
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<EnrollmentResponse?> SendAsync(EnrollmentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        string endpoint;
        try
        {
            endpoint = _admitterEndpoint();
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                _logger?.LogWarning("Socket enrollment: admitter endpoint is not configured — aborting fail-closed.");
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Socket enrollment: could not resolve the admitter endpoint — aborting fail-closed.");
            return null;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        var token = deadline.Token;

        // Outbound-only transport — we only dial; we never listen here.
        await using var transport = new TcpSyncDaemonTransport();
        ISyncDaemonConnection? conn = null;
        try
        {
            conn = await transport.ConnectAsync(endpoint, token).ConfigureAwait(false);

            // Send the enrollment request as the FIRST frame (the pre-trust phase). The opaque payload is the
            // protocol request the host serialized — kernel-sync ships it without inspecting it.
            var payload = EnrollmentWireCodec.EncodeRequest(request);
            await conn.SendAsync(new EnrollRequestMessage(payload), token).ConfigureAwait(false);

            // Read A's response. Anything other than an accepted ENROLL_RESPONSE → fail-closed null.
            var inbound = await conn.ReceiveAsync(token).ConfigureAwait(false);
            if (inbound is not EnrollResponseMessage enrollResponse)
            {
                _logger?.LogWarning(
                    "Socket enrollment: expected ENROLL_RESPONSE from {Endpoint}, got {Frame} — aborting fail-closed.",
                    endpoint, inbound.GetType().Name);
                return null;
            }
            if (!enrollResponse.Accepted)
            {
                _logger?.LogWarning(
                    "Socket enrollment: admitter {Endpoint} rejected the invite ({Reason}) — aborting fail-closed.",
                    endpoint, enrollResponse.RejectReason ?? "unspecified");
                return null;
            }

            return EnrollmentWireCodec.DecodeResponse(enrollResponse.Payload ?? Array.Empty<byte>());
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Socket enrollment: transport/decode failure reaching {Endpoint} — aborting fail-closed.", endpoint);
            return null;
        }
        finally
        {
            if (conn is not null)
            {
                try { await conn.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort close */ }
            }
        }
    }
}
