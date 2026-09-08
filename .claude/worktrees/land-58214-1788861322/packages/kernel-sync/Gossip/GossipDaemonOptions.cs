namespace Harborline.Api.Kernel.Sync.Gossip;

/// <summary>
/// Tunable knobs for the <see cref="GossipDaemon"/>. Bound via
/// <see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/>.
/// </summary>
/// <remarks>
/// Defaults match paper §6.1 / sync-daemon-protocol §2.3: 30-second gossip
/// tick, two random peers per round, 5-second connect timeout, 60-second
/// dead-peer cool-off window. Override per-deployment if the local topology
/// needs a different cadence.
/// </remarks>
public sealed class GossipDaemonOptions
{
    /// <summary>
    /// Seconds between gossip rounds. Default 30, matching paper §6.1.
    /// Tests typically set this to 1.
    /// </summary>
    public int RoundIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Number of random peers gossiped to per round. Default 2 per paper §6.1
    /// ("two random peers" — open question in the spec leaves this a knob).
    /// </summary>
    public int PeerPickCount { get; set; } = 2;

    /// <summary>
    /// Maximum concurrent immediate pushes in the foreground bulkhead.
    /// Default 1 preserves the existing serialized, coalescing behavior for
    /// deployments that use only this class. Must be ≥ 1.
    /// </summary>
    public int ForegroundPushConcurrency { get; set; } = 1;

    /// <summary>
    /// Maximum concurrent immediate pushes in the background bulkhead.
    /// Default 1 preserves the existing serialized, coalescing behavior for
    /// deployments that use only this class. Must be ≥ 1.
    /// </summary>
    public int BackgroundPushConcurrency { get; set; } = 1;

    /// <summary>
    /// How long a single peer connect + handshake is allowed to take before
    /// the peer is marked dead for the current round. Default 5 seconds
    /// (sync-daemon-protocol §7 timeout schedule).
    /// </summary>
    public int ConnectTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Wall-clock budget for the post-handshake GOSSIP_PING + DELTA_STREAM
    /// exchange. Default 30 seconds. Exceeding it emits the named
    /// <c>DELTA_STREAM_DEADLINE_EXCEEDED</c> error instead of leaving a round
    /// hung indefinitely. Must be ≥ 1.
    /// </summary>
    public int DeltaStreamDeadlineSeconds { get; set; } = 30;

    /// <summary>
    /// Initial cool-off window after a peer times out. The actual skip window
    /// grows exponentially on repeated failures (doubles per strike, capped
    /// at 4× this value), then uses full jitter over the upper half of that
    /// window. Thus the absolute ceiling is 4× this value. A successful round
    /// resets the counter and clears the skip window entirely.
    /// </summary>
    public int DeadPeerBackoffSeconds { get; set; } = 60;

    /// <summary>
    /// Sliding window, in seconds, used to calculate the per-peer failure
    /// ratio. Default 600 seconds so the four-attempt floor remains reachable
    /// under the default escalating backoff. Must be ≥ 1.
    /// </summary>
    public int CircuitBreakerSamplingWindowSeconds { get; set; } = 600;

    /// <summary>
    /// Minimum completed attempts in the sampling window before a peer's
    /// circuit may open. Default 4, preventing a low-traffic blip from
    /// breaking the peer. Must be ≥ 1.
    /// </summary>
    public int CircuitBreakerMinimumThroughput { get; set; } = 4;

    /// <summary>
    /// Failure ratio that opens a peer's circuit once minimum throughput has
    /// been met. Default 0.5. Values are clamped to the inclusive 0–1 range.
    /// </summary>
    public double CircuitBreakerFailureRatio { get; set; } = 0.5;

    /// <summary>
    /// Per-peer DELTA_STREAM budget, enforced by
    /// <see cref="Protocol.DeltaStreamRateLimiter"/>. Default 1000 per
    /// sync-daemon-protocol §8 "Rate limiting". Incoming DELTA_STREAM
    /// frames above this rate are dropped and logged at warning level.
    /// </summary>
    public int MaxDeltaStreamPerSecondPerPeer { get; set; } = 1000;

    // ------------------------------------------------------------------
    // Accept-loop DoS caps (multi-device INC-5, #1261 finding #2)
    // ------------------------------------------------------------------
    // When the daemon binds an inbound listener (IPAddress.Any on a LAN),
    // an unauthenticated peer can hold a pre-auth session that buffers up
    // to the 16 MiB frame cap. These knobs bound the blast radius BEFORE
    // the handshake reads any frame: a concurrency cap bounds total
    // in-flight pre-auth sessions, a per-IP cap bounds a single host's
    // share of them, and a per-handshake deadline bounds how long a slow
    // peer can pin one of those slots.

    /// <summary>
    /// Maximum number of inbound connections whose handshake may be
    /// in-flight at once on the accept loop. A connection that arrives
    /// while the cap is saturated is closed immediately (before any frame
    /// is read), so a flood cannot force unbounded concurrent pre-auth
    /// 16 MiB allocations. Default 64. Must be ≥ 1.
    /// </summary>
    public int MaxConcurrentInboundHandshakes { get; set; } = 64;

    /// <summary>
    /// Maximum number of simultaneously in-flight inbound handshakes from a
    /// single remote IP. Bounds a single host's share of the global
    /// concurrency budget so one attacker cannot starve legitimate peers.
    /// Default 8. Must be ≥ 1.
    /// </summary>
    public int MaxConcurrentInboundHandshakesPerIp { get; set; } = 8;

    /// <summary>
    /// Wall-clock budget for a single inbound handshake (HELLO ⇄ HELLO →
    /// CAPABILITY_NEG → ACK) on the accept loop. A peer that does not
    /// complete the handshake within this window has its connection closed,
    /// freeing the concurrency slot. Default 5 seconds. Must be ≥ 1.
    /// </summary>
    public int InboundHandshakeDeadlineSeconds { get; set; } = 5;
}
