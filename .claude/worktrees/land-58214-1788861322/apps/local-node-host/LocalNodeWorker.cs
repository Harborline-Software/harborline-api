using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Sync.Discovery;
using Harborline.Api.Kernel.Sync.Gossip;
using Harborline.Api.Kernel.Sync.Handshake;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Protocol;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost;

/// <summary>
/// Long-running hosted service that boots the Harborline local-node kernel on
/// startup and unwinds it on application shutdown. The kernel itself drives
/// all per-tick work (event-bus dispatch, sync daemon, projection scheduler
/// in later waves) — this worker's job is purely lifecycle coordination,
/// so once start-up completes it parks on <see cref="Task.Delay(int, CancellationToken)"/>
/// until cancellation.
/// </summary>
/// <remarks>
/// <para>
/// Paper §4 calls for the container stack to run as a "persistent background
/// service registered with the OS service manager." The .NET generic host is
/// the stepping stone: we run exactly the same executable under
/// <c>dotnet run</c> during development and (Wave 4) under systemd / launchd /
/// Windows Service in production. No host-code branching is required; the
/// service-manager integration is packaging, not code.
/// </para>
/// <para>
/// Wave 6.3.E.2: gossip is now team-scoped — it lives in the active team
/// context's <see cref="TeamContext.Services"/> rather than on the install-
/// level service provider. The worker resolves it from
/// <see cref="IActiveTeamAccessor.Active"/> at start-up; if no team has been
/// materialized (or activation failed), the worker logs a warning and proceeds
/// without gossip so the host remains useful for plugin lifecycle.
/// </para>
/// <para>
/// <b>DAEMON-REBIND on team-switch (cerebrum [2026-06-21] gap #3).</b> The node
/// lifecycle originally assumed a FIXED active team at boot — it bound the gossip
/// daemon ONCE from the boot active-team context with no rebind. That broke the
/// B-side of wire enrollment: when a node JOINS a peer team (switching its active
/// team to the admitter's team via <see cref="NodeEnrollmentJoinService"/>), the
/// daemon kept presenting the OLD team's HELLO key, so the trusted handshake with
/// the admitter failed <c>PEER_UNTRUSTED</c> from the joiner's side. This worker
/// now subscribes to <see cref="IActiveTeamAccessor.ActiveChanged"/> and, on a
/// genuine team switch, STOPS the prior team's daemon (round + listen + push-on-
/// change + discovery) and STARTS the now-active team's daemon — which the per-team
/// child container has already keyed to the new team (HKDF(node-root, newTeamId)).
/// The rebound daemon presents the new team's HELLO key, so the handshake passes.
/// A node that never switches teams (the single-user default) never enters the
/// rebind path — its daemon stays bound to its genesis team exactly as before.
/// </para>
/// <para>
/// The idle loop uses <see cref="Timeout.Infinite"/> rather than a polling
/// <c>while</c> loop so the process burns no CPU at rest. Cancellation from
/// either <see cref="IHostApplicationLifetime.ApplicationStopping"/> or an
/// external signal propagates via <paramref name="stoppingToken"/>, the
/// <see cref="Task.Delay(int, CancellationToken)"/> throws
/// <see cref="OperationCanceledException"/>, and shutdown proceeds deterministically.
/// </para>
/// </remarks>
public sealed class LocalNodeWorker : BackgroundService, Harborline.Api.LocalNodeHost.Health.IEnrollmentRebindStatus
{
    private readonly INodeHost _nodeHost;
    private readonly IPluginRegistry _pluginRegistry;
    private readonly IEnumerable<ILocalNodePlugin> _plugins;
    private readonly ITeamContextFactory _teamContextFactory;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly LocalNodeOptions _options;
    private readonly IPeerDiscovery? _peerDiscovery;
    private readonly ICrdtProjectionRegistry? _crdtProjectionRegistry;
    private readonly NodeTeamRoster? _teamRoster;
    private readonly ILogger<LocalNodeWorker> _logger;

    // The gossip daemon CURRENTLY bound + driven by this worker, and the team it
    // belongs to. Rebound on a team-switch. Null until the first team's gossip is
    // started (or on a team with no IGossipDaemon).
    private IGossipDaemon? _boundGossip;
    private TeamContext? _boundTeam;

    // The stopping token captured at ExecuteAsync so the rebind path (driven from
    // the ActiveChanged event, off the ExecuteAsync call stack) can start the new
    // daemon's loops bound to the host's shutdown lifetime.
    private CancellationToken _stoppingToken = CancellationToken.None;

    // Serializes start/stop/rebind so a team-switch event cannot interleave with
    // the initial boot start or a concurrent switch. The whole rebind runs under it.
    private readonly SemaphoreSlim _gossipGate = new(1, 1);

    // Multi-device INC-5 — held so they can be torn down on shutdown / rebind.
    private IDisposable? _discoverySubscription;
    private bool _discoveryStarted;

    // Push-on-change — the local-delta → daemon-push subscription, held so it can
    // be detached on shutdown / rebind before the daemon stops.
    private EventHandler? _pushOnChangeHandler;

    // The team-switch subscription, held so it can be detached on shutdown.
    private EventHandler<ActiveTeamChangedEventArgs>? _activeChangedHandler;

    // MAJOR-1 (cerebrum [2026-06-21] verdict): the rebind runs OFF the join's call stack, so a faulted
    // rebind (incl. the BLOCKER-1 EADDRINUSE) used to be swallowed to a LogError AFTER the join already
    // returned 200 {joined:true} — leaving B silently active-team=A with NO running daemon, undetectable.
    // We now record the last rebind's outcome here so it is QUERYABLE: the sync-status surface reads it and
    // reports a degraded "enrollment-incomplete" state, and tests can assert a faulted rebind is detectable
    // (not a silent success). Written under _gossipGate at the end of every rebind; read lock-free (an
    // advisory status snapshot, not a transactional invariant).
    private volatile RebindOutcome _lastRebind = RebindOutcome.Healthy();

    public LocalNodeWorker(
        INodeHost nodeHost,
        IPluginRegistry pluginRegistry,
        IEnumerable<ILocalNodePlugin> plugins,
        ITeamContextFactory teamContextFactory,
        IActiveTeamAccessor activeTeam,
        IOptions<LocalNodeOptions> options,
        ILogger<LocalNodeWorker> logger,
        IPeerDiscovery? peerDiscovery = null,
        ICrdtProjectionRegistry? crdtProjectionRegistry = null,
        NodeTeamRoster? teamRoster = null)
    {
        ArgumentNullException.ThrowIfNull(nodeHost);
        ArgumentNullException.ThrowIfNull(pluginRegistry);
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(teamContextFactory);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _nodeHost = nodeHost;
        _pluginRegistry = pluginRegistry;
        _plugins = plugins;
        _teamContextFactory = teamContextFactory;
        _activeTeam = activeTeam;
        _options = options.Value;
        _peerDiscovery = peerDiscovery; // null unless AddMdnsPeerDiscovery registered one
        _crdtProjectionRegistry = crdtProjectionRegistry;
        _teamRoster = teamRoster;
        _logger = logger;
    }

    /// <summary>Current host state; surfaced for tests and diagnostics.</summary>
    public NodeState CurrentState => _nodeHost.State;

    /// <summary>The plugin registry this worker drives; surfaced for tests and diagnostics.</summary>
    public IPluginRegistry PluginRegistry => _pluginRegistry;

    /// <summary>
    /// The active team's gossip daemon, if a team is currently active and its
    /// provider has one registered. Null on hosts that have not yet had a team
    /// materialized. Surfaced for tests and diagnostics.
    /// </summary>
    /// <remarks>
    /// After a team-switch + daemon-rebind this returns the NEW active team's
    /// daemon — the same instance the worker is now driving (<see cref="_boundGossip"/>
    /// tracks it). Reads live from the active team's provider so it stays correct
    /// across rebinds.
    /// </remarks>
    public IGossipDaemon? Gossip =>
        _activeTeam.Active?.Services.GetService<IGossipDaemon>();

    /// <summary>
    /// The gossip daemon the worker is CURRENTLY driving (started + listening +
    /// push-wired), and the team it belongs to. Surfaced for tests so a rebind can
    /// be asserted directly (the bound daemon's team id flips to the joined team).
    /// </summary>
    public IGossipDaemon? BoundGossip => _boundGossip;

    /// <summary>The team whose daemon the worker is currently driving; null before the first start.</summary>
    public TeamContext? BoundTeam => _boundTeam;

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        var activeTeam = _activeTeam.Active;
        if (activeTeam?.Services.GetService<IGossipDaemon>() is not null
            && activeTeam.Services.GetService<IPeerTrustPolicy>() is null)
        {
            throw new InvalidOperationException(
                "The active team's peer trust policy is missing; refusing allow-all sync startup.");
        }

        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// MAJOR-1 — the outcome of the most recent daemon-rebind (team-switch). <see cref="RebindOutcome.IsHealthy"/>
    /// is true on boot (no rebind yet) and after every SUCCESSFUL rebind; it goes false (with a fault summary +
    /// the team the rebind was switching TO) when a rebind FAULTS — e.g. the new team's transport could not bind
    /// its listener. A faulted rebind leaves the node active-team=A with NO running gossip daemon (it presents no
    /// HELLO / cannot sync), so this is the LOUD, queryable signal the join response + the sync-status surface read
    /// to report an "enrollment-incomplete / degraded" state rather than a silent success. Surfaced for tests +
    /// diagnostics + the sync-status route.
    /// </summary>
    public RebindOutcome LastRebind => _lastRebind;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;

        _logger.LogInformation(
            "Harborline local-node host starting (node state: {State})",
            _nodeHost.State);

        // Materialize the injected plugin enumerable so tests can assert
        // against LoadedPlugins.Count without a second DI resolution.
        var pluginList = _plugins as IReadOnlyCollection<ILocalNodePlugin>
            ?? _plugins.ToList();

        await _pluginRegistry.LoadAllAsync(pluginList, stoppingToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Loaded {Count} plugin(s)",
            _pluginRegistry.LoadedPlugins.Count);

        await _nodeHost.StartAsync(stoppingToken).ConfigureAwait(false);

        // Wave 6.3.E.2: gossip is team-scoped. Start it for the boot active team.
        // gap #3 (cerebrum [2026-06-21]): then subscribe to ActiveChanged so a
        // later team-switch (a wire-enrollment JOIN) REBINDS the daemon to the new
        // team's key.
        await _gossipGate.WaitAsync(stoppingToken).ConfigureAwait(false);
        try
        {
            await StartGossipForTeamAsync(_activeTeam.Active, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            _gossipGate.Release();
        }

        _activeChangedHandler = OnActiveTeamChanged;
        _activeTeam.ActiveChanged += _activeChangedHandler;

        // Idle loop — the kernel is the worker; we just wait. Timeout.Infinite
        // so the process burns no CPU; cancellation is the only exit path.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on graceful shutdown. Swallow and fall through to teardown.
        }

        _logger.LogInformation("Harborline local-node host stopping");

        // Detach the team-switch subscription first so a switch during teardown
        // does not kick off a rebind into a stopping host.
        if (_activeChangedHandler is not null)
        {
            _activeTeam.ActiveChanged -= _activeChangedHandler;
            _activeChangedHandler = null;
        }

        // Shutdown uses a fresh CancellationToken.None so the shutdown path
        // runs to completion even when the caller's token is already cancelled.
        // Reverse-order of startup: gossip first, node host second, plugins last.
        await _gossipGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopBoundGossipAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gossipGate.Release();
        }
        await _nodeHost.StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _pluginRegistry.UnloadAllAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// DAEMON-REBIND (gap #3). Fired when <see cref="IActiveTeamAccessor.Active"/> changes — i.e. a wire-enrollment
    /// JOIN switched this node onto the admitter's team. Stops the prior team's daemon and starts the now-active
    /// team's daemon, so the node presents the NEW team's HELLO key on the trusted handshake. The event handler is
    /// synchronous; it spins the async rebind onto a task (serialized by <see cref="_gossipGate"/>) and never lets a
    /// failure escape onto the caller's <c>SetActiveAsync</c> path — a rebind fault must not brick the join.
    /// </summary>
    private void OnActiveTeamChanged(object? sender, ActiveTeamChangedEventArgs e)
    {
        // No-op if the active team did not actually change to a different context.
        if (ReferenceEquals(e.Current, _boundTeam))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await _gossipGate.WaitAsync(_stoppingToken).ConfigureAwait(false);
            try
            {
                // Re-check under the gate: a racing switch may have already rebound to this team.
                if (ReferenceEquals(_activeTeam.Active, _boundTeam))
                {
                    return;
                }

                var target = _activeTeam.Active;
                _logger.LogInformation(
                    "Active team changed ({Previous} → {Current}) — REBINDING the gossip daemon to the new "
                    + "team's HELLO key (wire-enrollment join / team-switch).",
                    _boundTeam?.TeamId.ToString() ?? "(none)",
                    target?.TeamId.ToString() ?? "(none)");

                // BLOCKER-1 (cerebrum [2026-06-21] verdict): the OLD team's TcpSyncDaemonTransport binds the
                // fixed production port (0.0.0.0:7473) in its ctor and releases it only on DisposeAsync. Stopping
                // the daemon LOOPS does NOT free the socket. So we DISPOSE the old team context here (releasing
                // 7473) BEFORE binding the new team's transport — otherwise the new team's ctor hits EADDRINUSE on
                // the production fixed-port config. Disposing the old context is safe: B LEFT its genesis team on
                // enrollment (Active is already the new team, re-checked above), so nothing references the old
                // context anymore. The disposal cascades context → child provider → the transport singleton.
                await StopBoundGossipAsync(_stoppingToken, disposeOldContext: true).ConfigureAwait(false);
                await StartGossipForTeamAsync(target, _stoppingToken).ConfigureAwait(false);

                // MAJOR-1: the rebind settled. The new team's daemon either bound (BoundGossip non-null for a
                // sync-enabled team) or the team genuinely registered no daemon. Mark healthy.
                _lastRebind = RebindOutcome.Healthy();
            }
            catch (OperationCanceledException)
            {
                // Host is shutting down — the rebind is moot. Do NOT mark a fault: shutdown is not a failure.
            }
            catch (Exception ex)
            {
                // MAJOR-1: fail LOUD + DETECTABLE. The join route already returned 200 {joined:true} (the rebind
                // runs off that stack), so swallowing this to a log line alone is the silent half-rebound the
                // verdict flagged: active-team=A, roster adopted, but NO running daemon → B presents no HELLO →
                // PEER_UNTRUSTED, with the user told "joined". We record the fault so the sync-status surface (and
                // the join response, which can re-read it after the rebind settles) reports an
                // enrollment-INCOMPLETE / degraded state the Harborline App can show + the user can retry. We do NOT
                // re-throw (a rebind fault must not crash the host loop); the loud signal is the queryable state.
                var targetTeamId = _activeTeam.Active?.TeamId;
                _lastRebind = RebindOutcome.Faulted(targetTeamId, ex.GetType().Name + ": " + ex.Message);
                _logger.LogError(ex,
                    "Daemon-rebind on team-switch FAILED — the new team's gossip daemon is NOT running, so this "
                    + "node cannot sync on the joined team (it presents no HELLO). The join's roster adoption + "
                    + "active-team switch still hold, but the enrollment is INCOMPLETE: the sync-status surface "
                    + "now reports a degraded state and a host restart (or a re-join) re-binds the daemon. Target "
                    + "team: {Target}.",
                    _activeTeam.Active?.TeamId.ToString() ?? "(none)");
            }
            finally
            {
                _gossipGate.Release();
            }
        });
    }

    /// <summary>
    /// Start gossip for <paramref name="team"/> and record it as the bound daemon. Resolves the team's
    /// <see cref="IGossipDaemon"/> (null on a team that registered none — e.g. a sync-disabled install), forces the
    /// sync-status read-model's construction (so its subscription is live before the first round), starts the
    /// round loop + accept loop, wires cross-machine discovery/dial, and wires push-on-change. Idempotent per team
    /// (the daemon's own Start* calls are idempotent). Caller MUST hold <see cref="_gossipGate"/>.
    /// </summary>
    private async Task StartGossipForTeamAsync(TeamContext? team, CancellationToken ct)
    {
        var gossip = team?.Services.GetService<IGossipDaemon>();
        if (gossip is null)
        {
            _boundGossip = null;
            _boundTeam = team;
            _logger.LogWarning(
                "No active team context exposes IGossipDaemon — local-node host running without sync. " +
                "Active team: {TeamId}",
                team?.TeamId.ToString() ?? "(none)");
            return;
        }

        if (team!.Services.GetService<IPeerTrustPolicy>() is null)
        {
            throw new InvalidOperationException(
                "The active team's peer trust policy is missing; refusing allow-all sync startup.");
        }

        // Sync-status read-model (Phase A). Resolve it BEFORE StartAsync so its
        // constructor subscribes to the daemon's FrameReceived + RoundCompleted
        // events before the first round runs — otherwise a round that completes
        // between StartAsync and a later first-resolve would be missed by the
        // cadence projection. Null on a team that registered none — harmless.
        _ = team.Services.GetService<ISyncStatusReadModel>();

        // Start the gossip daemon — paper §6.1 tier-1 intra-team sync.
        await gossip.StartAsync(ct).ConfigureAwait(false);

        // Multi-device INC-5 — start the inbound responder/accept loop so the
        // daemon authenticates inbound peers (LIVE roster trust gate) and applies
        // their deltas. Outbound-only (single-device) → a clean no-op.
        await gossip.StartListeningAsync(ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Gossip daemon started for team {TeamId} (peers: {Count}, listening: {Listening})",
            team.TeamId, gossip.KnownPeers.Count, gossip.IsListening);

        // Multi-device INC-5 — wire DISCOVERY + DIAL so two hosts don't merely
        // listen but actually find + connect to each other (static config peers +
        // optional mDNS). Keyed to THIS team's transport identity.
        await WireCrossMachineSyncAsync(gossip, team, ct).ConfigureAwait(false);

        // Push-on-change — local edits trigger an immediate authenticated round.
        WirePushOnChange(gossip, ct);

        _boundGossip = gossip;
        _boundTeam = team;
    }

    /// <summary>
    /// Stop the currently-bound gossip daemon (gap #3 rebind teardown / shutdown). Detaches push-on-change, unwinds
    /// discovery, stops the accept loop + round loop, and clears the bound state — leaving the worker ready to bind
    /// a different team's daemon. Safe to call when nothing is bound (no-op). Caller MUST hold <see cref="_gossipGate"/>.
    /// </summary>
    /// <param name="disposeOldContext">
    /// BLOCKER-1 (cerebrum [2026-06-21] verdict). When <c>true</c> (the REBIND path) the just-stopped team's
    /// <see cref="TeamContext"/> is disposed via <see cref="ITeamContextFactory.RemoveAsync"/> AFTER its daemon
    /// loops stop — which cascades context → child provider → the <see cref="TcpSyncDaemonTransport"/> singleton's
    /// <c>DisposeAsync</c>, releasing the OS listener socket (the fixed production <c>0.0.0.0:7473</c> bind). This
    /// MUST happen before the new team's transport ctor binds the SAME fixed port, else it throws
    /// <c>EADDRINUSE</c>. Stopping the daemon loops alone does NOT free the socket — only disposing the transport
    /// does. It is safe to dispose the old context here because the rebind only runs once the active team has
    /// already switched away from it (re-checked under the gate in <see cref="OnActiveTeamChanged"/>), so nothing
    /// references the now-inactive old context. When <c>false</c> (the SHUTDOWN path) the context is left for the
    /// host's own DI teardown to dispose — the whole process is going down, so freeing the port early is moot and
    /// removing the still-active team from the factory mid-shutdown is needless churn.
    /// </param>
    private async Task StopBoundGossipAsync(CancellationToken ct, bool disposeOldContext = false)
    {
        var gossip = _boundGossip;
        var oldTeam = _boundTeam;
        if (gossip is null)
        {
            _boundTeam = null;
            return;
        }

        // Push-on-change — detach the local-delta → push subscription FIRST so a
        // late local edit during teardown does not fire a push into a stopping daemon.
        if (_pushOnChangeHandler is not null && _crdtProjectionRegistry is not null)
        {
            _crdtProjectionRegistry.LocalDeltaProduced -= _pushOnChangeHandler;
            _pushOnChangeHandler = null;
        }

        // Multi-device INC-5 — unwind discovery before the daemon: detach the
        // mDNS→gossip subscription and stop advertising so no PeerDiscovered fires
        // into a stopping daemon.
        _discoverySubscription?.Dispose();
        _discoverySubscription = null;
        if (_discoveryStarted && _peerDiscovery is not null)
        {
            try
            {
                await _peerDiscovery.StopAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "mDNS peer discovery stop failed (non-fatal).");
            }
            _discoveryStarted = false;
        }

        await gossip.StopListeningAsync(ct).ConfigureAwait(false);
        await gossip.StopAsync(ct).ConfigureAwait(false);

        _boundGossip = null;
        _boundTeam = null;

        // BLOCKER-1: on the REBIND path, dispose the old team context so its transport releases the fixed 7473
        // listener BEFORE the new team's transport tries to bind the SAME port. Use CancellationToken.None for the
        // disposal so a cancelled rebind token cannot leave the old socket bound (which would brick every future
        // rebind for the host's lifetime). RemoveAsync → TeamContext.DisposeAsync → child-provider dispose →
        // TcpSyncDaemonTransport.DisposeAsync (socket released).
        if (disposeOldContext && oldTeam is not null)
        {
            try
            {
                await _teamContextFactory.RemoveAsync(oldTeam.TeamId, CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation(
                    "Disposed the previous team context {TeamId} on rebind — its sync listener socket (the fixed "
                    + "{Port} bind) is released so the new team's daemon can bind it.",
                    oldTeam.TeamId, TcpSyncDaemonTransport.DefaultPort);
            }
            catch (Exception ex)
            {
                // A disposal fault must not abort the rebind (the new daemon may still bind on an ephemeral / free
                // port, or this team registered no listener). But it IS the BLOCKER's failure mode if the port is
                // still held — so log it loudly; the subsequent StartGossipForTeamAsync bind will surface the
                // EADDRINUSE (caught by OnActiveTeamChanged → fail-loud) if the socket truly didn't release.
                _logger.LogError(ex,
                    "Failed to dispose the previous team context {TeamId} on rebind — its sync listener socket may "
                    + "still be bound, which can cause the new team's transport to fail address-in-use.",
                    oldTeam.TeamId);
            }
        }
    }

    /// <summary>
    /// Multi-device INC-5 — wire peer DISCOVERY + DIAL onto the live per-team gossip
    /// daemon. Two hosts that merely <c>StartListeningAsync</c> never converge: each
    /// must learn the other's endpoint and dial it. This bridges the two production
    /// discovery paths onto the daemon:
    /// <list type="number">
    ///   <item><b>Static peers</b> (<c>LocalNode:Sync:Peers</c>) — dialed directly via
    ///     <see cref="IGossipDaemon.AddPeer"/> with THIS node's team public key (the
    ///     trust anchor; a same-root peer authenticates, a different-root one is
    ///     rejected fail-closed). The cross-subnet / Tailscale path.</item>
    ///   <item><b>mDNS</b> (when <c>LocalNode:Sync:EnableMdns</c> + an
    ///     <see cref="IPeerDiscovery"/> is registered) — advertise a LAN-routable
    ///     <see cref="PeerAdvertisement"/> built from the team transport's
    ///     <see cref="TcpSyncDaemonTransport.ListenEndpoint"/>, then
    ///     <see cref="GossipDaemonDiscoveryExtensions.AttachDiscovery"/> so discovered
    ///     same-subnet peers auto-<c>AddPeer</c>. Link-local only.</item>
    /// </list>
    /// The team public key is read from the per-team <see cref="INodeIdentityProvider"/>
    /// the registrar installed in the child container; the routable advertised
    /// endpoint comes from the per-team <see cref="ISyncDaemonTransport"/>.
    /// </summary>
    private async Task WireCrossMachineSyncAsync(
        IGossipDaemon gossip, TeamContext activeTeam, CancellationToken ct)
    {
        // The trust anchor for every dial: this node's team-scoped public key. A
        // sibling device with the same root seed + team id derives it identically
        // (HKDF, ADR 0032) — so a same-root peer is trusted, a stranger rejected.
        var identityProvider = activeTeam.Services.GetService<INodeIdentityProvider>();
        if (identityProvider is null)
        {
            _logger.LogWarning(
                "Active team exposes no INodeIdentityProvider — cannot derive the team " +
                "public key to dial peers; cross-machine sync wiring skipped.");
            return;
        }
        var teamIdentity = identityProvider.Current;
        var teamPublicKey = teamIdentity.PublicKey;

        // (a) Static config-driven peers — the cross-subnet / Tailscale path.
        var dialed = 0;
        foreach (var raw in _options.Sync.Peers)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var endpoint = SyncEndpoint.NormalizeTcpEndpoint(raw);
            gossip.AddPeer(endpoint, teamPublicKey);
            dialed++;
            _logger.LogInformation(
                "Static sync peer added: {Endpoint} (trusted via shared team key).", endpoint);
        }
        if (dialed > 0)
        {
            _logger.LogInformation(
                "Dialed {Count} static sync peer(s); gossip now has {Total} known peer(s).",
                dialed, gossip.KnownPeers.Count);
        }

        // (b) mDNS auto-discovery — same-subnet only. Skipped unless enabled AND an
        // IPeerDiscovery is registered (AddMdnsPeerDiscovery in Program.cs).
        if (!_options.Sync.EnableMdns || _peerDiscovery is null)
        {
            return;
        }

        var transport = activeTeam.Services.GetService<ISyncDaemonTransport>();
        if (transport is not TcpSyncDaemonTransport tcp || tcp.ListenEndpoint is null)
        {
            _logger.LogWarning(
                "mDNS enabled but the active team has no LAN-routable TCP listen endpoint " +
                "(transport: {Transport}). mDNS advertisement needs a bound listener — " +
                "set LocalNode:Sync:BindAddress (e.g. 0.0.0.0:7473) and ListenForPeers. " +
                "Static-peer dial still works.",
                transport?.GetType().Name ?? "(none)");
            return;
        }

        var advertisement = TcpPeerAdvertisement.ForTransport(
            tcp,
            nodeId: teamIdentity.NodeId,
            teamPublicKey: teamPublicKey,
            teamId: activeTeam.TeamId.ToString(),
            schemaVersion: HandshakeProtocol.DefaultSchemaVersion,
            rosterId: _teamRoster?.DiscoveryRosterId
                ?? throw new InvalidOperationException(
                    "mDNS discovery requires the active roster root; refusing unscoped peer discovery."));

        await _peerDiscovery.StartAsync(advertisement, ct).ConfigureAwait(false);
        _discoveryStarted = true;
        _discoverySubscription = gossip.AttachDiscovery(_peerDiscovery);
        _logger.LogInformation(
            "mDNS discovery started — advertising {Endpoint}; discovered same-subnet peers auto-dial.",
            advertisement.Endpoint);
    }

    /// <summary>
    /// Push-on-change (snappy-convergence follow-up). Subscribe the CRDT projection
    /// registry's <see cref="ICrdtProjectionEventSource.LocalDeltaProduced"/> signal to
    /// the live gossip daemon's <see cref="IGossipDaemon.TriggerPushAsync"/> so a
    /// local edit ships to connected, authenticated peers immediately rather than on
    /// the next periodic anti-entropy tick.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Reuses the authenticated path.</b> <c>TriggerPushAsync</c> runs the SAME
    /// round the periodic timer drives — signed HELLO, the LIVE shared-root trust
    /// gate, the DELTA_STREAM rate limit, the accept-loop DoS caps. It is purely a
    /// "run the round now" trigger; it opens no new connection and bypasses no trust
    /// check.
    /// </para>
    /// <para>
    /// <b>Fire-and-forget + non-fatal.</b> The local-delta signal is raised synchronously
    /// on a doctype write path; the handler must return
    /// immediately, so it spins the push onto a background task and swallows any
    /// failure — the periodic anti-entropy round is the backstop. The daemon's own
    /// coalescing bounds a burst of edits to ~2 rounds.
    /// </para>
    /// <para>
    /// <b>Rebind-safe.</b> The captured <paramref name="gossip"/> is the CURRENTLY-bound
    /// daemon. On a team-switch the prior subscription is detached in
    /// <see cref="StopBoundGossipAsync"/> before the daemon stops, then this re-wires
    /// against the new team's daemon — so a local edit always pushes to the daemon
    /// the worker is actually driving.
    /// </para>
    /// </remarks>
    private void WirePushOnChange(IGossipDaemon gossip, CancellationToken ct)
    {
        if (_crdtProjectionRegistry is null)
        {
            return; // no synced doctype composed — nothing to push on.
        }

        _pushOnChangeHandler = (_, _) =>
        {
            // Fire-and-forget the immediate push. Never block the synchronous raise
            // on the contact write path; never let a push failure escape (the
            // periodic round repairs a missed push).
            _ = Task.Run(async () =>
            {
                try
                {
                    await gossip.TriggerPushAsync(
                        OutboundSyncLane.Foreground,
                        ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    /* shutdown — the periodic round is the backstop */
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "Push-on-change trigger failed (non-fatal); the periodic anti-entropy round will catch up.");
                }
            }, ct);
        };
        _crdtProjectionRegistry.LocalDeltaProduced += _pushOnChangeHandler;
        _logger.LogInformation(
            "Push-on-change wired: local CRDT edits trigger an immediate authenticated gossip round.");
    }
}

/// <summary>
/// MAJOR-1 (cerebrum [2026-06-21] verdict) — the queryable outcome of the most recent daemon-rebind
/// (team-switch). The rebind runs OFF the join's call stack, so without this a faulted rebind (incl. the
/// BLOCKER-1 <c>EADDRINUSE</c>) was undetectable: the join had already returned <c>200 {joined:true}</c> and the
/// only signal was a log line. This record makes the half-rebound state LOUD + queryable — the sync-status
/// surface reads it to report an "enrollment-incomplete / degraded" state and the join response can re-read it
/// after the rebind settles, so a user is never told a join SUCCEEDED while the node sits unable to sync on the
/// joined team.
/// </summary>
/// <param name="IsHealthy">True on boot (no rebind has run) and after every SUCCESSFUL rebind. False when the
/// most recent rebind FAULTED — the node is active-team=A with no running daemon (presents no HELLO, cannot
/// sync).</param>
/// <param name="FaultedTeamId">When not healthy, the team the faulted rebind was switching TO (the joined team
/// the node now cannot serve); null when healthy.</param>
/// <param name="FaultSummary">When not healthy, a short, PII-free fault summary (exception type + message) for
/// diagnostics; null when healthy.</param>
public sealed record RebindOutcome(bool IsHealthy, TeamId? FaultedTeamId, string? FaultSummary)
{
    /// <summary>The healthy outcome — boot or a successful rebind. No fault details.</summary>
    public static RebindOutcome Healthy() => new(true, null, null);

    /// <summary>A FAULTED rebind switching to <paramref name="faultedTeamId"/> — the node cannot sync on it.</summary>
    public static RebindOutcome Faulted(TeamId? faultedTeamId, string faultSummary) =>
        new(false, faultedTeamId, faultSummary);
}
