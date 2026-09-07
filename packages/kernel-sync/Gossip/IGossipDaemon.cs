using Harborline.Api.Kernel.Sync.Protocol;

namespace Harborline.Api.Kernel.Sync.Gossip;

/// <summary>
/// Paper §6.1 gossip-based anti-entropy daemon. Every
/// <see cref="GossipDaemonOptions.RoundIntervalSeconds"/> seconds, picks
/// <see cref="GossipDaemonOptions.PeerPickCount"/> random peers from
/// <see cref="KnownPeers"/> and exchanges HELLO / CAPABILITY_NEG / ACK /
/// DELTA_STREAM / GOSSIP_PING with each.
/// </summary>
public interface IGossipDaemon : IAsyncDisposable
{
    /// <summary>
    /// Start the round scheduler. Subsequent calls while running are
    /// idempotent (no-op). Throws if the daemon has already been disposed.
    /// </summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>
    /// Signal the scheduler to stop. Waits for the in-flight round (if any)
    /// to finish, then returns. Idempotent.
    /// </summary>
    Task StopAsync(CancellationToken ct);

    /// <summary>
    /// Start the inbound responder/accept loop (multi-device INC-5). Accepts
    /// inbound peer connections, runs the signed-HELLO handshake with the LIVE
    /// trust gate on each, and mirrors the gossip round. Bounded by the
    /// accept-loop DoS caps. Idempotent. A transport with no listener simply
    /// has no responder side (the loop ends cleanly). Separate from
    /// <see cref="StartAsync"/> so a deployment can run outbound-only,
    /// inbound-only, or both.
    /// </summary>
    Task StartListeningAsync(CancellationToken ct);

    /// <summary>Stop the inbound responder/accept loop. Idempotent.</summary>
    Task StopListeningAsync(CancellationToken ct);

    /// <summary>Whether the inbound responder/accept loop is running.</summary>
    bool IsListening { get; }

    /// <summary>
    /// Run ONE anti-entropy round IMMEDIATELY against the currently-eligible
    /// peers, instead of waiting for the next periodic
    /// <see cref="GossipDaemonOptions.RoundIntervalSeconds"/> tick. This is the
    /// "push-on-change" path: when a local mutation produces a delta, the
    /// application calls this so the new op reaches connected, authenticated
    /// peers in sub-second time rather than up to a full round later. The
    /// periodic round becomes the catch-up / backstop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Same path, no new surface.</b> This reuses the EXACT round the periodic
    /// timer drives — the signed-HELLO handshake, the LIVE trust gate, the
    /// DELTA_STREAM rate limit, and (on the responder side) the accept-loop DoS
    /// caps. It opens NO new connection, adds NO unauthenticated push, and bypasses
    /// NO trust check: an untrusted peer is rejected here exactly as in a scheduled
    /// round. It is purely a "run the round now" trigger.
    /// </para>
    /// <para>
    /// <b>Bulkheaded + coalescing.</b> Each explicitly selected lane has its own
    /// concurrency budget. A burst within one lane coalesces — a push already in
    /// flight absorbs edits that land while that lane is saturated — without
    /// consuming the other lane's permits. It is a no-op when no peers are eligible. Safe to call before
    /// <see cref="StartAsync"/> (it runs a round directly; the periodic loop need
    /// not be running). Never throws for a peer-level failure — per-peer backoff is
    /// applied exactly as in a scheduled round.
    /// </para>
    /// </remarks>
    /// <param name="lane">The independently budgeted outbound work class.</param>
    /// <param name="ct">Cancellation token.</param>
    Task TriggerPushAsync(OutboundSyncLane lane, CancellationToken ct);

    /// <summary>Add a known peer for gossip rounds to consider.</summary>
    void AddPeer(string peerEndpoint, byte[] peerPublicKey);

    /// <summary>Remove a peer from the membership list.</summary>
    void RemovePeer(string peerEndpoint);

    /// <summary>Current membership snapshot. Enumeration is safe across rounds.</summary>
    IReadOnlyCollection<PeerInfo> KnownPeers { get; }

    /// <summary>
    /// Whether the daemon's round loop is currently running. Transitions from
    /// <c>false</c> to <c>true</c> on <see cref="StartAsync"/> and back to
    /// <c>false</c> on <see cref="StopAsync"/> / <see cref="IAsyncDisposable.DisposeAsync"/>.
    /// Surfaced for health-check consumers (Wave 5.2.D
    /// <c>LocalNodeHealthCheck</c>) that need to distinguish "active team exists
    /// but gossip is not yet spinning" (Degraded) from "active team + gossip
    /// running" (Healthy).
    /// </summary>
    bool IsRunning { get; }

    /// <summary>Fires on every successful gossip round; useful for tests and observability.</summary>
    event EventHandler<GossipRoundCompletedEventArgs>? RoundCompleted;

    /// <summary>
    /// Fires once for every inbound sync-daemon frame the round loop
    /// successfully observed from a peer — completed HELLO handshakes,
    /// received GOSSIP_PINGs, and round-level errors (handshake failure,
    /// generic gossip error).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The event is coarser than the wire protocol: it does not fire for
    /// purely outbound frames (the daemon's own PING send) nor for inbound
    /// frames that the round loop currently ignores (ACK, DELTA_STREAM —
    /// those are still Wave 2.6 / 6.x territory). The intent is to give
    /// notification producers (Wave 6.5
    /// <c>GossipEventTeamNotificationStream</c>) a stable signal of
    /// "something observable happened with peer X" without coupling them to
    /// the daemon's internal frame-dispatch state machine.
    /// </para>
    /// <para>
    /// Handlers run synchronously on the round loop's task. They must not
    /// block — if a consumer needs to fan out heavy work, it should
    /// offload to its own queue / channel (see
    /// <c>GossipEventTeamNotificationStream</c> for the canonical pattern).
    /// </para>
    /// </remarks>
    event EventHandler<GossipFrameEventArgs>? FrameReceived;
}

/// <summary>
/// Explicit work classes for independently budgeted outbound sync bulkheads.
/// </summary>
public enum OutboundSyncLane
{
    /// <summary>Changes a user or foreground workflow is waiting to observe.</summary>
    Foreground = 0,

    /// <summary>Scheduled reconciliation and speculative catch-up work.</summary>
    Background = 1,
}

/// <summary>
/// Membership record kept by the gossip daemon. Public surface intentionally
/// small — richer attributes (role, attestation snapshots, ...) live in
/// sibling packages and flow through the handshake, not through membership.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LastSeenNonce"/> tracks the highest <c>monotonic_nonce</c> seen
/// in a GOSSIP_PING from this peer (sync-daemon-protocol §8 replay
/// protection). Initial value is <c>0</c> — any real peer PING carries a
/// strictly-positive nonce, so zero unambiguously means "no PING yet".
/// </para>
/// <para>
/// <b>Sync-status read-model exposure (Phase A — sync-status-read-model survey
/// 2026-06-19 §State 3 "needs-exposing").</b> <see cref="ConsecutiveFailures"/>
/// and <see cref="BackoffUntil"/> promote the daemon's per-peer dead-peer
/// bookkeeping (previously held only on the private <c>PeerState</c>) onto the
/// public membership snapshot so the <see cref="ISyncStatusReadModel"/> can
/// derive the SHOULD-waiting state ("peer unreachable, will retry") without
/// reaching into daemon internals. They are strictly THIS node's view of the
/// peer (distributed-status honesty): a non-zero <see cref="ConsecutiveFailures"/>
/// / a future <see cref="BackoffUntil"/> means "I have not reached this peer
/// recently", never "the peer is down". A successful round clears both.
/// </para>
/// </remarks>
/// <param name="Endpoint">Transport-level peer endpoint (unix socket path /
/// websocket URL / tcp host:port). Unique per peer on a given node.</param>
/// <param name="PublicKey">The peer's raw Ed25519 public key.</param>
/// <param name="LastSeenAt">UTC timestamp of the last <i>successful</i> round
/// with this peer (the "as of last contact" stamp). <see cref="DateTimeOffset.MinValue"/>
/// until the first successful exchange.</param>
/// <param name="LastSeenVectorClock">Reserved for Phase B per-peer convergence
/// (currently seeded <c>0</c>, never written — see the survey §State 1).</param>
/// <param name="LastSeenNonce">Highest replay-protection nonce seen from this
/// peer (sync-daemon-protocol §8).</param>
/// <param name="ConsecutiveFailures">Number of consecutive failed rounds with
/// this peer (the daemon's per-peer "strike" count). <c>0</c> when the peer was
/// last reached successfully. Promoted from the private dead-peer backoff
/// bookkeeping for the sync-status read-model (Phase A).</param>
/// <param name="BackoffUntil">If the peer is in dead-peer cool-off, the UTC
/// deadline until which it is skipped for gossip rounds; <c>null</c> when not
/// backing off. A future value ⇒ the peer is currently being waited on.
/// Promoted from the private <c>SkipUntil</c> for the sync-status read-model
/// (Phase A).</param>
/// <param name="CircuitState">Current evidence-driven circuit state for this
/// peer. Closed is the healthy/default state.</param>
public sealed record PeerInfo(
    string Endpoint,
    byte[] PublicKey,
    DateTimeOffset LastSeenAt,
    ulong LastSeenVectorClock,
    ulong LastSeenNonce = 0,
    int ConsecutiveFailures = 0,
    DateTimeOffset? BackoffUntil = null,
    PeerCircuitState CircuitState = PeerCircuitState.Closed);

/// <summary>Evidence-driven circuit state for one gossip peer.</summary>
public enum PeerCircuitState
{
    /// <summary>Normal operation; attempts are admitted.</summary>
    Closed = 0,

    /// <summary>The failure ratio crossed its configured threshold.</summary>
    Open = 1,

    /// <summary>One recovery probe is in flight and must supply evidence.</summary>
    HalfOpen = 2,
}

/// <summary>Payload of <see cref="IGossipDaemon.RoundCompleted"/>.</summary>
public sealed record GossipRoundCompletedEventArgs(
    int PeersSelected,
    int DeltasExchanged,
    int OpsReceived);

/// <summary>
/// Coarse classification of an observable event emitted through
/// <see cref="IGossipDaemon.FrameReceived"/>. The enum is deliberately
/// broader than the wire-level <c>MessageTypes</c> discriminator — it
/// folds in round-level outcomes (<see cref="HandshakeFailure"/>,
/// <see cref="GossipError"/>) so consumers can render them as
/// notifications without needing to dissect the receive-loop plumbing.
/// </summary>
public enum GossipFrameType
{
    /// <summary>An inbound HELLO (handshake) completed successfully.</summary>
    Hello = 0,

    /// <summary>An inbound GOSSIP_PING was received and accepted.</summary>
    GossipPing = 1,

    /// <summary>An inbound DELTA_STREAM frame was processed.</summary>
    DeltaStream = 2,

    /// <summary>
    /// The handshake against this peer failed (signature invalid,
    /// schema-incompatible, timeout during HELLO/CAPABILITY_NEG).
    /// </summary>
    HandshakeFailure = 3,

    /// <summary>
    /// A generic gossip-round error against this peer (connect failed,
    /// transport dropped mid-exchange, unexpected exception).
    /// </summary>
    GossipError = 4,
}

/// <summary>
/// Payload of <see cref="IGossipDaemon.FrameReceived"/>.
/// </summary>
/// <param name="PeerEndpoint">The sync-daemon endpoint of the peer that
/// produced the frame (transport-level; e.g. unix socket path or
/// websocket URL). Unique per peer on a given node.</param>
/// <param name="PeerNodeId">Opaque per-peer identifier (hex-encoded
/// 16-byte node id, lowercase). Empty string if the peer's identity has
/// not yet been validated (e.g. <see cref="GossipFrameType.HandshakeFailure"/>
/// before HELLO completed).</param>
/// <param name="FrameType">Which class of frame was observed.</param>
/// <param name="OccurredAt">UTC timestamp the frame was observed.</param>
/// <param name="Summary">Optional human-readable one-liner suitable for
/// a notification tooltip (e.g. <c>"alice sent gossip ping"</c>).
/// <c>null</c> means consumers should synthesise their own from
/// <see cref="FrameType"/>.</param>
/// <param name="ErrorCode">The structured protocol error code for a
/// failure frame, when one is known. Carried so consumers can branch on the
/// <i>specific</i> failure rather than the coarse <see cref="GossipFrameType"/>
/// — most importantly to split <see cref="Protocol.ErrorCode.PeerUntrusted"/>
/// (the security lane: "an untrusted device tried to join your fleet") from a
/// benign schema-mismatch / signature failure, which today both surface as a
/// generic <see cref="GossipFrameType.HandshakeFailure"/>. <c>null</c> for
/// success frames and for failures with no structured code (e.g. a transport
/// drop / round timeout). Added Phase A — sync-status-read-model survey
/// 2026-06-19 §State 4 "the SECURITY lane is COLLAPSED". No wire-protocol
/// change: the code is observed where the daemon already raises the frame.</param>
public sealed record GossipFrameEventArgs(
    string PeerEndpoint,
    string PeerNodeId,
    GossipFrameType FrameType,
    DateTimeOffset OccurredAt,
    string? Summary = null,
    ErrorCode? ErrorCode = null);
