namespace Harborline.Api.Kernel.Sync.Handshake;

/// <summary>
/// The A-side hook the gossip daemon's accept loop invokes for a PRE-TRUST ENROLLMENT exchange on the network
/// sync listener (#1301 F-1 — the cross-machine enrollment transport the in-proc test masked). A connecting peer
/// may send an <c>ENROLL_REQUEST</c> frame as its FIRST frame, BEFORE the trusted HELLO; the daemon hands the
/// request's OPAQUE payload to this handler, which validates the INVITE the payload carries and (on success)
/// returns the OPAQUE bootstrap response the joiner adopts. The trusted HELLO then proceeds on a SUBSEQUENT
/// connection.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an interface here, and why opaque bytes.</b> The enrollment PROTOCOL (foundation-identity-atlas
/// <c>WireEnrollment</c> — invite-gating, MITM-resist via the out-of-band anchor, roster re-validation) lives in
/// a package that sits ABOVE kernel-sync; kernel-sync must NOT depend on it (the council praised exactly this
/// clean split). So the daemon only knows "first frame might be an enrollment request → hand the bytes to a
/// handler the host wired, write back the handler's bytes." The concrete handler lives in the host
/// (local-node-host) and bridges the bytes to <c>WireEnrollment</c>. This mirrors how the daemon already
/// resolves the OPTIONAL <c>IPeerTrustPolicy</c> / <c>ITrustedMemberKeyProvider</c> from the outer provider
/// without depending on the host.
/// </para>
/// <para>
/// <b>AUTH is the INVITE, not the session token and not roster membership.</b> The pre-trust enrollment is gated
/// ENTIRELY by the invite inside the payload (single-use, roster-admin-signed, TTL — enforced by the handler's
/// downstream <c>AdmissionCoordinator</c>). The joiner B is, by definition, NOT in the roster yet and has NO
/// trusted-session token — so neither of those can gate this phase. The trusted-session gate
/// (<see cref="HandshakeProtocol"/> + <see cref="IPeerTrustPolicy"/>) is UNCHANGED and still gates every HELLO.
/// </para>
/// <para>
/// <b>Fail-closed + no-leak.</b> A null handler (no host wired one) means the daemon treats an enrollment frame
/// as a protocol violation and closes the connection — the network listener exposes the enrollment phase ONLY
/// when a handler is present. The handler MUST fail closed on any invalid / missing / expired / replayed invite,
/// returning a bare rejection (<see cref="PreTrustEnrollmentResult.Reject"/>) that discloses no roster / genesis
/// state — an invalid-invite caller learns only that it was rejected. The handler must NOT throw for an expected
/// reject (a throw is caught by the daemon and also treated as a fail-closed reject).
/// </para>
/// </remarks>
public interface IPreTrustEnrollmentHandler
{
    /// <summary>
    /// Validate the invite the OPAQUE <paramref name="requestPayload"/> carries and, on success, admit the joiner
    /// and return the OPAQUE bootstrap response. Fail-closed on any invalid / missing / expired / replayed invite
    /// — return <see cref="PreTrustEnrollmentResult.Reject"/> with an opaque reason (NO roster/genesis leak), do
    /// not throw for an expected reject.
    /// </summary>
    /// <param name="requestPayload">The enrollment-protocol request bytes (the host knows their structure; the
    /// daemon does not). Never null but may be empty — an empty/garbage payload must reject fail-closed.</param>
    /// <param name="source">The transport-observed remote source (normally the peer IP). The host uses this
    /// server-derived value for enrollment throttling; it is never accepted from the request payload.</param>
    /// <param name="ct">Cancellation (bounded by the daemon's per-handshake deadline).</param>
    Task<PreTrustEnrollmentResult> HandleAsync(
        byte[] requestPayload,
        string source,
        CancellationToken ct);
}

/// <summary>
/// The outcome of a pre-trust enrollment exchange — accepted (with the OPAQUE bootstrap response payload the
/// daemon writes back) or rejected (fail-closed, with an opaque reason and NO payload, so the joiner adopts
/// nothing and an invalid-invite caller learns nothing about the roster).
/// </summary>
public sealed record PreTrustEnrollmentResult
{
    private PreTrustEnrollmentResult(bool accepted, byte[] responsePayload, string? rejectReason)
    {
        Accepted = accepted;
        ResponsePayload = responsePayload;
        RejectReason = rejectReason;
    }

    /// <summary>True iff the invite validated and the joiner was admitted.</summary>
    public bool Accepted { get; }

    /// <summary>The OPAQUE bootstrap response bytes to write back on success; empty on a reject.</summary>
    public byte[] ResponsePayload { get; }

    /// <summary>An opaque reject reason token on failure (never roster/genesis-disclosing); null on success.</summary>
    public string? RejectReason { get; }

    /// <summary>Accept — carry the OPAQUE bootstrap response bytes back to the joiner.</summary>
    public static PreTrustEnrollmentResult Accept(byte[] responsePayload) =>
        new(true, responsePayload ?? System.Array.Empty<byte>(), null);

    /// <summary>Reject fail-closed — an opaque reason, no payload, no roster/genesis leak.</summary>
    public static PreTrustEnrollmentResult Reject(string reason) =>
        new(false, System.Array.Empty<byte>(), reason);
}
