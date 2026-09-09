using System.Collections.Concurrent;

using Microsoft.Extensions.Options;

using Harborline.Api.Kernel.Sync.Protocol;

namespace Harborline.Api.Kernel.Sync.Gossip;

/// <summary>
/// Per-team <see cref="ISyncStatusReadModel"/> — a state projector over the
/// gossip daemon's EXISTING transient events. Subscribes to
/// <see cref="IGossipDaemon.FrameReceived"/> + <see cref="IGossipDaemon.RoundCompleted"/>
/// in its constructor, accumulates the per-peer failure / security bookkeeping
/// the events carry, and on <see cref="Snapshot"/> reads the live
/// <see cref="IGossipDaemon.KnownPeers"/> roster (which now exposes
/// <see cref="PeerInfo.LastSeenAt"/> / <see cref="PeerInfo.ConsecutiveFailures"/>
/// / <see cref="PeerInfo.BackoffUntil"/>) to derive the four UX states.
/// </summary>
/// <remarks>
/// <para>
/// <b>Zero wire-protocol change.</b> This is a pure projection — it never sends
/// a frame, never mutates daemon / trust / transport / CRDT state. It only reads
/// the signals the daemon already raises (survey: ~70% derivable-today, the rest
/// the additive <see cref="PeerInfo"/> / <see cref="GossipFrameEventArgs"/>
/// fields this Phase-A change exposes).
/// </para>
/// <para>
/// <b>Four-state derivation (survey §Recommendation, top-down — first match
/// wins, per peer):</b>
/// <list type="number">
///   <item><b>COULDN'T</b> — a recorded NON-RECOVERABLE failure for the peer (a
///     trust reject / signature / schema mismatch). If the last failure code is
///     <see cref="ErrorCode.PeerUntrusted"/> the peer is also flagged
///     <see cref="SyncPeerSnapshot.IsSecurityEvent"/> — the SECURITY lane, which
///     you do not "retry" (PAO G3). COULDN'T = "won't, without intervention".</item>
///   <item><b>SHOULD</b> — the peer is in dead-peer backoff
///     (<see cref="PeerInfo.BackoffUntil"/> in the future) OR has unreset strikes
///     (<see cref="PeerInfo.ConsecutiveFailures"/> &gt; 0). The daemon WILL retry;
///     "waiting", as distinct from COULDN'T.</item>
///   <item><b>WILL</b> — a round is scheduled (cadence) and the peer has not yet
///     been reached this session (no <see cref="PeerInfo.LastSeenAt"/>) — a
///     change is on its way. Phase A is cadence-based; true per-peer
///     unacked-delta is Phase B.</item>
///   <item><b>HAS</b> — the peer was reached successfully
///     (<see cref="PeerInfo.LastSeenAt"/> set) and nothing is failing. Phase A
///     earns HAS via "a round succeeded"; Phase B will make it provable via
///     per-peer clock comparison WITHOUT reshaping this record.</item>
/// </list>
/// </para>
/// <para>
/// <b>Thread-safety.</b> <see cref="IGossipDaemon.FrameReceived"/> handlers run
/// synchronously on the daemon's round-loop task; <see cref="Snapshot"/> is
/// called from the HTTP request thread. The per-peer failure map is a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> and the last-round fields are
/// read/written via <see cref="System.Threading.Volatile"/>, so a snapshot taken
/// concurrently with an event sees a consistent-enough view (the read is
/// advisory status, not a transactional invariant).
/// </para>
/// </remarks>
public sealed class SyncStatusReadModel : ISyncStatusReadModel, IDisposable
{
    /// <summary>
    /// Max distinct ROSTER endpoints we track failure bookkeeping for. A roster
    /// peer is a known, bounded set (your own fleet), so this is a generous
    /// belt-and-suspenders cap — it should never be reached in practice, but it
    /// guarantees <see cref="_failures"/> cannot grow without bound even under a
    /// pathological churn of roster endpoints.
    /// </summary>
    internal const int MaxRosterFailures = 256;

    /// <summary>
    /// Max distinct ATTACKER IPs we keep an off-roster security row for. This is
    /// the flood-facing bound (S1): an untrusted-connection flood from many
    /// source IPs cannot inflate the read-model's heap or every status payload
    /// beyond this many coalesced rows. When exceeded, the oldest entries are
    /// evicted (LRU-by-first-seen) and the synthesized payload carries a
    /// "+N more blocked" summary row instead of an unbounded list.
    /// </summary>
    internal const int MaxOffRosterSecurityIps = 64;

    /// <summary>
    /// How long an off-roster security entry lives without a fresh untrusted
    /// frame from the same IP before it ages out. A stranger that stops knocking
    /// stops occupying a slot — the security view shows recent intrusion
    /// attempts, not an unbounded historical ledger.
    /// </summary>
    internal static readonly TimeSpan OffRosterSecurityTtl = TimeSpan.FromHours(24);

    /// <summary>
    /// Max off-roster security ROWS synthesized into a single
    /// <see cref="Snapshot"/> payload. Independent of (and ≤) the map cap so the
    /// route payload is bounded even if the map cap is later raised. When the
    /// number of distinct attacker IPs exceeds this, the payload carries up to
    /// this many rows plus ONE "+N more blocked" summary row.
    /// </summary>
    internal const int MaxOffRosterSecurityRows = 32;

    private readonly IGossipDaemon _daemon;
    private readonly TimeProvider _timeProvider;
    private readonly int _roundIntervalSeconds;

    // Per-peer last-failure bookkeeping for ROSTER peers, keyed by transport
    // endpoint (the stable key the daemon uses for membership). Cleared for a
    // peer when a success frame (Hello / GossipPing / DeltaStream) is observed
    // from it. Bounded by MaxRosterFailures (belt-and-suspenders — the roster is
    // a small known set).
    private readonly ConcurrentDictionary<string, PeerFailure> _failures =
        new(StringComparer.Ordinal);

    // S1 — the FLOOD-FACING bound. Off-roster PeerUntrusted failures are an
    // attacker-controlled surface: a stranger never sends a success frame from
    // its ephemeral-port endpoint, so keying these by IP:ephemeral-port (as the
    // frame carries) means every reconnect is a new, never-evicted key →
    // unbounded heap + payload growth. We instead COALESCE by stable IP (one row
    // per attacker IP, not per port), CAP the distinct-IP count, and TTL-evict
    // stale entries. This is the read-model's own bookkeeping; the daemon's
    // accept-loop DoS caps (concurrent-per-IP) are unchanged and orthogonal.
    private readonly ConcurrentDictionary<string, OffRosterSecurity> _offRosterSecurity =
        new(StringComparer.Ordinal);

    // Last completed round wall-clock, for the WILL cadence ("next round at T").
    // null until the first RoundCompleted fires.
    private long _lastRoundAtTicks;
    private bool _anyRoundCompleted;

    private readonly EventHandler<GossipFrameEventArgs> _onFrame;
    private readonly EventHandler<GossipRoundCompletedEventArgs> _onRound;
    private bool _disposed;

    /// <summary>
    /// Construct the projector bound to <paramref name="daemon"/> and subscribe
    /// to its events. The round interval is read from
    /// <paramref name="options"/> for the WILL cadence. The daemon's lifetime is
    /// owned by the per-team provider, not this projector; <see cref="Dispose"/>
    /// only detaches the event handlers.
    /// </summary>
    public SyncStatusReadModel(
        IGossipDaemon daemon,
        IOptions<GossipDaemonOptions> options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(daemon);
        ArgumentNullException.ThrowIfNull(options);

        _daemon = daemon;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _roundIntervalSeconds = Math.Max(1, options.Value.RoundIntervalSeconds);

        _onFrame = OnFrameReceived;
        _onRound = OnRoundCompleted;
        _daemon.FrameReceived += _onFrame;
        _daemon.RoundCompleted += _onRound;
    }

    /// <inheritdoc />
    public SyncStatusSnapshot Snapshot()
    {
        var asOf = _timeProvider.GetUtcNow();

        var roster = _daemon.KnownPeers.ToList();

        var peers = roster
            .Select(p => DerivePeer(p, asOf))
            .ToList();

        // Off-roster SECURITY lane (PAO design §4: "Unknown device — Blocked,
        // not in your fleet"). An inbound untrusted device the accept loop
        // rejected is NEVER added to KnownPeers (it is a stranger), so it has no
        // roster row — yet it is exactly the security event the UX must surface.
        //
        // S1: these rows are COALESCED BY ATTACKER IP and BOUNDED. A reconnect
        // flood from one IP across many ephemeral ports is ONE row (with an
        // attempt count), not one row per port; the distinct-IP map is capped +
        // TTL-evicted; and the synthesized payload itself is capped at
        // MaxOffRosterSecurityRows with a single "+N more blocked" summary row
        // when more attacker IPs exist than rows we list. So neither the heap nor
        // the GET /sync-status payload grows without bound under a flood.
        AppendOffRosterSecurityRows(peers, asOf);

        var aggregate = RollUp(peers);

        DateTimeOffset? nextRoundAt = null;
        if (Volatile.Read(ref _anyRoundCompleted))
        {
            var lastRound = new DateTimeOffset(Volatile.Read(ref _lastRoundAtTicks), TimeSpan.Zero);
            nextRoundAt = lastRound.AddSeconds(_roundIntervalSeconds);
        }

        return new SyncStatusSnapshot(
            Aggregate: aggregate,
            Peers: peers,
            Cadence: new SyncCadence(nextRoundAt, _roundIntervalSeconds),
            AsOf: asOf);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _daemon.FrameReceived -= _onFrame;
        _daemon.RoundCompleted -= _onRound;
    }

    // ── Event accumulation ───────────────────────────────────────────────────

    private void OnFrameReceived(object? sender, GossipFrameEventArgs e)
    {
        switch (e.FrameType)
        {
            case GossipFrameType.Hello:
            case GossipFrameType.GossipPing:
            case GossipFrameType.DeltaStream:
                // A successful observable frame from this peer — clear any
                // recorded failure so the peer can return to HAS/WILL. (The
                // daemon's OnRoundSucceeded also clears ConsecutiveFailures /
                // BackoffUntil on PeerInfo; this clears OUR failure record so a
                // recovered peer stops reading COULDN'T.) A roster peer's success
                // also clears any off-roster security row coalesced under its IP
                // (the peer is now trusted/on-roster, no longer a stranger).
                _failures.TryRemove(e.PeerEndpoint, out _);
                _offRosterSecurity.TryRemove(ExtractIp(e.PeerEndpoint), out _);
                break;

            case GossipFrameType.HandshakeFailure:
            case GossipFrameType.GossipError:
                RecordFailure(e);
                break;
        }
    }

    /// <summary>
    /// Record the most-recent failure for the frame's peer. The structured
    /// <see cref="ErrorCode"/> (when carried) is the discriminator: PeerUntrusted
    /// is the SECURITY lane; the other handshake codes are non-recoverable
    /// COULDN'T; a code-less GossipError is a transient round failure (SHOULD
    /// territory — see DerivePeer).
    /// <para>
    /// <b>S1 split.</b> A <see cref="ErrorCode.PeerUntrusted"/> failure that does
    /// not match a roster endpoint is the attacker-controlled off-roster lane: it
    /// is coalesced by stable IP into the bounded <see cref="_offRosterSecurity"/>
    /// map (one entry per attacker IP, cap + TTL), NOT recorded in the
    /// roster-keyed <see cref="_failures"/> map. This bounds heap + payload growth
    /// under a reconnect flood (each reconnect uses a fresh ephemeral port and so
    /// would otherwise mint a new never-evicted key). A failure that DOES match a
    /// roster endpoint stays in the roster map (the success-frame eviction path).
    /// </para>
    /// </summary>
    private void RecordFailure(GossipFrameEventArgs e)
    {
        // Off-roster security lane: an inbound PeerUntrusted reject from an
        // endpoint that is not (yet) a roster peer. Coalesce by IP into the
        // bounded map. We test roster membership against the live roster so a
        // failure from a known peer (a transient flap of an on-roster device)
        // stays in the roster-keyed map and follows the normal eviction path.
        if (e.ErrorCode == ErrorCode.PeerUntrusted && !IsRosterEndpoint(e.PeerEndpoint))
        {
            RecordOffRosterSecurity(e);
            return;
        }

        // Roster (or non-security off-roster) failure: keep in the roster-keyed
        // map. Bounded by MaxRosterFailures as a belt-and-suspenders guard.
        if (_failures.Count >= MaxRosterFailures && !_failures.ContainsKey(e.PeerEndpoint))
        {
            EvictOldest(_failures, f => f.At);
        }
        _failures[e.PeerEndpoint] = new PeerFailure(e.ErrorCode, e.OccurredAt, e.PeerNodeId);
    }

    /// <summary>
    /// Coalesce an off-roster <see cref="ErrorCode.PeerUntrusted"/> reject by
    /// stable IP into the bounded <see cref="_offRosterSecurity"/> map. N
    /// reconnects from one attacker IP (across N ephemeral ports) collapse to ONE
    /// entry whose <see cref="OffRosterSecurity.Attempts"/> count increments.
    /// Caps the distinct-IP count (LRU-by-first-seen eviction) and prunes TTL-
    /// expired entries opportunistically.
    /// </summary>
    private void RecordOffRosterSecurity(GossipFrameEventArgs e)
    {
        var ip = ExtractIp(e.PeerEndpoint);
        var now = _timeProvider.GetUtcNow();

        // Opportunistic TTL prune so a flood of distinct IPs doesn't keep stale
        // entries alive purely by filling the map.
        PruneExpiredOffRoster(now);

        _offRosterSecurity.AddOrUpdate(
            ip,
            // First time we see this IP: cap before inserting a NEW key.
            _ =>
            {
                if (_offRosterSecurity.Count >= MaxOffRosterSecurityIps)
                {
                    EvictOldest(_offRosterSecurity, s => s.FirstSeen);
                }
                return new OffRosterSecurity(
                    FirstSeen: now,
                    LastSeen: now,
                    Attempts: 1,
                    LastNodeId: e.PeerNodeId,
                    LastEndpoint: e.PeerEndpoint);
            },
            // Subsequent reconnect from the same IP: COALESCE — bump the count
            // and refresh LastSeen, do NOT add a row.
            (_, existing) => existing with
            {
                LastSeen = now,
                Attempts = existing.Attempts + 1,
                LastNodeId = string.IsNullOrEmpty(e.PeerNodeId) ? existing.LastNodeId : e.PeerNodeId,
                LastEndpoint = e.PeerEndpoint,
            });
    }

    private void PruneExpiredOffRoster(DateTimeOffset now)
    {
        foreach (var kvp in _offRosterSecurity)
        {
            if (now - kvp.Value.LastSeen > OffRosterSecurityTtl)
            {
                _offRosterSecurity.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <summary>
    /// Evict the oldest entry from a bounded map by the supplied age selector
    /// (LRU-by-first-seen / oldest-failure). Best-effort under concurrency — a
    /// racing insert may briefly exceed the cap by one, which is acceptable for
    /// an advisory status surface (the next insert re-trims).
    /// </summary>
    private static void EvictOldest<TValue>(
        ConcurrentDictionary<string, TValue> map,
        Func<TValue, DateTimeOffset> age)
    {
        string? oldestKey = null;
        var oldest = DateTimeOffset.MaxValue;
        foreach (var kvp in map)
        {
            var a = age(kvp.Value);
            if (a < oldest)
            {
                oldest = a;
                oldestKey = kvp.Key;
            }
        }
        if (oldestKey is not null)
        {
            map.TryRemove(oldestKey, out _);
        }
    }

    private bool IsRosterEndpoint(string endpoint) =>
        _daemon.KnownPeers.Any(p => string.Equals(p.Endpoint, endpoint, StringComparison.Ordinal));

    /// <summary>
    /// Parse a STABLE per-IP key from a transport endpoint string
    /// (<c>tcp://host:port</c>, bare <c>host:port</c>, IPv6 <c>[::1]:port</c>, or
    /// a socket path). The ephemeral port is dropped so all connections from one
    /// host coalesce to a single key — mirrors the daemon's <c>ExtractRemoteIp</c>
    /// so a frame endpoint and the daemon's per-IP budget agree.
    /// </summary>
    internal static string ExtractIp(string endpoint)
    {
        if (string.IsNullOrEmpty(endpoint))
        {
            return "unknown";
        }
        var s = endpoint;
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            s = s[(scheme + 3)..];
        }
        // Not host:port (e.g. a unix socket path) — one shared local key.
        if (s.StartsWith('/', StringComparison.Ordinal))
        {
            return "local-uds";
        }
        // IPv6 literal [::1]:port → ::1
        if (s.StartsWith('[', StringComparison.Ordinal))
        {
            var close = s.IndexOf(']', StringComparison.Ordinal);
            return close > 0 ? s[1..close] : s;
        }
        var colon = s.LastIndexOf(':', StringComparison.Ordinal);
        return colon > 0 ? s[..colon] : s;
    }

    private void OnRoundCompleted(object? sender, GossipRoundCompletedEventArgs e)
    {
        Volatile.Write(ref _lastRoundAtTicks, _timeProvider.GetUtcNow().UtcTicks);
        Volatile.Write(ref _anyRoundCompleted, true);
    }

    // ── Off-roster security rows (bounded, IP-coalesced) ─────────────────────

    /// <summary>
    /// Synthesize the COULDN'T·security rows for off-roster untrusted devices
    /// from the IP-coalesced <see cref="_offRosterSecurity"/> map, appending them
    /// to <paramref name="peers"/>. TTL-expired entries are pruned first. The
    /// output is bounded at <see cref="MaxOffRosterSecurityRows"/>: if more
    /// distinct attacker IPs exist, the most-recent rows are listed (newest
    /// <see cref="OffRosterSecurity.LastSeen"/> first) and a single
    /// "+N more blocked" summary row stands in for the remainder — so the payload
    /// is bounded under a flood regardless of the map cap.
    /// </summary>
    private void AppendOffRosterSecurityRows(List<SyncPeerSnapshot> peers, DateTimeOffset asOf)
    {
        PruneExpiredOffRoster(asOf);

        if (_offRosterSecurity.IsEmpty)
        {
            return;
        }

        // Snapshot newest-first so the listed rows are the most recent intrusion
        // attempts; the rest collapse into the summary row.
        var ordered = _offRosterSecurity
            .OrderByDescending(kvp => kvp.Value.LastSeen)
            .ToList();

        var listed = Math.Min(ordered.Count, MaxOffRosterSecurityRows);
        for (var i = 0; i < listed; i++)
        {
            var (ip, entry) = (ordered[i].Key, ordered[i].Value);
            peers.Add(new SyncPeerSnapshot(
                DeviceId: entry.LastNodeId,
                Label: ip,
                State: SyncPeerState.Couldnt,
                LastReachedAt: null,
                OfflineDuration: null,
                IsSecurityEvent: true,
                ErrorCode: ErrorCode.PeerUntrusted));
        }

        var remaining = ordered.Count - listed;
        if (remaining > 0)
        {
            // ONE summary row for the overflow — payload stays bounded.
            peers.Add(new SyncPeerSnapshot(
                DeviceId: string.Empty,
                Label: $"+{remaining} more blocked",
                State: SyncPeerState.Couldnt,
                LastReachedAt: null,
                OfflineDuration: null,
                IsSecurityEvent: true,
                ErrorCode: ErrorCode.PeerUntrusted));
        }
    }

    // ── Four-state derivation ────────────────────────────────────────────────

    private SyncPeerSnapshot DerivePeer(PeerInfo peer, DateTimeOffset asOf)
    {
        var deviceId = DeriveDeviceId(peer);
        var lastReachedAt = peer.LastSeenAt == DateTimeOffset.MinValue
            ? (DateTimeOffset?)null
            : peer.LastSeenAt;
        var offlineDuration = lastReachedAt is { } reached && asOf > reached
            ? asOf - reached
            : (TimeSpan?)null;

        var failure = _failures.GetValueOrDefault(peer.Endpoint);

        // 1) COULDN'T — a recorded NON-RECOVERABLE failure (security reject /
        //    signature / schema). These do not self-heal by retry. A code-less
        //    GossipError is NOT here — that is a transient round failure that
        //    falls through to SHOULD (the daemon keeps retrying it).
        if (failure is not null && IsNonRecoverable(failure.Code))
        {
            var isSecurity = failure.Code == ErrorCode.PeerUntrusted;
            return new SyncPeerSnapshot(
                DeviceId: deviceId,
                Label: peer.Endpoint,
                State: SyncPeerState.Couldnt,
                LastReachedAt: lastReachedAt,
                OfflineDuration: offlineDuration,
                IsSecurityEvent: isSecurity,
                ErrorCode: failure.Code);
        }

        // 2) SHOULD — the peer is unreachable but the daemon WILL retry: it is in
        //    dead-peer backoff (BackoffUntil in the future) OR has unreset
        //    strikes, OR has a recent code-less transient failure recorded.
        var inBackoff = peer.BackoffUntil is { } until && until > asOf;
        var hasStrikes = peer.ConsecutiveFailures > 0;
        var hasTransientFailure = failure is not null && !IsNonRecoverable(failure.Code);
        if (inBackoff || hasStrikes || hasTransientFailure)
        {
            return new SyncPeerSnapshot(
                DeviceId: deviceId,
                Label: peer.Endpoint,
                State: SyncPeerState.Should,
                LastReachedAt: lastReachedAt,
                OfflineDuration: offlineDuration,
                IsSecurityEvent: false,
                ErrorCode: failure?.Code);
        }

        // 3) WILL — a change is in flight / a round is scheduled and this peer
        //    has not yet been reached (no successful exchange recorded). Phase A
        //    is cadence-based: a freshly-added peer reads WILL until the first
        //    successful round flips it to HAS.
        if (lastReachedAt is null)
        {
            return new SyncPeerSnapshot(
                DeviceId: deviceId,
                Label: peer.Endpoint,
                State: SyncPeerState.Will,
                LastReachedAt: null,
                OfflineDuration: null,
                IsSecurityEvent: false,
                ErrorCode: null);
        }

        // 4) HAS — reached successfully, nothing failing. (Phase B will make this
        //    a provable per-peer clock comparison; Phase A infers it from "a
        //    round succeeded recently and nothing is failing".)
        return new SyncPeerSnapshot(
            DeviceId: deviceId,
            Label: peer.Endpoint,
            State: SyncPeerState.Has,
            LastReachedAt: lastReachedAt,
            OfflineDuration: offlineDuration,
            IsSecurityEvent: false,
            ErrorCode: null);
    }

    /// <summary>
    /// Worst-state-wins roll-up for the fleet badge: any COULDN'T dominates, then
    /// SHOULD, then WILL; HAS only if ALL known peers are HAS (or there are no
    /// known peers — a solo node has nothing failing). Within COULDN'T the
    /// security lane is surfaced per-peer (the aggregate is still COULDN'T; the
    /// UI escalates the security treatment from the flagged peer row).
    /// </summary>
    private static SyncPeerState RollUp(IReadOnlyList<SyncPeerSnapshot> peers)
    {
        if (peers.Count == 0)
        {
            return SyncPeerState.Has;
        }
        if (peers.Any(p => p.State == SyncPeerState.Couldnt))
        {
            return SyncPeerState.Couldnt;
        }
        if (peers.Any(p => p.State == SyncPeerState.Should))
        {
            return SyncPeerState.Should;
        }
        if (peers.Any(p => p.State == SyncPeerState.Will))
        {
            return SyncPeerState.Will;
        }
        return SyncPeerState.Has;
    }

    /// <summary>
    /// A failure code the daemon will NOT self-heal by retry — the COULDN'T set.
    /// The security lane (<see cref="ErrorCode.PeerUntrusted"/>) and the
    /// integrity / schema rejects are non-recoverable; a code-less or transport
    /// failure (<c>null</c>) is transient and stays in SHOULD (the daemon retries
    /// it with backoff).
    /// </summary>
    private static bool IsNonRecoverable(ErrorCode? code) => code switch
    {
        ErrorCode.PeerUntrusted => true,
        ErrorCode.HelloSignatureInvalid => true,
        ErrorCode.SchemaVersionIncompatible => true,
        _ => false,
    };

    /// <summary>
    /// Per-peer device id = first 16 bytes of the public key, lowercase hex —
    /// matching the daemon's <c>peerNodeId</c> rendering so a frame event and a
    /// roster row line up.
    /// </summary>
    private static string DeriveDeviceId(PeerInfo peer)
    {
        if (peer.PublicKey is null || peer.PublicKey.Length == 0)
        {
            return string.Empty;
        }
        var len = Math.Min(16, peer.PublicKey.Length);
        return Convert.ToHexString(peer.PublicKey.AsSpan(0, len)).ToLowerInvariant();
    }

    /// <summary>
    /// The last failure observed for a peer (structured code + when + the peer's
    /// hex node id, kept so an off-roster security row can still carry a device id).
    /// </summary>
    private sealed record PeerFailure(ErrorCode? Code, DateTimeOffset At, string PeerNodeId);

    /// <summary>
    /// One off-roster attacker IP's coalesced security bookkeeping (S1). All
    /// untrusted reconnects from one IP (across many ephemeral ports) collapse
    /// into a single entry: <see cref="Attempts"/> counts them,
    /// <see cref="LastSeen"/> drives TTL eviction + newest-first ordering, and
    /// <see cref="FirstSeen"/> drives LRU eviction when the distinct-IP cap is
    /// hit. <see cref="LastNodeId"/> / <see cref="LastEndpoint"/> keep the most
    /// recent identity material for the synthesized row (the inbound reject
    /// carries an empty node id, so this is usually empty — the IP label is the
    /// load-bearing identifier).
    /// </summary>
    private sealed record OffRosterSecurity(
        DateTimeOffset FirstSeen,
        DateTimeOffset LastSeen,
        long Attempts,
        string LastNodeId,
        string LastEndpoint);
}
