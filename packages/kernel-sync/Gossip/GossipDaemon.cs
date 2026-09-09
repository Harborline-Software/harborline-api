using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Sync.Handshake;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Network;
using Harborline.Api.Kernel.Sync.Protocol;
using Harborline.Api.Kernel.Sync.Restore;

namespace Harborline.Api.Kernel.Sync.Gossip;

/// <summary>
/// Paper §6.1 gossip-based anti-entropy daemon. See <see cref="IGossipDaemon"/>
/// for the contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Concurrency model:</b> the daemon drives rounds with a
/// <see cref="PeriodicTimer"/> rather than wrapping a
/// <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>. The reason is
/// that this package is consumed both from Hosting-backed apps (where a
/// BackgroundService would be natural) and from lightweight CLI / test
/// harnesses where pulling in Hosting just to get a background loop is
/// overkill. PeriodicTimer gives us clean cancellation, deterministic ticks,
/// and zero Hosting dependency.
/// </para>
/// <para>
/// <b>Dead-peer backoff.</b> A peer that times out or errors during a round
/// gets a "strike"; the next round skips it for a jittered,
/// exponentially-growing window (doubles per strike, capped at 4× the
/// configured base). A successful
/// round clears both the strike count and the skip deadline. Rebound is
/// immediate — a reconnected peer does not "slow-start" back into the
/// rotation.
/// </para>
/// <para>
/// <b>Failure-ratio breaker.</b> Completed attempts are measured per peer over
/// a sliding window. The circuit opens only when both the configured failure
/// ratio and minimum-throughput floor are met. Once its jittered backoff has
/// elapsed, exactly one attempt enters half-open; elapsed time alone does not
/// close the circuit. A successful probe supplies the recovery evidence and
/// resets the peer, while a failed probe reopens it.
/// </para>
/// <para>
/// <b>Replay protection (spec §8).</b> Every outbound GOSSIP_PING carries a
/// strictly-increasing <c>monotonic_nonce</c> maintained via
/// <see cref="Interlocked.Increment(ref ulong)"/>. Every inbound PING is
/// checked against the per-peer <see cref="PeerInfo.LastSeenNonce"/>; a
/// non-monotonic nonce is dropped (spec "not fatal — recoverable"). The
/// wraparound at <c>ulong.MaxValue</c> is not a real-world concern: at
/// 1000 PINGs/sec/peer (≫ the 30 s tick), the counter would take ~5×10^17
/// seconds — roughly 99 billion years — to wrap.
/// </para>
/// <para>
/// <b>What this daemon ships in Wave 2.5.</b> The round loop completes the
/// signed HELLO handshake against every picked peer, exchanges a
/// signed-payload GOSSIP_PING carrying the current vector-clock snapshot
/// plus the next monotonic nonce, and rate-limits inbound DELTA_STREAM at
/// <see cref="GossipDaemonOptions.MaxDeltaStreamPerSecondPerPeer"/>. CRDT-op
/// apply-back into <c>ICrdtDocument</c> still lands in Wave 2.6 alongside
/// the application-layer integration.
/// </para>
/// </remarks>
public sealed class GossipDaemon : IGossipDaemon
{
    private readonly ISyncDaemonTransport _transport;
    private readonly VectorClock _vectorClock;
    private readonly GossipDaemonOptions _options;
    private readonly ILogger<GossipDaemon> _logger;
    private readonly INodeIdentityProvider _nodeIdentity;
    private readonly IEd25519Signer _signer;
    private readonly DeltaStreamRateLimiter _rateLimiter;
    private readonly Application.IDeltaProducer _deltaProducer;
    private readonly Application.IDeltaSink _deltaSink;
    private readonly IPeerTrustPolicy? _trustPolicy;
    private readonly INetworkTrustState _networkTrust;
    private readonly TimeProvider _timeProvider;
    private readonly Random _random;
    private readonly object _randomLock = new();

    // #1301 F-1 — the OPTIONAL pre-trust enrollment handler. When non-null, the
    // accept loop offers a PRE-TRUST enrollment phase on the SAME network
    // listener (before the trusted HELLO): a connecting peer may send an
    // ENROLL_REQUEST first, gated by the INVITE the payload carries (NOT the
    // session token, NOT roster membership), so a remote not-yet-trusted node can
    // bootstrap mutual trust over the wire. Null preserves the pre-#1301 behavior:
    // an enrollment frame is a protocol violation and the connection is closed —
    // the enrollment surface is exposed ONLY when a host wired a handler.
    private readonly IPreTrustEnrollmentHandler? _enrollmentHandler;

    private readonly IOutboundSequenceAllocator _outboundSequenceAllocator;

    // Monotonic nonce counter. ulong.MaxValue wrap-around is not a
    // real-world concern (see class remarks); we do not guard against it.
    private ulong _pingNonce;

    private readonly ConcurrentDictionary<string, PeerState> _peers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, byte[]>> _peerStateVectors =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private CancellationTokenSource? _runCts;
    private Task? _runLoop;
    private bool _disposed;

    // Outbound bulkheads isolate interactive pushes from reconciliation work.
    // Each lane retains the existing one-at-a-time, one-follow-up coalescing
    // behavior without borrowing capacity from the other lane.
    private readonly PushLane _foregroundPushLane;
    private readonly PushLane _backgroundPushLane;

    // Accept-loop (responder) lifecycle + DoS caps (multi-device INC-5).
    // The accept loop is started by StartListening() — separate from the
    // outbound round loop in StartAsync — so a deployment can run
    // outbound-only (no listener) or both. The semaphore caps total
    // in-flight pre-auth handshakes; _perIpInflight caps a single remote
    // IP's share of that budget.
    private CancellationTokenSource? _listenCts;
    private Task? _listenLoop;
    private SemaphoreSlim? _handshakeConcurrency;
    private readonly ConcurrentDictionary<string, int> _perIpInflight = new(StringComparer.Ordinal);

    /// <summary>
    /// Policy callback that turns a successful CAPABILITY_NEG proposal into
    /// an ACK on the responder/accept path. Defaults to "grant exactly what
    /// the peer proposed" — the same policy the test responder used. Settable
    /// for deployments that need to filter streams. Read on the accept loop.
    /// </summary>
    private readonly Func<CapabilityNegMessage, AckMessage> _capabilityPolicy =
        proposal => new AckMessage(
            proposal.ProposedStreams,
            Array.Empty<Rejection>(),
            proposal.Capabilities
                .Where(capability => capability == SyncCapabilities.DeltaStateVector)
                .ToArray());

    public event EventHandler<GossipRoundCompletedEventArgs>? RoundCompleted;

    /// <inheritdoc />
    public event EventHandler<GossipFrameEventArgs>? FrameReceived;

    public GossipDaemon(
        ISyncDaemonTransport transport,
        VectorClock vectorClock,
        IOptions<GossipDaemonOptions> options,
        INodeIdentityProvider nodeIdentity,
        IEd25519Signer signer,
        Application.IDeltaProducer? deltaProducer = null,
        Application.IDeltaSink? deltaSink = null,
        IPeerTrustPolicy? trustPolicy = null,
        ILogger<GossipDaemon>? logger = null,
        IPreTrustEnrollmentHandler? enrollmentHandler = null,
        TimeProvider? timeProvider = null,
        Random? random = null,
        INetworkTrustState? networkTrust = null,
        IOutboundSequenceAllocator? outboundSequenceAllocator = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _vectorClock = vectorClock ?? throw new ArgumentNullException(nameof(vectorClock));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _nodeIdentity = nodeIdentity ?? throw new ArgumentNullException(nameof(nodeIdentity));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        // Wave 2.5 — null defaults preserve PING-only behavior for callers
        // that haven't migrated to the Application namespace yet (the older
        // 5-arg constructor signature).
        _deltaProducer = deltaProducer ?? new Application.NoopDeltaProducer();
        _deltaSink = deltaSink ?? new Application.NoopDeltaSink();
        // Multi-device INC-5 (#1261 finding #1). A NON-NULL policy is the
        // live trust gate: the initiator passes it to InitiateAsync and the
        // accept loop passes it to RespondAsync, so a same-root peer is
        // trusted and a different-root LAN peer is rejected fail-closed on
        // BOTH live paths. A null policy preserves the pre-INC-5 allow-all
        // behavior (deployments with their own out-of-band trust boundary).
        _trustPolicy = trustPolicy;
        _networkTrust = networkTrust
            ?? new ConfiguredNetworkTrustState(NetworkTrustLevel.Known);
        // #1301 F-1 — optional; null leaves the network listener with NO pre-trust
        // enrollment surface (an enrollment frame closes the connection).
        _enrollmentHandler = enrollmentHandler;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GossipDaemon>.Instance;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _random = random ?? new Random();
        _outboundSequenceAllocator = outboundSequenceAllocator
            ?? new InMemoryOutboundSequenceAllocator();
        _rateLimiter = new DeltaStreamRateLimiter(_options.MaxDeltaStreamPerSecondPerPeer);
        _foregroundPushLane = new PushLane(Math.Max(1, _options.ForegroundPushConcurrency));
        _backgroundPushLane = new PushLane(Math.Max(1, _options.BackgroundPushConcurrency));
    }

    public IReadOnlyCollection<PeerInfo> KnownPeers =>
        _peers.Values.Select(p => p.Info).ToList();

    /// <inheritdoc />
    /// <remarks>
    /// Reads the private <c>_runLoop</c> reference under no lock — the field is
    /// a plain managed reference and the read is a single aligned pointer load,
    /// so the worst-case observable state is "just-transitioned"; that is
    /// acceptable for the health-check consumer, which treats this as an
    /// advisory signal.
    /// </remarks>
    public bool IsRunning => _runLoop is not null;

    /// <summary>
    /// Exposed for tests and observability. Per-peer DELTA_STREAM budget
    /// enforced against inbound frames in this daemon's receive path.
    /// </summary>
    public DeltaStreamRateLimiter RateLimiter => _rateLimiter;

    public void AddPeer(string peerEndpoint, byte[] peerPublicKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerEndpoint);
        ArgumentNullException.ThrowIfNull(peerPublicKey);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _peers[peerEndpoint] = new PeerState(
            new PeerInfo(peerEndpoint, peerPublicKey, DateTimeOffset.MinValue, 0, LastSeenNonce: 0));
    }

    public void RemovePeer(string peerEndpoint)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerEndpoint);
        _peers.TryRemove(peerEndpoint, out _);
    }

    /// <summary>
    /// Validate and record a GOSSIP_PING received from a peer. Returns
    /// <c>true</c> if the nonce is strictly greater than the peer's
    /// <see cref="PeerInfo.LastSeenNonce"/> and was advanced; <c>false</c>
    /// if it was a replay (stale or equal) and the caller should drop it.
    /// </summary>
    /// <remarks>
    /// Exposed as a public entrypoint so receive loops outside the
    /// round-loop (e.g. a dedicated listener) can share the same replay
    /// check. An unknown peer returns <c>false</c> — only membership
    /// advertised via <see cref="AddPeer"/> is subject to replay tracking.
    /// </remarks>
    public bool TryAdvancePeerNonce(string peerEndpoint, ulong incomingNonce)
    {
        if (!_peers.TryGetValue(peerEndpoint, out var peer))
        {
            return false;
        }
        return peer.TryAdvanceNonce(incomingNonce);
    }

    /// <summary>
    /// Rate-limit check for inbound DELTA_STREAM from <paramref name="peerEndpoint"/>.
    /// Returns <c>true</c> if the frame may be processed; <c>false</c> if
    /// the peer exceeded its per-second budget and the frame must be
    /// dropped. Convenience wrapper over <see cref="RateLimiter"/>.
    /// </summary>
    public bool AllowInboundDelta(string peerEndpoint)
    {
        var allowed = _rateLimiter.TryConsume(peerEndpoint);
        if (!allowed)
        {
            _logger.LogWarning(
                "DELTA_STREAM from {Peer} dropped: exceeds {Budget}/s rate limit.",
                peerEndpoint, _options.MaxDeltaStreamPerSecondPerPeer);
        }
        return allowed;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_runLoop is not null) return; // idempotent start
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _runLoop = Task.Run(() => RunLoopAsync(_runCts.Token), ct);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_runLoop is null) return;
            _runCts?.Cancel();
            try
            {
                await _runLoop.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                /* expected during shutdown */
            }
            _runLoop = null;
            _runCts?.Dispose();
            _runCts = null;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Start the inbound responder/accept loop (multi-device INC-5). Loops
    /// <see cref="ISyncDaemonTransport.ListenAsync"/>, runs the signed-HELLO
    /// handshake with the LIVE trust gate (<see cref="HandshakeProtocol.RespondAsync"/>) on each
    /// accepted connection, and then mirrors the gossip round so a delta a
    /// peer pushes is applied. DoS-bounded by
    /// <see cref="GossipDaemonOptions.MaxConcurrentInboundHandshakes"/> /
    /// <see cref="GossipDaemonOptions.MaxConcurrentInboundHandshakesPerIp"/> /
    /// <see cref="GossipDaemonOptions.InboundHandshakeDeadlineSeconds"/>,
    /// all enforced BEFORE any frame is read.
    /// </summary>
    /// <remarks>
    /// Idempotent. A transport with no listen endpoint throws from
    /// <see cref="ISyncDaemonTransport.ListenAsync"/> on the loop's first
    /// iteration; the loop swallows it and ends — an outbound-only transport
    /// simply has no responder side.
    /// </remarks>
    public async Task StartListeningAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_listenLoop is not null) return; // idempotent start
            var cap = Math.Max(1, _options.MaxConcurrentInboundHandshakes);
            _handshakeConcurrency = new SemaphoreSlim(cap, cap);
            _listenCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _listenLoop = Task.Run(() => AcceptLoopAsync(_listenCts.Token), ct);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>Stop the inbound responder/accept loop. Idempotent.</summary>
    public async Task StopListeningAsync(CancellationToken ct)
    {
        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_listenLoop is null) return;
            _listenCts?.Cancel();
            try
            {
                await _listenLoop.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                /* expected during shutdown */
            }
            _listenLoop = null;
            _listenCts?.Dispose();
            _listenCts = null;
            _handshakeConcurrency?.Dispose();
            _handshakeConcurrency = null;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>Whether the inbound responder/accept loop is running.</summary>
    public bool IsListening => _listenLoop is not null;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await StopListeningAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            /* swallow — DisposeAsync should not throw */
        }
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            /* swallow — DisposeAsync should not throw */
        }
        _lifecycleLock.Dispose();
        _foregroundPushLane.Dispose();
        _backgroundPushLane.Dispose();
    }

    // ------------------------------------------------------------------
    // Round loop
    // ------------------------------------------------------------------

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var period = TimeSpan.FromSeconds(Math.Max(1, _options.RoundIntervalSeconds));
        using var timer = new PeriodicTimer(period);

        // Run one round immediately so tests with RoundIntervalSeconds: 1 do
        // not have to wait for the first tick.
        await TriggerPushAsync(OutboundSyncLane.Background, ct).ConfigureAwait(false);

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await TriggerPushAsync(OutboundSyncLane.Background, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            /* expected during shutdown */
        }
    }

    /// <inheritdoc />
    public async Task TriggerPushAsync(OutboundSyncLane lane, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var pushLane = lane switch
        {
            OutboundSyncLane.Foreground => _foregroundPushLane,
            OutboundSyncLane.Background => _backgroundPushLane,
            _ => throw new ArgumentOutOfRangeException(nameof(lane), lane, "Unknown outbound sync lane."),
        };

        // Coalesce a burst: if a push is already running, just mark that another
        // edit landed and let the in-flight push run one more round to carry it.
        // This bounds a flood of rapid local edits to ~2 rounds, not N.
        if (!await pushLane.Lock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            pushLane.QueueFollowUp();
            return;
        }

        try
        {
            // Run the round (and any follow-up the coalescing flag requested).
            // RunOneRoundAsync IS the periodic round — same handshake, same LIVE
            // trust gate, same DoS-capped DELTA_STREAM path. We are only running
            // it NOW instead of on the next tick.
            do
            {
                pushLane.ClearQueuedFollowUp();
                await RunOneRoundAsync(ct).ConfigureAwait(false);
            }
            while (pushLane.TakeQueuedFollowUp() && !ct.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            /* push cancelled (shutdown) — the periodic round is the backstop */
        }
        finally
        {
            pushLane.Lock.Release();
        }
    }

    private async Task RunOneRoundAsync(CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var eligible = _peers.Values
            .Where(p => p.CanAttempt(now))
            .ToList();

        if (eligible.Count == 0)
        {
            // Still emit an empty round event so tests observing RoundCompleted
            // can unblock even when no peers are configured.
            RoundCompleted?.Invoke(this, new GossipRoundCompletedEventArgs(0, 0, 0));
            return;
        }

        var picks = PickRandom(eligible, _options.PeerPickCount);
        var deltasExchanged = 0;
        var opsReceived = 0;

        foreach (var peer in picks)
        {
            if (ct.IsCancellationRequested) break;
            if (!peer.TryBeginAttempt(_timeProvider.GetUtcNow())) continue;
            var outcome = await ExchangeWithPeerAsync(peer, ct).ConfigureAwait(false);
            deltasExchanged += outcome.DeltasExchanged;
            opsReceived += outcome.OpsReceived;
        }

        RoundCompleted?.Invoke(this, new GossipRoundCompletedEventArgs(
            picks.Count, deltasExchanged, opsReceived));
    }

    private List<PeerState> PickRandom(List<PeerState> pool, int count)
    {
        if (count >= pool.Count) return pool.ToList();

        // Fisher–Yates prefix shuffle over the index array — O(count) work.
        var indices = Enumerable.Range(0, pool.Count).ToArray();
        for (var i = 0; i < count; i++)
        {
            int swap;
            lock (_randomLock)
            {
                swap = _random.Next(i, pool.Count);
            }
            (indices[i], indices[swap]) = (indices[swap], indices[i]);
        }
        return indices.Take(count).Select(i => pool[i]).ToList();
    }

    private void OnRoundFailed(PeerState peer)
    {
        double jitter;
        lock (_randomLock)
        {
            jitter = 0.5 + (_random.NextDouble() * 0.5);
        }

        peer.OnRoundFailed(
            _options.DeadPeerBackoffSeconds,
            _timeProvider.GetUtcNow(),
            jitter,
            _options);
    }

    private async Task<(int DeltasExchanged, int OpsReceived)> ExchangeWithPeerAsync(
        PeerState peer,
        CancellationToken roundCt)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(roundCt);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));

        ISyncDaemonConnection? conn = null;
        CancellationTokenSource? deltaDeadlineCts = null;
        var peerNodeId = Convert.ToHexString(peer.Info.PublicKey.AsSpan(
            0, Math.Min(16, peer.Info.PublicKey.Length))).ToLowerInvariant();
        try
        {
            conn = await _transport.ConnectAsync(peer.Info.Endpoint, timeoutCts.Token).ConfigureAwait(false);

            var identity = _nodeIdentity.Current;
            CapabilityResult capabilityResult;

            // The handshake ladder: HELLO (both ways) → CAPABILITY_NEG → ACK.
            try
            {
                capabilityResult = await HandshakeProtocol.InitiateAsync(
                    conn,
                    localIdentity: new LocalIdentity(
                        NodeId: identity.NodeIdBytes,
                        PublicKey: identity.PublicKey,
                        Signer: _signer,
                        PrivateKey: identity.PrivateKey,
                        SchemaVersion: HandshakeProtocol.DefaultSchemaVersion,
                        SupportedVersions: HandshakeProtocol.DefaultSupportedVersions,
                        Capabilities: [SyncCapabilities.DeltaStateVector]),
                    timeoutCts.Token,
                    // Multi-device INC-5: the LIVE initiator trust gate. Was
                    // omitted (allow-all) before this — any LAN peer the
                    // signature check passed was trusted. With a non-null
                    // SharedRootTrustPolicy a different-root peer is rejected.
                    _trustPolicy,
                    _timeProvider).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Signed-HELLO failed (bad signature, incompatible schema,
                // transport dropped mid-handshake). Surface as an observable
                // HandshakeFailure frame before the outer catch reclassifies
                // it as a dead-peer round failure — the outer catch re-raises
                // per-peer backoff, but the notification layer still wants to
                // know the handshake itself failed rather than a generic
                // connect error. Carry the structured ErrorCode where it is
                // cheaply derivable from the failure (read-only classification
                // over the exception message the handshake already produced —
                // no handshake-behavior change) so the sync-status read-model
                // can split the outbound security lane (a peer I dialed
                // rejected me as untrusted) from a benign schema mismatch.
                FrameReceived?.Invoke(this, new GossipFrameEventArgs(
                    PeerEndpoint: peer.Info.Endpoint,
                    PeerNodeId: peerNodeId,
                    FrameType: GossipFrameType.HandshakeFailure,
                    OccurredAt: _timeProvider.GetUtcNow(),
                    Summary: $"handshake with {peerNodeId} failed: {ex.GetType().Name}",
                    ErrorCode: ClassifyHandshakeError(ex)));
                throw;
            }

            // Successful HELLO → Hello frame event.
            FrameReceived?.Invoke(this, new GossipFrameEventArgs(
                PeerEndpoint: peer.Info.Endpoint,
                PeerNodeId: peerNodeId,
                FrameType: GossipFrameType.Hello,
                OccurredAt: _timeProvider.GetUtcNow(),
                Summary: $"{peerNodeId} completed handshake"));

            deltaDeadlineCts = CancellationTokenSource.CreateLinkedTokenSource(roundCt);
            deltaDeadlineCts.CancelAfter(TimeSpan.FromSeconds(
                Math.Max(1, _options.DeltaStreamDeadlineSeconds)));
            var deltaCt = deltaDeadlineCts.Token;

            // Exchange a GOSSIP_PING carrying our vector-clock snapshot and
            // the next monotonic nonce (spec §8). We accept at most one
            // inbound ping per round to bound the loop.
            var carriesStateVectors = capabilityResult.GrantedCapabilities.Contains(
                SyncCapabilities.DeltaStateVector,
                StringComparer.Ordinal);
            var localStateVectors = carriesStateVectors
                ? await GetCurrentStateVectorsAsync(deltaCt).ConfigureAwait(false)
                : EmptyStateVectors;
            var peerStateVectors = carriesStateVectors
                && _peerStateVectors.TryGetValue(peer.Info.Endpoint, out var cachedStateVectors)
                    ? cachedStateVectors
                    : EmptyStateVectors;
            var nonce = Interlocked.Increment(ref _pingNonce);
            var ping = new GossipPingMessage(
                VectorClock: _vectorClock.Snapshot(),
                PeerMembershipDelta: new MembershipDelta(
                    Added: Array.Empty<byte[]>(),
                    Removed: Array.Empty<byte[]>()),
                MonotonicNonce: nonce,
                StateVectors: localStateVectors);
            await conn.SendAsync(ping, deltaCt).ConfigureAwait(false);

            // Wave 2.5 / GAP-1 — outbound DELTA_STREAM(s). Always send at least
            // one frame so the wire protocol stays predictable: every round is
            // PING then one-or-more DELTA_STREAM, regardless of whether a
            // producer has anything to ship. An empty CrdtOps payload is a no-op
            // on the receiver (the sink interprets empty as "nothing to apply").
            // This mirrors the always-PING convention.
            //
            // A negotiated state-vector session uses the last vector received
            // from this peer. First contact, and peers without the capability,
            // deliberately pass empty so the producer encodes full history.
            // C5 — pass the peer's transport pubkey so a participant-scoped (dm:) stream ships only to a participant.
            await SendOutboundDeltasAsync(
                conn,
                deltaCt,
                peer.Info.PublicKey,
                peerStateVectors).ConfigureAwait(false);
            var opsReceivedThisRound = 0;

            var inbound = await conn.ReceiveAsync(deltaCt).ConfigureAwait(false);
            if (inbound is GossipPingMessage inboundPing)
            {
                // Replay-check the inbound nonce before merging state.
                if (!peer.TryAdvanceNonce(inboundPing.MonotonicNonce))
                {
                    _logger.LogWarning(
                        "Dropped replayed GOSSIP_PING from {Peer}: nonce {Nonce} <= last seen {Last}.",
                        peer.Info.Endpoint, inboundPing.MonotonicNonce, peer.Info.LastSeenNonce);
                }
                else
                {
                    if (carriesStateVectors)
                    {
                        _peerStateVectors[peer.Info.Endpoint] = CopyStateVectors(inboundPing.StateVectors);
                    }

                    // Merge the peer's vector clock so we converge on the
                    // newer-op frontier for the next round.
                    var peerClock = new VectorClock(inboundPing.VectorClock);
                    _vectorClock.Merge(peerClock);

                    // Successful inbound PING → GossipPing frame event.
                    FrameReceived?.Invoke(this, new GossipFrameEventArgs(
                        PeerEndpoint: peer.Info.Endpoint,
                        PeerNodeId: peerNodeId,
                        FrameType: GossipFrameType.GossipPing,
                        OccurredAt: _timeProvider.GetUtcNow(),
                        Summary: $"{peerNodeId} sent gossip ping"));

                    // Wave 2.5 / GAP-1 — after the PING comes one DELTA_STREAM
                    // per doctype the peer ships. We read UP TO our own
                    // registered-doctype count of frames (a peer cannot make
                    // us read more frames than we have local doctypes — the
                    // per-id anti-amplification bound), routing each by its
                    // StreamId. A non-fan-out peer ships exactly one frame and
                    // the loop ends on the next receive timeout.
                    opsReceivedThisRound = await ReceiveDeltaStreamsAsync(
                        conn, peer.Info.Endpoint, peerNodeId, deltaCt).ConfigureAwait(false);
                }
            }

            // Success → reset strike count.
            peer.OnRoundSucceeded(_timeProvider.GetUtcNow(), _options);
            return (DeltasExchanged: 1, OpsReceived: opsReceivedThisRound);
        }
        catch (OperationCanceledException) when (
            !roundCt.IsCancellationRequested
            && deltaDeadlineCts?.IsCancellationRequested == true)
        {
            OnRoundFailed(peer);
            _logger.LogWarning(
                "Delta stream deadline exceeded for {Endpoint} after {Seconds}s.",
                peer.Info.Endpoint,
                Math.Max(1, _options.DeltaStreamDeadlineSeconds));
            FrameReceived?.Invoke(this, new GossipFrameEventArgs(
                PeerEndpoint: peer.Info.Endpoint,
                PeerNodeId: peerNodeId,
                FrameType: GossipFrameType.GossipError,
                OccurredAt: _timeProvider.GetUtcNow(),
                Summary: $"delta stream deadline exceeded for {peer.Info.Endpoint}",
                ErrorCode: ErrorCode.DeltaStreamDeadlineExceeded));
            return (0, 0);
        }
        catch (OperationCanceledException) when (!roundCt.IsCancellationRequested)
        {
            OnRoundFailed(peer);
            _logger.LogDebug("Gossip round timed out for {Endpoint}", peer.Info.Endpoint);
            FrameReceived?.Invoke(this, new GossipFrameEventArgs(
                PeerEndpoint: peer.Info.Endpoint,
                PeerNodeId: peerNodeId,
                FrameType: GossipFrameType.GossipError,
                OccurredAt: _timeProvider.GetUtcNow(),
                Summary: $"gossip round timed out for {peer.Info.Endpoint}"));
            return (0, 0);
        }
        catch (Exception ex)
        {
            OnRoundFailed(peer);
            _logger.LogDebug(ex, "Gossip round failed for {Endpoint}", peer.Info.Endpoint);
            // The HandshakeFailure path already raised its own FrameReceived
            // before rethrowing; we still emit a GossipError here so consumers
            // that only track GossipError see every round failure. If the
            // double-emit becomes noisy a future enhancement can gate this
            // on a "did we already raise" flag — today the notification
            // aggregator de-dupes by TeamNotification.Id (random guid), so a
            // double event is two benign notifications, not a state bug.
            FrameReceived?.Invoke(this, new GossipFrameEventArgs(
                PeerEndpoint: peer.Info.Endpoint,
                PeerNodeId: peerNodeId,
                FrameType: GossipFrameType.GossipError,
                OccurredAt: _timeProvider.GetUtcNow(),
                Summary: $"gossip round failed for {peer.Info.Endpoint}: {ex.GetType().Name}"));
            return (0, 0);
        }
        finally
        {
            deltaDeadlineCts?.Dispose();
            if (conn is not null)
            {
                try { await conn.DisposeAsync().ConfigureAwait(false); }
                catch { /* best-effort close */ }
            }
        }
    }

    // ------------------------------------------------------------------
    // GAP-1 — per-document-id wire fan-out (outbound encode/ship + inbound
    // per-id routing). One DELTA_STREAM frame per registered doctype crosses
    // the wire, each tagged with its own StreamId, so a SECOND doctype (comms,
    // and every future one — GL/calendar/stories) converges, not just the
    // first-registered "default" (contacts).
    // ------------------------------------------------------------------

    /// <summary>
    /// The set of document ids this daemon ships outbound this round, and the
    /// upper bound on inbound delta frames it will read.
    /// <para>
    /// When the producer is the install-level <see cref="Application.IDeltaRouter"/>
    /// (the production object graph — the container bridge), this is its
    /// registered doctype set, so EVERY registered doctype crosses the wire. When
    /// it is a bare single-document producer (legacy / test
    /// <see cref="Application.IDeltaProducer"/>, or a Noop), this is exactly the
    /// well-known <c>"default"</c> id — preserving the single-stream Phase-1 wire
    /// shape for back-compat (a peer that only sends/understands "default" still
    /// works).
    /// </para>
    /// <para>
    /// <b>Anti-amplification.</b> The count is bounded by THIS node's own
    /// registry — never by anything the remote peer sends — so a peer can neither
    /// make us ship an unbounded number of outbound frames nor make us read more
    /// inbound frames than we have local doctypes.
    /// </para>
    /// </summary>
    private IReadOnlyList<string> OutboundDocumentIds()
    {
        if (_deltaProducer is Application.IDeltaRouter router)
        {
            var ids = router.RegisteredDocumentIds;
            // Empty registry → fall back to the single well-known stream id so the
            // always-send-one-frame wire convention holds (a Noop round).
            if (ids.Count > 0)
            {
                return ids as IReadOnlyList<string> ?? ids.ToArray();
            }
        }
        return SingleDefaultStreamId;
    }

    private static readonly IReadOnlyList<string> SingleDefaultStreamId =
        new[] { Application.DeltaRoutingRegistry.WellKnownDefaultStreamId };

    private static readonly IReadOnlyDictionary<string, byte[]> EmptyStateVectors =
        new Dictionary<string, byte[]>(StringComparer.Ordinal);

    private async Task<IReadOnlyDictionary<string, byte[]>> GetCurrentStateVectorsAsync(CancellationToken ct)
    {
        if (_deltaProducer is not Application.IDeltaStateVectorProvider provider)
        {
            return EmptyStateVectors;
        }

        var stateVectors = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var documentId in OutboundDocumentIds())
        {
            var stateVector = await provider.GetCurrentStateVectorAsync(documentId, ct).ConfigureAwait(false);
            stateVectors[documentId] = stateVector.ToArray();
        }
        return stateVectors;
    }

    private static IReadOnlyDictionary<string, byte[]> CopyStateVectors(
        IReadOnlyDictionary<string, byte[]> stateVectors) =>
        stateVectors.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.ToArray(),
            StringComparer.Ordinal);

    /// <summary>
    /// Ship one outbound <c>DELTA_STREAM</c> per registered document id, each
    /// tagged with its own <see cref="DeltaStreamMessage.StreamId"/>. Always
    /// sends at least one frame (the well-known <c>"default"</c> id for a
    /// single-document producer) so the wire stays predictable: PING then
    /// one-or-more DELTA_STREAM. An empty CrdtOps payload is a per-id no-op on
    /// the receiver.
    /// </summary>
    private async Task SendOutboundDeltasAsync(
        ISyncDaemonConnection conn,
        CancellationToken ct,
        byte[]? peerPublicKey = null,
        IReadOnlyDictionary<string, byte[]>? peerStateVectors = null)
    {
        // C5 — the participant-scoped routing filter. When the producer is the id-routed delta router AND a stream
        // declared a recipient filter (a dm: conversation: "only its two participants"), SKIP that stream for a peer
        // the filter rejects — so a DM's deltas never ship toward a non-participant (metadata defence-in-depth; the
        // body is already sealed). A stream with no filter (team/contacts/roster) fans out to every peer as before.
        // peerPublicKey is the peer's transport pubkey from the trust-gated HELLO; when it is unknown (a legacy
        // call site) the filter is conservatively SKIPPED-SAFE: we still apply it but pass an empty key, so a
        // dm: filter (which matches a participant's transport key) returns false and the DM is NOT sent — never
        // over-shared to an unidentified peer (fail-closed routing).
        var router = _deltaProducer as Application.IDeltaRouter;
        foreach (var documentId in OutboundDocumentIds())
        {
            if (router is not null)
            {
                var filter = router.GetRecipientFilter(documentId);
                if (filter is not null && !filter(peerPublicKey ?? Array.Empty<byte>()))
                {
                    continue; // this peer is not a recipient of this (participant-scoped) stream — skip it.
                }
            }

            var outboundDelta = await _deltaProducer.EncodeOutboundDeltaAsync(
                documentId,
                peerStateVectors is not null && peerStateVectors.TryGetValue(documentId, out var stateVector)
                    ? stateVector
                    : ReadOnlyMemory<byte>.Empty,
                ct).ConfigureAwait(false);
            var outboundSeq = await _outboundSequenceAllocator.ReserveNextAsync(
                _nodeIdentity.Current.NodeId,
                ct).ConfigureAwait(false);
            _vectorClock.Set(_nodeIdentity.Current.NodeId, outboundSeq);
            await conn.SendAsync(
                new DeltaStreamMessage(
                    StreamId: documentId,
                    OpSequence: outboundSeq,
                    CrdtOps: outboundDelta?.ToArray() ?? Array.Empty<byte>()),
                ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// GAP-1 — read up to <see cref="OutboundDocumentIds"/>.Count inbound
    /// <c>DELTA_STREAM</c> frames (the per-id anti-amplification bound — a peer
    /// cannot make us read more frames than we have local doctypes) and route
    /// EACH by its <see cref="DeltaStreamMessage.StreamId"/> to the registered
    /// sink. Best-effort: a timeout (the peer shipped fewer frames than our
    /// bound), a non-delta frame, a rate-limit rejection, or a malformed payload
    /// ends/skips a frame without faulting the round. Returns the count of
    /// non-empty frames applied (a coarse ops-received stand-in).
    /// <para>
    /// Routing is delegated to the sink: when the sink is the
    /// <see cref="Application.IDeltaRouter"/>, an explicit id match wins, the
    /// well-known <c>"default"</c> id reaches the default doctype (back-compat),
    /// and a genuinely-unknown id is DROPPED — never mis-routed to the default
    /// doctype. Each frame still passes the per-peer rate limiter first.
    /// </para>
    /// </summary>
    private async Task<int> ReceiveDeltaStreamsAsync(
        ISyncDaemonConnection conn,
        string peerEndpoint,
        string peerNodeId,
        CancellationToken ct,
        string inboundSuffix = "")
    {
        var maxFrames = Math.Max(1, OutboundDocumentIds().Count);
        var applied = 0;

        for (var i = 0; i < maxFrames; i++)
        {
            DeltaStreamMessage delta;
            try
            {
                var inbound = await conn.ReceiveAsync(ct).ConfigureAwait(false);
                if (inbound is not DeltaStreamMessage d)
                {
                    // Peer sent something else (or the PING-then-deltas sequence
                    // ended) — stop reading delta frames this round.
                    break;
                }
                delta = d;
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException && ct.IsCancellationRequested)
                {
                    throw;
                }

                // The peer shipped fewer frames than our bound, closed, or hit
                // a transport error. Every frame already received remains
                // applied. Cancellation is rethrown above so shutdown and the
                // named delta-stream deadline remain observable by the caller.
                _logger.LogDebug(ex,
                    "Inbound delta receive from {Peer} ended: {Reason}", peerEndpoint, ex.Message);
                break;
            }

            // Empty payload = peer had nothing to ship for this doctype. The
            // frame is sent unconditionally (per the always-send-one-frame
            // convention) so there's nothing for the sink to apply.
            if (delta.CrdtOps is null || delta.CrdtOps.Length == 0)
            {
                continue;
            }

            // Spec §8 rate-limit check before dispatching to the sink. Counts
            // against the SAME per-peer budget regardless of doctype — the
            // fan-out adds per-id streams over one authenticated/capped channel,
            // it does NOT grant a per-id rate-limit bypass.
            if (!AllowInboundDelta(peerEndpoint))
            {
                continue;
            }

            try
            {
                // Route by the frame's own StreamId — the router resolves it to
                // the owning doctype's sink (or DROPS an unknown id). A bare
                // single-document sink ignores the id and applies to its one doc.
                await _deltaSink.ApplyInboundDeltaAsync(
                    delta.StreamId,
                    delta.OpSequence,
                    delta.CrdtOps,
                    ct).ConfigureAwait(false);

                FrameReceived?.Invoke(this, new GossipFrameEventArgs(
                    PeerEndpoint: peerEndpoint,
                    PeerNodeId: peerNodeId,
                    FrameType: GossipFrameType.DeltaStream,
                    OccurredAt: _timeProvider.GetUtcNow(),
                    Summary: $"{peerNodeId} sent delta-stream {delta.StreamId} op {delta.OpSequence}{inboundSuffix}"));

                // Reporting "1 op per non-empty frame" is a coarse stand-in for
                // the actual CRDT op count — the wire payload is opaque bytes
                // whose internal op-count semantics belong to the engine.
                applied++;
            }
            catch (Exception ex)
            {
                // Per IDeltaSink contract: malformed payloads are recoverable.
                // Log and drop the frame rather than aborting the round (which
                // would over-penalize a peer for a single bad frame). Keep
                // reading the remaining doctype frames this round.
                _logger.LogWarning(ex,
                    "Inbound DELTA_STREAM {Stream} from {Peer} rejected: {Reason}",
                    delta.StreamId, peerEndpoint, ex.Message);
            }
        }

        return applied;
    }

    // ------------------------------------------------------------------
    // Responder / accept loop (multi-device INC-5) — the LIVE inbound side
    // ------------------------------------------------------------------

    /// <summary>
    /// Accept inbound connections and run the trust-gated handshake on each.
    /// DoS caps are applied in this exact order, ALL before the handshake
    /// reads a single (up to 16 MiB) frame:
    /// <list type="number">
    ///   <item>per-IP in-flight cap — a single host cannot grab more than its
    ///     share of the budget;</item>
    ///   <item>global concurrency cap — a flood across many IPs cannot exceed
    ///     the total in-flight pre-auth session budget;</item>
    ///   <item>per-handshake deadline — a slow peer that holds a slot is timed
    ///     out, freeing it.</item>
    /// </list>
    /// A connection rejected by (1) or (2) is closed immediately, so a flood
    /// can never force unbounded concurrent 16 MiB allocations.
    /// </summary>
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        IAsyncEnumerator<ISyncDaemonConnection>? enumerator = null;
        try
        {
            enumerator = _transport.ListenAsync(ct).GetAsyncEnumerator(ct);
        }
        catch (Exception ex)
        {
            // An outbound-only transport throws here — no responder side. Log
            // once at debug and end the loop cleanly.
            _logger.LogDebug(ex, "Accept loop not started: transport has no listener.");
            return;
        }

        try
        {
            while (true)
            {
                ISyncDaemonConnection conn;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
                    conn = enumerator.Current;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Accept loop iteration faulted; continuing.");
                    continue;
                }

                // --- DoS cap (1): per-IP in-flight cap. Bounds one host's
                //     share BEFORE we even take a global slot or read a frame.
                var remoteIp = ExtractRemoteIp(conn.RemoteEndpoint);
                if (!TryAcquirePerIp(remoteIp))
                {
                    _logger.LogWarning(
                        "Inbound connection from {Ip} rejected: per-IP in-flight cap {Cap} reached.",
                        remoteIp, Math.Max(1, _options.MaxConcurrentInboundHandshakesPerIp));
                    await SafeCloseAsync(conn).ConfigureAwait(false);
                    continue;
                }

                // --- DoS cap (2): global concurrency cap. A connection that
                //     arrives while saturated is closed immediately (no wait,
                //     no frame read) so a flood is bounded, not queued. Capture
                //     the semaphore instance so the handler releases the SAME
                //     one even if a stop/start cycle swapped the field.
                var concurrency = _handshakeConcurrency;
                if (concurrency is null || !concurrency.Wait(0, CancellationToken.None))
                {
                    ReleasePerIp(remoteIp);
                    _logger.LogWarning(
                        "Inbound connection from {Ip} rejected: global handshake concurrency cap {Cap} reached.",
                        remoteIp, Math.Max(1, _options.MaxConcurrentInboundHandshakes));
                    await SafeCloseAsync(conn).ConfigureAwait(false);
                    continue;
                }

                // Fire-and-forget the bounded handshake; the loop keeps
                // accepting. Each branch releases BOTH the per-IP and the
                // captured global slot exactly once via the handler's finally.
                _ = Task.Run(() => HandleInboundAsync(conn, remoteIp, concurrency, ct), ct);
            }
        }
        finally
        {
            if (enumerator is not null)
            {
                try { await enumerator.DisposeAsync().ConfigureAwait(false); }
                catch { /* best-effort */ }
            }
        }
    }

    /// <summary>
    /// Run the trust-gated handshake + a mirrored gossip round on a single
    /// accepted connection, under the per-handshake deadline (DoS cap 3).
    /// Always releases the global + per-IP concurrency slots.
    /// </summary>
    private async Task HandleInboundAsync(
        ISyncDaemonConnection conn, string remoteIp, SemaphoreSlim concurrency, CancellationToken loopCt)
    {
        // --- DoS cap (3): per-handshake read-deadline. A peer that does not
        //     finish the handshake within the window has its connection torn
        //     down, freeing the slot it holds.
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(loopCt);
        deadlineCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.InboundHandshakeDeadlineSeconds)));
        var ct = deadlineCts.Token;
        var peerNodeId = string.Empty;
        try
        {
            var identity = _nodeIdentity.Current;
            var local = new LocalIdentity(
                NodeId: identity.NodeIdBytes,
                PublicKey: identity.PublicKey,
                Signer: _signer,
                PrivateKey: identity.PrivateKey,
                SchemaVersion: HandshakeProtocol.DefaultSchemaVersion,
                SupportedVersions: HandshakeProtocol.DefaultSupportedVersions,
                Capabilities: [SyncCapabilities.DeltaStateVector]);

            // #1301 F-1 — peek the FIRST frame to discriminate a PRE-TRUST
            // ENROLLMENT request from a trusted HELLO. The accept loop reads the
            // first frame itself; the trusted-session ladder (RespondToHelloAsync)
            // then continues from the already-read HELLO so the first frame is not
            // double-read. This is the ONE network listener carrying BOTH the
            // pre-trust enrollment phase and the trusted HELLO — one port (7473),
            // not a second listener.
            var first = await conn.ReceiveAsync(ct).ConfigureAwait(false);

            if (first is EnrollRequestMessage enrollRequest)
            {
                // A connecting peer opened with an enrollment request. Handle it
                // ONLY through the host-wired pre-trust handler, gated by the
                // INVITE in the payload (NOT the trusted-session gate, NOT roster
                // membership) — then close. The trusted HELLO comes on a SUBSEQUENT
                // connection once the joiner has adopted. If no handler is wired
                // the network listener exposes NO enrollment surface: close the
                // connection fail-closed (an enrollment frame is a protocol
                // violation for a sync-only deployment).
                await HandlePreTrustEnrollmentAsync(conn, enrollRequest, remoteIp, ct).ConfigureAwait(false);
                return;
            }

            if (first is not HelloMessage peerHello)
            {
                // Neither an enrollment request nor a HELLO — a malformed opener.
                // Close fail-closed (the responder ladder would have thrown anyway).
                _logger.LogDebug(
                    "Inbound connection from {Ip} opened with {Frame}; expected HELLO or ENROLL_REQUEST — closing.",
                    remoteIp, first.GetType().Name);
                return;
            }

            peerNodeId = Convert.ToHexString(peerHello.PublicKey.AsSpan(
                0, Math.Min(16, peerHello.PublicKey.Length))).ToLowerInvariant();

            // The LIVE responder trust gate. A non-null _trustPolicy rejects a
            // different-root initiator (fail-closed) before any session state
            // is committed; signature/replay/schema are screened inside
            // RespondToHelloAsync as on the initiator path. The already-read HELLO
            // is handed in so the ladder does not double-read the first frame.
            var result = await HandshakeProtocol.RespondToHelloAsync(
                conn, local, peerHello, _capabilityPolicy, ct, _trustPolicy, _timeProvider).ConfigureAwait(false);

            peerNodeId = Convert.ToHexString(result.PeerPublicKey.AsSpan(
                0, Math.Min(16, result.PeerPublicKey.Length))).ToLowerInvariant();

            FrameReceived?.Invoke(this, new GossipFrameEventArgs(
                PeerEndpoint: conn.RemoteEndpoint,
                PeerNodeId: peerNodeId,
                FrameType: GossipFrameType.Hello,
                OccurredAt: _timeProvider.GetUtcNow(),
                Summary: $"{peerNodeId} completed inbound handshake"));

            using var deltaDeadlineCts = CancellationTokenSource.CreateLinkedTokenSource(loopCt);
            deltaDeadlineCts.CancelAfter(TimeSpan.FromSeconds(
                Math.Max(1, _options.DeltaStreamDeadlineSeconds)));

            // Mirror the gossip round from the responder's perspective: read
            // the initiator's PING + DELTA_STREAM, apply the delta through the
            // sink (so a peer's contact edit reaches our store), then send our
            // own PING + outbound DELTA_STREAM so the exchange is symmetric.
            // C5 — pass the verified peer transport pubkey so a participant-scoped (dm:) stream is filtered here too.
            try
            {
                await ServeInboundRoundAsync(
                    conn,
                    conn.RemoteEndpoint,
                    peerNodeId,
                    deltaDeadlineCts.Token,
                    result.PeerPublicKey,
                    result.GrantedCapabilities.Contains(
                        SyncCapabilities.DeltaStateVector,
                        StringComparer.Ordinal)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                !loopCt.IsCancellationRequested && deltaDeadlineCts.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Inbound delta stream deadline exceeded for {Endpoint} after {Seconds}s.",
                    conn.RemoteEndpoint,
                    Math.Max(1, _options.DeltaStreamDeadlineSeconds));
                FrameReceived?.Invoke(this, new GossipFrameEventArgs(
                    PeerEndpoint: conn.RemoteEndpoint,
                    PeerNodeId: peerNodeId,
                    FrameType: GossipFrameType.GossipError,
                    OccurredAt: _timeProvider.GetUtcNow(),
                    Summary: $"delta stream deadline exceeded for {conn.RemoteEndpoint}",
                    ErrorCode: ErrorCode.DeltaStreamDeadlineExceeded));
            }
        }
        catch (OperationCanceledException)
        {
            // Deadline hit or shutdown — the slow/incomplete peer is dropped.
            _logger.LogDebug("Inbound handshake from {Ip} cancelled (deadline or shutdown).", remoteIp);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("untrusted", StringComparison.OrdinalIgnoreCase))
        {
            // The LIVE responder trust gate rejected a different-root peer.
            // This is the SECURITY lane — "an untrusted device tried to join
            // your fleet and was blocked." Carry the structured
            // ErrorCode.PeerUntrusted on the frame so the sync-status
            // read-model can surface it DISTINCTLY from a benign schema /
            // signature handshake failure (survey §State 4 guardrail G3; the
            // frame type stays HandshakeFailure for back-compat with the
            // notification stream's severity mapping, the ErrorCode is the new
            // discriminator). The fail-closed reject already happened inside
            // RespondAsync; this is purely the observable signal.
            _logger.LogWarning("Inbound handshake from {Ip} rejected by trust gate: {Reason}", remoteIp, ex.Message);
            FrameReceived?.Invoke(this, new GossipFrameEventArgs(
                PeerEndpoint: conn.RemoteEndpoint,
                PeerNodeId: peerNodeId,
                FrameType: GossipFrameType.HandshakeFailure,
                OccurredAt: _timeProvider.GetUtcNow(),
                Summary: $"inbound peer {remoteIp} rejected: untrusted",
                ErrorCode: ErrorCode.PeerUntrusted));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Inbound handshake from {Ip} failed: {Reason}", remoteIp, ex.Message);
        }
        finally
        {
            // Release the captured semaphore (not the field — it may have been
            // swapped/disposed by a stop/start). Guard against a race with
            // StopListeningAsync disposing it mid-shutdown.
            try { concurrency.Release(); }
            catch (ObjectDisposedException) { /* listener stopped; slot is moot */ }
            ReleasePerIp(remoteIp);
            await SafeCloseAsync(conn).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// #1301 F-1 — the PRE-TRUST ENROLLMENT phase on the network sync listener. A connecting peer opened with an
    /// <see cref="EnrollRequestMessage"/> instead of a HELLO; hand its OPAQUE payload to the host-wired
    /// <see cref="IPreTrustEnrollmentHandler"/> (gated by the INVITE the payload carries — NOT the trusted-session
    /// gate, NOT roster membership), then write back the OPAQUE response and close. The trusted HELLO comes on a
    /// SUBSEQUENT connection once the joiner has adopted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Narrow + fail-closed + no-leak.</b> This is the ONLY thing the enrollment phase does. With NO handler
    /// wired the network listener exposes no enrollment surface — the connection is closed without a reply (an
    /// enrollment frame is a protocol violation for a sync-only deployment). With a handler, ANY failure (invalid
    /// / missing / expired / replayed invite, or a handler throw) yields a bare fail-closed
    /// <see cref="EnrollResponseMessage"/> reject (empty payload, opaque reason) so an invalid-invite caller
    /// learns nothing about the roster / genesis. The handler runs under the SAME per-handshake deadline
    /// (<paramref name="ct"/>) + the per-IP / global concurrency caps already taken in the accept loop, so the
    /// enrollment phase is DoS-bounded exactly like a HELLO. The trusted-session gate is untouched.
    /// </para>
    /// </remarks>
    private async Task HandlePreTrustEnrollmentAsync(
        ISyncDaemonConnection conn, EnrollRequestMessage request, string remoteIp, CancellationToken ct)
    {
        if (_networkTrust.Current is not NetworkTrustLevel.Known)
        {
            _logger.LogDebug(
                "Inbound enrollment request from {Ip} rejected because the current network is unknown — closing.",
                remoteIp);
            return;
        }

        if (_enrollmentHandler is null)
        {
            // No host wired an enrollment handler — the network listener exposes NO pre-trust enrollment surface.
            // Close fail-closed; do not reply (a stranger learns only that the connection dropped).
            _logger.LogDebug(
                "Inbound enrollment request from {Ip} but no pre-trust enrollment handler is wired — closing.",
                remoteIp);
            return;
        }

        EnrollResponseMessage response;
        try
        {
            var outcome = await _enrollmentHandler
                .HandleAsync(request.Payload ?? Array.Empty<byte>(), remoteIp, ct)
                .ConfigureAwait(false);
            response = outcome.Accepted
                ? new EnrollResponseMessage(Accepted: true, Payload: outcome.ResponsePayload, RejectReason: null)
                : new EnrollResponseMessage(Accepted: false, Payload: Array.Empty<byte>(),
                    RejectReason: outcome.RejectReason ?? "enrollment_rejected");

            FrameReceived?.Invoke(this, new GossipFrameEventArgs(
                PeerEndpoint: conn.RemoteEndpoint,
                PeerNodeId: string.Empty,
                FrameType: outcome.Accepted ? GossipFrameType.Hello : GossipFrameType.HandshakeFailure,
                OccurredAt: _timeProvider.GetUtcNow(),
                Summary: outcome.Accepted
                    ? $"pre-trust enrollment from {remoteIp} ACCEPTED (invite-gated)"
                    : $"pre-trust enrollment from {remoteIp} rejected: {outcome.RejectReason ?? "invalid_invite"}",
                ErrorCode: outcome.Accepted ? null : ErrorCode.PeerUntrusted));
        }
        catch (OperationCanceledException)
        {
            // Deadline / shutdown — drop without a reply (the slot frees in the caller's finally).
            _logger.LogDebug("Pre-trust enrollment from {Ip} cancelled (deadline or shutdown).", remoteIp);
            return;
        }
        catch (Exception ex)
        {
            // An UNEXPECTED handler fault is still treated as a fail-closed reject — never leak detail or a stack
            // to the wire. The handler is contracted not to throw for an expected reject; this is the backstop.
            _logger.LogWarning(ex, "Pre-trust enrollment handler faulted for {Ip}; rejecting fail-closed.", remoteIp);
            response = new EnrollResponseMessage(Accepted: false, Payload: Array.Empty<byte>(),
                RejectReason: "enrollment_error");
        }

        try
        {
            await conn.SendAsync(response, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort write-back: the peer may have hung up. Nothing committed on our side beyond the admit
            // the handler already performed (which is idempotent-by-single-use-token), so a dropped reply just
            // means the joiner retries with a fresh invite.
            _logger.LogDebug(ex, "Pre-trust enrollment reply to {Ip} could not be sent.", remoteIp);
        }
    }

    /// <summary>
    /// Responder half of a gossip round: read the initiator's PING (+ optional
    /// DELTA_STREAM, applied through the sink), then send our own PING +
    /// outbound DELTA_STREAM. Best-effort — a missing frame ends the round
    /// without faulting the connection.
    /// </summary>
    private async Task ServeInboundRoundAsync(
        ISyncDaemonConnection conn, string peerEndpoint, string peerNodeId, CancellationToken ct,
        byte[]? peerPublicKey = null, bool carriesStateVectors = false)
    {
        var inbound = await conn.ReceiveAsync(ct).ConfigureAwait(false);
        if (inbound is not GossipPingMessage inboundPing)
        {
            return; // peer did not open with a PING — nothing further to mirror
        }

        FrameReceived?.Invoke(this, new GossipFrameEventArgs(
            PeerEndpoint: peerEndpoint,
            PeerNodeId: peerNodeId,
            FrameType: GossipFrameType.GossipPing,
            OccurredAt: _timeProvider.GetUtcNow(),
            Summary: $"{peerNodeId} sent gossip ping (inbound)"));

        // GAP-1 — the initiator follows its PING with one DELTA_STREAM per
        // doctype. Read UP TO our own registered-doctype count of frames (the
        // per-id anti-amplification bound — a peer cannot make us read more
        // frames than we have local doctypes) and route each by its StreamId.
        await ReceiveDeltaStreamsAsync(conn, peerEndpoint, peerNodeId, ct,
            inboundSuffix: " (inbound)").ConfigureAwait(false);

        // Reply with our own PING + one outbound DELTA_STREAM per doctype so the
        // exchange is symmetric and the initiator converges on our frontier too.
        var localStateVectors = carriesStateVectors
            ? await GetCurrentStateVectorsAsync(ct).ConfigureAwait(false)
            : EmptyStateVectors;
        var nonce = Interlocked.Increment(ref _pingNonce);
        await conn.SendAsync(
            new GossipPingMessage(
                _vectorClock.Snapshot(),
                new MembershipDelta(Array.Empty<byte[]>(), Array.Empty<byte[]>()),
                MonotonicNonce: nonce,
                StateVectors: localStateVectors),
            ct).ConfigureAwait(false);

        // C5 — the responder also filters participant-scoped (dm:) streams to the verified peer.
        await SendOutboundDeltasAsync(
            conn,
            ct,
            peerPublicKey,
            carriesStateVectors ? inboundPing.StateVectors : EmptyStateVectors).ConfigureAwait(false);
    }

    // --- DoS-cap helpers --------------------------------------------------

    /// <summary>Reserve a per-IP in-flight slot; false if the per-IP cap is hit.</summary>
    private bool TryAcquirePerIp(string ip)
    {
        var cap = Math.Max(1, _options.MaxConcurrentInboundHandshakesPerIp);
        while (true)
        {
            var current = _perIpInflight.GetValueOrDefault(ip, 0);
            if (current >= cap) return false;
            if (current == 0)
            {
                if (_perIpInflight.TryAdd(ip, 1)) return true;
            }
            else if (_perIpInflight.TryUpdate(ip, current + 1, current))
            {
                return true;
            }
            // lost a race — retry with a fresh read
        }
    }

    /// <summary>Release a per-IP in-flight slot; removes the key at zero.</summary>
    private void ReleasePerIp(string ip)
    {
        while (true)
        {
            if (!_perIpInflight.TryGetValue(ip, out var current)) return;
            if (current <= 1)
            {
                // Remove only if still at the value we observed — avoids
                // dropping a slot a concurrent acquire just took.
                if (((System.Collections.Generic.ICollection<KeyValuePair<string, int>>)_perIpInflight)
                        .Remove(new KeyValuePair<string, int>(ip, current)))
                {
                    return;
                }
            }
            else if (_perIpInflight.TryUpdate(ip, current - 1, current))
            {
                return;
            }
            // lost a race — retry
        }
    }

    /// <summary>
    /// Extract a stable per-IP key from a transport endpoint string
    /// (<c>tcp://host:port</c>, bare <c>host:port</c>, or a socket path). The
    /// port is intentionally dropped so all connections from one host share a
    /// per-IP budget. A path-style endpoint (UDS) collapses to a constant key
    /// (same-machine, no per-IP threat) so the cap still composes.
    /// </summary>
    private static string ExtractRemoteIp(string endpoint)
    {
        if (string.IsNullOrEmpty(endpoint)) return "unknown";
        var s = endpoint;
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        // Not host:port (e.g. a unix socket path) — one shared local key.
        if (s.StartsWith('/', StringComparison.Ordinal)) return "local-uds";
        // IPv6 literal [::1]:port
        if (s.StartsWith('[', StringComparison.Ordinal))
        {
            var close = s.IndexOf(']', StringComparison.Ordinal);
            return close > 0 ? s[1..close] : s;
        }
        var colon = s.LastIndexOf(':', StringComparison.Ordinal);
        return colon > 0 ? s[..colon] : s;
    }

    private async Task SafeCloseAsync(ISyncDaemonConnection conn)
    {
        try { await conn.DisposeAsync().ConfigureAwait(false); }
        catch { /* best-effort close */ }
    }

    /// <summary>
    /// The handshake protocol's canonical reject messages, mapped to the
    /// structured <see cref="ErrorCode"/> the responder ALSO sends as a wire
    /// <c>ErrorMessage</c> alongside each reject. Used by
    /// <see cref="ClassifyHandshakeError"/> for read-only classification of an
    /// outbound handshake failure into the same enum the INBOUND lane keys on.
    /// <para>
    /// <b>S2.</b> This replaces fuzzy <c>message.Contains(...)</c> substring
    /// matching — which could mis-flag a transport/OS fault (or a roster peer)
    /// whose message coincidentally contained a token — with EXACT-equality on
    /// the protocol's own deterministic reject strings. The reject strings are a
    /// closed set the protocol owns
    /// (<see cref="HandshakeProtocol"/>'s <c>throw new InvalidOperationException(...)</c>
    /// rejection sites); a transport drop / connect fault is not in the table and
    /// classifies as <c>null</c> (frame-type-only), so a benign fault can never
    /// be mis-flagged as the security lane.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, ErrorCode> HandshakeRejectCodes =
        new Dictionary<string, ErrorCode>(StringComparer.Ordinal)
        {
            ["Peer untrusted; session closed."] = ErrorCode.PeerUntrusted,
            ["HELLO signature invalid; session closed."] = ErrorCode.HelloSignatureInvalid,
            ["Schema-version mismatch; session closed."] = ErrorCode.SchemaVersionIncompatible,
            ["HELLO replay window exceeded; session closed."] = ErrorCode.HelloTimestampStale,
        };

    /// <summary>
    /// Read-only classification of an OUTBOUND handshake exception into a
    /// structured <see cref="ErrorCode"/> for the observable
    /// <see cref="GossipFrameEventArgs"/> (sync-status read-model, Phase A).
    /// <para>
    /// The handshake protocol throws an <see cref="InvalidOperationException"/>
    /// with a fixed canonical message per rejection reason and ALSO sends the
    /// matching structured <c>ErrorMessage</c> code on the wire. We map the
    /// exception back to that SAME code WITHOUT changing any handshake behavior —
    /// the rejection and fail-close already happened inside the protocol.
    /// </para>
    /// <para>
    /// <b>S2 hardening:</b> classification is structured, not fuzzy. We require
    /// the handshake's own reject exception type (<see cref="InvalidOperationException"/>
    /// — the type the protocol throws for a determinate reject) AND an
    /// exact-equality match against the protocol's canonical reject strings
    /// (<see cref="HandshakeRejectCodes"/>). A transport/connect fault
    /// (<c>SocketException</c>, <c>IOException</c>, timeout) is a DIFFERENT
    /// exception type / not in the table → <c>null</c>, so a benign fault — or a
    /// roster peer the local node dialed — is never mis-classified into the
    /// security lane.
    /// </para>
    /// </summary>
    internal static ErrorCode? ClassifyHandshakeError(Exception ex)
    {
        // Only the handshake's own determinate reject is classified. Transport /
        // connect / timeout faults are other exception types and stay null
        // (frame-type-only) — this is what stops a benign fault flagging the
        // security lane.
        if (ex is not InvalidOperationException)
        {
            return null;
        }

        return HandshakeRejectCodes.TryGetValue(ex.Message, out var code) ? code : null;
    }

    // ------------------------------------------------------------------
    // Per-peer state — owns backoff bookkeeping + last-seen nonce
    // ------------------------------------------------------------------

    private sealed class PushLane : IDisposable
    {
        private int _followUpQueued;

        public PushLane(int concurrency)
        {
            Lock = new SemaphoreSlim(concurrency, concurrency);
        }

        public SemaphoreSlim Lock { get; }

        public void QueueFollowUp() => Interlocked.Exchange(ref _followUpQueued, 1);

        public void ClearQueuedFollowUp() => Interlocked.Exchange(ref _followUpQueued, 0);

        public bool TakeQueuedFollowUp() => Interlocked.Exchange(ref _followUpQueued, 0) == 1;

        public void Dispose() => Lock.Dispose();
    }

    private sealed class PeerState
    {
        private readonly object _nonceLock = new();
        private readonly object _retryLock = new();
        private readonly Queue<(DateTimeOffset OccurredAt, bool Failed)> _outcomes = new();
        private int _strikes;

        public PeerInfo Info { get; private set; }
        public DateTimeOffset SkipUntil { get; private set; } = DateTimeOffset.MinValue;

        public PeerState(PeerInfo info)
        {
            Info = info;
        }

        public bool CanAttempt(DateTimeOffset now)
        {
            lock (_retryLock)
            {
                return SkipUntil <= now && Info.CircuitState != PeerCircuitState.HalfOpen;
            }
        }

        public bool TryBeginAttempt(DateTimeOffset now)
        {
            lock (_retryLock)
            {
                if (SkipUntil > now || Info.CircuitState == PeerCircuitState.HalfOpen)
                {
                    return false;
                }

                if (Info.CircuitState == PeerCircuitState.Open)
                {
                    Info = Info with { CircuitState = PeerCircuitState.HalfOpen };
                }

                return true;
            }
        }

        public void OnRoundSucceeded(DateTimeOffset now, GossipDaemonOptions options)
        {
            lock (_retryLock)
            {
                if (Info.CircuitState == PeerCircuitState.HalfOpen)
                {
                    _outcomes.Clear();
                }
                RecordOutcome(now, failed: false, options);
                _strikes = 0;
                SkipUntil = DateTimeOffset.MinValue;
                // Mirror the cleared backoff bookkeeping onto the public PeerInfo
                // so the sync-status read-model sees "reached, nothing failing"
                // (Phase A — survey §State 3). ConsecutiveFailures back to 0,
                // BackoffUntil cleared.
                Info = Info with
                {
                    LastSeenAt = now,
                    ConsecutiveFailures = 0,
                    BackoffUntil = null,
                    CircuitState = PeerCircuitState.Closed,
                };
            }
        }

        public void OnRoundFailed(
            int baseBackoffSeconds,
            DateTimeOffset now,
            double jitter,
            GossipDaemonOptions options)
        {
            lock (_retryLock)
            {
                var probeFailed = Info.CircuitState == PeerCircuitState.HalfOpen;
                RecordOutcome(now, failed: true, options);
                _strikes = Math.Min(_strikes + 1, 3); // cap doubling at 2^3 = 8× ... then we clamp below.
                var multiplier = Math.Min(1 << (_strikes - 1), 4); // 1,2,4,4…
                var ceilingSeconds = Math.Max(0, baseBackoffSeconds) * multiplier;
                SkipUntil = now.AddSeconds(ceilingSeconds * jitter);
                // Mirror the strike count + cool-off deadline onto the public
                // PeerInfo so the sync-status read-model can derive SHOULD-waiting
                // (peer unreachable, will retry) without reaching into PeerState
                // internals (Phase A — survey §State 3 "needs-exposing").
                Info = Info with
                {
                    ConsecutiveFailures = _strikes,
                    BackoffUntil = SkipUntil,
                    CircuitState = probeFailed || ShouldOpenCircuit(options)
                        ? PeerCircuitState.Open
                        : Info.CircuitState,
                };
            }
        }

        private void RecordOutcome(
            DateTimeOffset now,
            bool failed,
            GossipDaemonOptions options)
        {
            var window = TimeSpan.FromSeconds(Math.Max(
                1,
                options.CircuitBreakerSamplingWindowSeconds));
            var cutoff = now - window;
            while (_outcomes.TryPeek(out var outcome) && outcome.OccurredAt < cutoff)
            {
                _outcomes.Dequeue();
            }

            _outcomes.Enqueue((now, failed));
        }

        private bool ShouldOpenCircuit(GossipDaemonOptions options)
        {
            var minimumThroughput = Math.Max(1, options.CircuitBreakerMinimumThroughput);
            if (_outcomes.Count < minimumThroughput)
            {
                return false;
            }

            var failureRatio = (double)_outcomes.Count(outcome => outcome.Failed) / _outcomes.Count;
            return failureRatio >= Math.Clamp(options.CircuitBreakerFailureRatio, 0, 1);
        }

        /// <summary>
        /// Replay-protection gate: accept <paramref name="incomingNonce"/>
        /// only if it is strictly greater than the previously seen nonce.
        /// </summary>
        public bool TryAdvanceNonce(ulong incomingNonce)
        {
            lock (_nonceLock)
            {
                if (incomingNonce <= Info.LastSeenNonce)
                {
                    return false;
                }
                Info = Info with { LastSeenNonce = incomingNonce };
                return true;
            }
        }
    }
}
