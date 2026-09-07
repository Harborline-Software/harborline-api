using Harborline.Api.Kernel.Sync.Protocol;

namespace Harborline.Api.Kernel.Sync.Gossip;

/// <summary>
/// Per-team queryable projection of multi-device sync status, derived from the
/// <see cref="IGossipDaemon"/>'s EXISTING transient signals
/// (<see cref="IGossipDaemon.FrameReceived"/>, <see cref="IGossipDaemon.RoundCompleted"/>)
/// plus its <see cref="IGossipDaemon.KnownPeers"/> roster. Turns those
/// fire-and-forget events + private bookkeeping into a pull-able snapshot the
/// Harborline App UI reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase A (this surface).</b> Per the sync-status-read-model survey
/// (2026-06-19) and the PAO design (2026-06-19), Phase A is a ZERO-wire-protocol
/// projection: it exposes SHOULD (peer offline/waiting), COULDN'T (failed) +
/// the distinct SECURITY lane (<see cref="ErrorCode.PeerUntrusted"/>), a
/// cadence-based WILL ("round scheduled, next at T"), and a cadence-inferred HAS
/// ("a round succeeded recently and nothing is failing"). True per-peer
/// vector-clock convergence (provable HAS) is Phase B — this read is shaped so
/// Phase B enriches the per-peer <see cref="SyncPeerSnapshot"/> WITHOUT a reshape.
/// </para>
/// <para>
/// <b>Distributed-status honesty (survey guardrail / PAO G2).</b> Every value is
/// THIS node's view, "as of last contact" — the snapshot carries an
/// <see cref="SyncStatusSnapshot.AsOf"/> observer timestamp, and per-peer
/// <see cref="SyncPeerSnapshot.LastReachedAt"/> is the last <i>successful</i>
/// exchange, never an assertion the peer is currently online.
/// </para>
/// <para>
/// <b>The per-device offline threshold is NOT here (CIC 2026-06-20).</b> The
/// read-model exposes RAW <see cref="SyncPeerSnapshot.LastReachedAt"/> +
/// <see cref="SyncPeerSnapshot.OfflineDuration"/>; the calm→actionable threshold
/// (when "offline is normal" becomes "you should know") is per-device CONFIG the
/// UX applies. The daemon and this projection bake in no such threshold.
/// </para>
/// <para>
/// <b>Lifetime + hosting.</b> Registered per-team (alongside the gossip daemon in
/// the team child container) and subscribes to the daemon's events in its
/// constructor; it is a stateful projector. The host surfaces it through
/// <c>GET /api/local-node/sync-status</c> resolved from the active team's
/// provider (the <c>HostedJournalEntryApiEndpoint</c> precedent). Distinct from
/// the legacy <c>SyncState</c> / <c>SyncStateBadge</c> (Bridge-relay
/// connectivity — a different axis; survey guardrail G1 / PAO G1).
/// </para>
/// </remarks>
public interface ISyncStatusReadModel
{
    /// <summary>
    /// Take a current snapshot of this node's multi-device sync status: the
    /// worst-state-wins fleet aggregate, the per-peer rows, and the WILL
    /// round-cadence info. Pure read — never mutates daemon state.
    /// </summary>
    SyncStatusSnapshot Snapshot();
}

/// <summary>
/// The four-state, per-peer sync state (survey HAS / WILL / SHOULD / COULDN'T).
/// Distinct from the legacy <c>SyncState</c> enum (Bridge-relay connectivity).
/// The <see cref="Couldnt"/> state additionally carries a
/// <see cref="SyncPeerSnapshot.IsSecurityEvent"/> flag for the security lane —
/// the security treatment is a flag on COULDN'T, not a 5th state, but it MUST be
/// surfaced distinctly (PAO guardrail G3).
/// </summary>
public enum SyncPeerState
{
    /// <summary>A peer exchange succeeded and nothing is failing. This is not a
    /// claim that either peer holds globally current data.</summary>
    Has,

    /// <summary>A change is in flight / a round is scheduled (Phase A: cadence —
    /// "round scheduled, next at T").</summary>
    Will,

    /// <summary>Waiting — the peer is unreachable but the daemon WILL retry
    /// (backing off). "Retrying", as distinct from COULDN'T's "won't, without
    /// intervention".</summary>
    Should,

    /// <summary>Tried and failed — needs attention. Carries the security
    /// sub-lane via <see cref="SyncPeerSnapshot.IsSecurityEvent"/>.</summary>
    Couldnt,
}

/// <summary>
/// One peer's row in the sync-status snapshot. The shared read-model contract
/// the Harborline App UI consumes (matched 1:1 by the HTTP wire shape). All values are
/// THIS node's view "as of last contact".
/// </summary>
/// <param name="DeviceId">Opaque per-peer identifier (hex node id, lowercase) —
/// the first 16 bytes of the peer public key, matching the daemon's
/// <c>peerNodeId</c>. Empty until the peer's identity is validated.</param>
/// <param name="Label">Human-readable label for the peer; falls back to the
/// transport endpoint when no friendly name is known.</param>
/// <param name="State">The four-state for this peer.</param>
/// <param name="LastReachedAt">UTC timestamp of the last SUCCESSFUL exchange
/// with this peer, or <c>null</c> if never reached. The "as of" honesty stamp —
/// NOT a claim the peer is currently online.</param>
/// <param name="OfflineDuration">How long since <see cref="LastReachedAt"/>
/// (relative to the snapshot's observer clock), or <c>null</c> if never reached.
/// RAW duration only — the per-device calm→actionable threshold is UX config,
/// NOT baked here (CIC 2026-06-20).</param>
/// <param name="IsSecurityEvent">True when this peer's COULDN'T is the SECURITY
/// lane — an untrusted device that tried to join the fleet
/// (<see cref="ErrorCode.PeerUntrusted"/>). Drives the distinct shield treatment
/// (PAO G3). Always false for non-COULDN'T states.</param>
/// <param name="ErrorCode">The structured protocol error code for a COULDN'T
/// peer, when known (the discriminator the daemon now carries on the frame);
/// <c>null</c> otherwise.</param>
public sealed record SyncPeerSnapshot(
    string DeviceId,
    string Label,
    SyncPeerState State,
    DateTimeOffset? LastReachedAt,
    TimeSpan? OfflineDuration,
    bool IsSecurityEvent,
    ErrorCode? ErrorCode);

/// <summary>
/// WILL cadence info — when the next anti-entropy round is scheduled and the
/// configured round interval. Drives the "round scheduled, next at T" WILL copy.
/// </summary>
/// <param name="NextRoundAt">UTC timestamp the next periodic round is expected,
/// or <c>null</c> if no round has completed yet (cadence not yet established).
/// Phase A WILL is cadence-based; true per-peer unacked-delta is Phase B.</param>
/// <param name="RoundIntervalSeconds">The configured periodic round interval
/// (<see cref="GossipDaemonOptions.RoundIntervalSeconds"/>).</param>
public sealed record SyncCadence(
    DateTimeOffset? NextRoundAt,
    int RoundIntervalSeconds);

/// <summary>
/// The full sync-status snapshot: the worst-state-wins fleet aggregate, the
/// per-peer rows, the WILL cadence, and the observer's "as of" clock.
/// </summary>
/// <param name="Aggregate">The fleet-level worst-state-wins roll-up of all known
/// peers (any COULDN'T-security dominates, then COULDN'T, then SHOULD, then WILL;
/// HAS only if ALL known peers are HAS). HAS when there are no known peers (a
/// solo node has nothing failing).</param>
/// <param name="Peers">Per-peer rows. Empty for a node with no known peers.</param>
/// <param name="Cadence">The WILL round-cadence info.</param>
/// <param name="AsOf">The observer (this node) clock at snapshot time — the
/// distributed-status honesty stamp the UI renders as "(my view)".</param>
public sealed record SyncStatusSnapshot(
    SyncPeerState Aggregate,
    IReadOnlyList<SyncPeerSnapshot> Peers,
    SyncCadence Cadence,
    DateTimeOffset AsOf);
