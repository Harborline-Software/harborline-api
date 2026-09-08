using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ProtocolApplicationRoutes = Harborline.Api.Protocol.HarborlineApplicationRoutes;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Sync.Gossip;
using Harborline.Api.Kernel.Sync.Protocol;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Node-local multi-device sync-status surface (Phase A — sync-status-read-model
/// survey 2026-06-19; PAO design 2026-06-19). Exposes the four-state
/// (HAS / WILL / SHOULD / COULDN'T) sync status the Harborline App UI reads, projected
/// from the active team's gossip daemon by <see cref="ISyncStatusReadModel"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route:</b> <c>GET /api/local-node/sync-status</c> — the observed snapshot:
/// the worst-state-wins fleet aggregate, the per-peer rows, and the WILL
/// round-cadence. Each value is THIS node's view "as of last contact"; the
/// payload carries the observer's <c>asOf</c> stamp plus peer-exchange recency
/// that explicitly says currentness is not established. The Harborline App polls this
/// and renders L1–L4.
/// </para>
/// <para>
/// <b>The read-model is team-scoped.</b> The gossip daemon (and so the
/// <see cref="ISyncStatusReadModel"/>) lives in the active team's child
/// container, not the install-level provider — so the route resolves it
/// per-request from <see cref="IActiveTeamAccessor.Active"/> (the
/// <see cref="LocalNodeHealthCheck"/> precedent), NOT via the closed-over outer
/// container. When no team is active yet (boot) the route returns a calm
/// HAS-aggregate empty snapshot rather than an error — a node with no
/// materialized team has nothing failing.
/// </para>
/// <para>
/// <b>Wire contract (matched 1:1 by FED's Harborline App UI).</b> Fleet aggregate
/// state is one of <c>'has' | 'will' | 'should' | 'couldnt'</c> (worst-state-wins);
/// each peer is
/// <c>{ deviceId, label, state, lastReachedAt, offlineDurationMs, isSecurityEvent, errorCode? }</c>;
/// WILL cadence is <c>{ nextRoundAt, roundIntervalSeconds }</c>; recency is
/// <c>{ basis, currentness, lastExchange? }</c>. The per-device
/// offline THRESHOLD is NOT in the payload — the read exposes raw
/// <c>lastReachedAt</c> + <c>offlineDurationMs</c> and the per-device
/// calm→actionable threshold is UX config (CIC 2026-06-20).
/// </para>
/// <para>
/// <b>Caller-auth (inc-4 F1).</b> Like every other non-allowlisted
/// <c>/api/local-node/*</c> route this is gated by the LISTENER-LEVEL caller-auth
/// middleware (<c>SharedHostedWebApp</c>): a loopback bind authenticates the host,
/// not the calling process, so the Harborline App's per-boot session token is required
/// (fail-closed 401) when configured. The route ALSO keeps a per-route check as
/// defence-in-depth. It is READ-ONLY — a pure projection, no mutation.
/// </para>
/// </remarks>
public static class SyncStatusRoutes
{
    /// <summary>Canonical route for the node-local sync-status surface.</summary>
    public const string RouteBase = ProtocolApplicationRoutes.GetSyncStatus;

    /// <summary>
    /// Maps the sync-status route onto <paramref name="app"/>, closing over the
    /// install-level <paramref name="activeTeam"/>. The per-request handler
    /// resolves the team-scoped <see cref="ISyncStatusReadModel"/> from the
    /// active team's provider.
    /// </summary>
    public static void Map(IEndpointRouteBuilder app, IActiveTeamAccessor activeTeam, TimeProvider timeProvider)
        => Map(app, activeTeam, new NodeCallerSessionToken(null), timeProvider, rebindStatus: null);

    /// <summary>
    /// Maps the sync-status route with the inc-4 cross-process CALLER-AUTH guard
    /// (<paramref name="callerAuth"/>) enforced. A local stranger process without the
    /// Harborline App's per-boot session token is rejected <b>fail-closed (401)</b> before the
    /// snapshot is projected; a missing token (dev/single-host) runs un-enforced (the
    /// 2-arg overload above). See <see cref="NodeCallerSessionToken"/>.
    /// </summary>
    public static void Map(
        IEndpointRouteBuilder app,
        IActiveTeamAccessor activeTeam,
        NodeCallerSessionToken callerAuth,
        TimeProvider timeProvider)
        => Map(app, activeTeam, callerAuth, timeProvider, rebindStatus: null);

    /// <summary>
    /// Maps the sync-status route, ALSO surfacing the MAJOR-1 enrollment-rebind health
    /// (<paramref name="rebindStatus"/>; cerebrum [2026-06-21] verdict). When a daemon-rebind from a
    /// wire-enrollment JOIN FAULTED, the node is active-team=A with NO running gossip daemon — it presents no
    /// HELLO and cannot sync, yet the join route already returned <c>200 {joined:true}</c>. So the response carries
    /// a top-level <c>enrollment</c> object that goes <c>complete:false</c> + a degraded reason on a faulted
    /// rebind, making the silent half-rebound LOUD + queryable for the Harborline App. <paramref name="rebindStatus"/>
    /// null (e.g. the legacy/test overload, a host with no joiner role) ⇒ the enrollment object reports a healthy
    /// default, preserving the prior wire shape for consumers that don't read it.
    /// </summary>
    public static void Map(
        IEndpointRouteBuilder app,
        IActiveTeamAccessor activeTeam,
        NodeCallerSessionToken callerAuth,
        TimeProvider timeProvider,
        IEnrollmentRebindStatus? rebindStatus)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(callerAuth);
        ArgumentNullException.ThrowIfNull(timeProvider);

        app.MapGet(RouteBase, (HttpRequest httpRequest) =>
        {
            // inc-4 CALLER AUTH (fail-closed): loopback bind ≠ caller trust.
            // inc-4 F1: the AUTHORITATIVE gate is now the LISTENER-LEVEL middleware in
            // SharedHostedWebApp (gate-all-by-default); this per-route check is defence-in-depth.
            if (callerAuth.Validate(httpRequest) is NodeCallerSessionToken.Decision.Reject)
                return NodeCallerSessionToken.RejectResult();

            // MAJOR-1: the enrollment-rebind health rides alongside the sync snapshot. A FAULTED rebind is the
            // silent half-rebound the verdict flagged — surface it loudly so a "joined" node that cannot actually
            // sync is detectable (the Harborline App shows degraded; the user can retry).
            //
            // The enrollment read is itself fail-safe: a rebind-status that throws must NOT 500 the readout (see
            // SafeEnrollment). The whole point of this surface is that a "joined" node whose status is momentarily
            // unreadable still gets a calm, queryable answer — never an opaque 500 that hides whether the join
            // converged.
            var enrollment = SafeEnrollment(rebindStatus);

            // POST-JOIN-TRIGGER SURVIVAL. A wire-enrollment JOIN switches the active team OFF the join's call stack
            // (NodeEnrollmentJoinService.SetActiveAsync → the worker's REBIND in a Task.Run: dispose the old team
            // context, build + start the adopted team's daemon). The Harborline App polls THIS route immediately after the
            // join route returned 200 {joined:true}. Resolving the team-scoped ISyncStatusReadModel from the active
            // (newly-adopted) team's child container, or projecting its Snapshot(), can THROW during that transient
            // window — e.g. the adopted-team daemon is still constructing, or a racing rebind is disposing a child
            // provider (GetService throws ObjectDisposedException; a faulting ctor propagates — GetService returns
            // null ONLY for an UNREGISTERED service). An unhandled throw here is the 500 the Harborline App reported as
            // "node GET /api/local-node/sync-status failed: 500", which MASKED a join that may have actually
            // converged. A status READOUT failure must degrade to a calm/degraded snapshot, NOT 500 the endpoint.
            try
            {
                var readModel = activeTeam.Active?.Services.GetService<ISyncStatusReadModel>();
                if (readModel is null)
                {
                    // No active team / sync not enabled: a calm empty snapshot (HAS
                    // aggregate, no peers). Distinct-honesty: still stamps the
                    // observer clock. The enrollment signal still rides (a rebind can have
                    // faulted BEFORE a read-model materialized).
                    return Results.Ok(SyncStatusResponse.Empty(timeProvider.GetUtcNow()) with { Enrollment = enrollment });
                }

                var snapshot = readModel.Snapshot();
                return Results.Ok(SyncStatusResponse.From(snapshot) with { Enrollment = enrollment });
            }
            catch (Exception ex)
            {
                // The read-model could not be resolved/projected (a transient post-join-trigger / mid-rebind state,
                // a disposed child provider, or a faulting daemon ctor). Return 200 with a CALM EMPTY snapshot whose
                // enrollment signal is marked degraded ("sync status not yet readable") so the Harborline App keeps polling
                // and renders a transient/degraded state rather than treating the readout failure as the join
                // failing. This is a READOUT-resilience guard ONLY — it changes no enrollment/trust/sync behaviour;
                // the underlying join converges (or its own rebind fault surfaces) independently.
                return Results.Ok(
                    SyncStatusResponse.Empty(timeProvider.GetUtcNow()) with
                    {
                        Enrollment = enrollment.AsUnreadable(ex.GetType().Name),
                    });
            }
        });
    }

    /// <summary>
    /// Read the enrollment-rebind health <b>fail-safe</b>. The rebind-status seam is the live worker; reading
    /// <see cref="IEnrollmentRebindStatus.LastRebind"/> must never throw into the request path (a diagnostics /
    /// status concern can never 500 the readout). Any fault degrades to the healthy default — the join's own rebind
    /// fault, when there is one, still surfaces on the next readable poll.
    /// </summary>
    private static EnrollmentWire SafeEnrollment(IEnrollmentRebindStatus? rebindStatus)
    {
        try
        {
            return EnrollmentWire.From(rebindStatus?.LastRebind);
        }
        catch
        {
            // A status readout must never throw into the request. Fall back to the healthy wire shape; the real
            // rebind state (if faulted) re-surfaces on the next poll once the seam reads cleanly.
            return EnrollmentWire.Healthy;
        }
    }
}

// ── Wire shapes — the shared read-model contract (camelCase JSON) ───────────────

/// <summary>
/// The <c>GET /api/local-node/sync-status</c> response. Matches the shared
/// read-model contract FED's Harborline App UI is built against.
/// </summary>
/// <param name="Aggregate">Fleet worst-state-wins state:
/// <c>'has' | 'will' | 'should' | 'couldnt'</c>.</param>
/// <param name="Peers">Per-peer rows.</param>
/// <param name="Cadence">WILL round-cadence info.</param>
/// <param name="AsOf">Observer (this node) clock at snapshot time — the
/// "(my view)" honesty stamp (ISO-8601).</param>
public sealed record SyncStatusResponse(
    [property: JsonPropertyName("aggregate")] string Aggregate,
    [property: JsonPropertyName("peers")] IReadOnlyList<SyncPeerWire> Peers,
    [property: JsonPropertyName("cadence")] SyncCadenceWire Cadence,
    [property: JsonPropertyName("asOf")] string AsOf)
{
    /// <summary>
    /// What this node can truthfully report about recency. This is peer-exchange
    /// evidence only; gossip does not establish that the held data is current.
    /// </summary>
    [property: JsonPropertyName("recency")]
    public SyncRecencyWire Recency { get; init; } = SyncRecencyWire.NeverExchanged;

    /// <summary>
    /// MAJOR-1 — the enrollment-rebind health (cerebrum [2026-06-21] verdict). <c>complete:false</c> means the
    /// most recent wire-enrollment JOIN's daemon-rebind FAULTED and the node cannot sync on the joined team — the
    /// LOUD, queryable signal the Harborline App reads so a "joined" node that silently can't sync is visible. Defaults to
    /// the healthy shape, preserving the prior wire contract for consumers that don't read it.
    /// </summary>
    [property: JsonPropertyName("enrollment")]
    public EnrollmentWire Enrollment { get; init; } = EnrollmentWire.Healthy;

    /// <summary>Projects a <see cref="SyncStatusSnapshot"/> onto the wire shape.</summary>
    public static SyncStatusResponse From(SyncStatusSnapshot s)
    {
        var latestExchange = s.Peers
            .Where(peer => peer.LastReachedAt is not null)
            .MaxBy(peer => peer.LastReachedAt);

        return new(
            Aggregate: ToWireState(s.Aggregate),
            Peers: s.Peers.Select(SyncPeerWire.From).ToList(),
            Cadence: SyncCadenceWire.From(s.Cadence),
            AsOf: s.AsOf.ToString("O"))
        {
            Recency = SyncRecencyWire.From(latestExchange),
        };
    }

    /// <summary>An empty calm snapshot (HAS aggregate, no peers) at <paramref name="asOf"/>.</summary>
    public static SyncStatusResponse Empty(DateTimeOffset asOf) => new(
        Aggregate: "has",
        Peers: Array.Empty<SyncPeerWire>(),
        Cadence: new SyncCadenceWire(null, 0),
        AsOf: asOf.ToString("O"));

    /// <summary>Lowercase wire token for the four-state (matches the FED contract).</summary>
    internal static string ToWireState(SyncPeerState state) => state switch
    {
        SyncPeerState.Has => "has",
        SyncPeerState.Will => "will",
        SyncPeerState.Should => "should",
        SyncPeerState.Couldnt => "couldnt",
        _ => "has",
    };
}

/// <summary>
/// The evidence available for a surface to describe this node's recency without
/// implying that gossip established currentness.
/// </summary>
/// <param name="Basis"><c>peer-exchange</c> after at least one successful peer
/// exchange; <c>never-exchanged</c> otherwise.</param>
/// <param name="Currentness">Always <c>not-established</c>: this protocol has no
/// primary or authoritative latest position.</param>
/// <param name="LastExchange">The latest successful peer exchange known to this
/// node, or <c>null</c> when no peer has been reached.</param>
public sealed record SyncRecencyWire(
    [property: JsonPropertyName("basis")] string Basis,
    [property: JsonPropertyName("currentness")] string Currentness,
    [property: JsonPropertyName("lastExchange")] LastPeerExchangeWire? LastExchange)
{
    /// <summary>The honest recency statement for a node that has not reached a peer.</summary>
    public static readonly SyncRecencyWire NeverExchanged = new(
        Basis: "never-exchanged",
        Currentness: "not-established",
        LastExchange: null);

    /// <summary>Projects the latest reached peer, or the never-exchanged statement.</summary>
    public static SyncRecencyWire From(SyncPeerSnapshot? peer) => peer?.LastReachedAt is { } exchangedAt
        ? new(
            Basis: "peer-exchange",
            Currentness: "not-established",
            LastExchange: new LastPeerExchangeWire(peer.DeviceId, peer.Label, exchangedAt.ToString("O")))
        : NeverExchanged;
}

/// <summary>The peer and time of this node's latest successful exchange.</summary>
/// <param name="PeerDeviceId">Opaque peer identifier, or empty before identity validation.</param>
/// <param name="PeerLabel">Human-readable peer label.</param>
/// <param name="ExchangedAt">Successful exchange time in ISO-8601 format.</param>
public sealed record LastPeerExchangeWire(
    [property: JsonPropertyName("peerDeviceId")] string PeerDeviceId,
    [property: JsonPropertyName("peerLabel")] string PeerLabel,
    [property: JsonPropertyName("exchangedAt")] string ExchangedAt);

/// <summary>One peer row in the sync-status response.</summary>
/// <param name="DeviceId">Hex node id (lowercase), or empty if not yet validated.</param>
/// <param name="Label">Human-readable peer label.</param>
/// <param name="State"><c>'has' | 'will' | 'should' | 'couldnt'</c>.</param>
/// <param name="LastReachedAt">ISO-8601 of the last successful exchange, or
/// <c>null</c> if never reached.</param>
/// <param name="OfflineDurationMs">Milliseconds since <see cref="LastReachedAt"/>
/// (raw — the UX applies the per-device threshold), or <c>null</c>.</param>
/// <param name="IsSecurityEvent">True for the COULDN'T security lane
/// (untrusted-device).</param>
/// <param name="ErrorCode">Structured protocol error code (wire string), or
/// <c>null</c>.</param>
public sealed record SyncPeerWire(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("lastReachedAt")] string? LastReachedAt,
    [property: JsonPropertyName("offlineDurationMs")] long? OfflineDurationMs,
    [property: JsonPropertyName("isSecurityEvent")] bool IsSecurityEvent,
    [property: JsonPropertyName("errorCode")] string? ErrorCode)
{
    /// <summary>Projects a <see cref="SyncPeerSnapshot"/> onto the wire shape.</summary>
    public static SyncPeerWire From(SyncPeerSnapshot p) => new(
        DeviceId: p.DeviceId,
        Label: p.Label,
        State: SyncStatusResponse.ToWireState(p.State),
        LastReachedAt: p.LastReachedAt?.ToString("O"),
        OfflineDurationMs: p.OfflineDuration is { } d ? (long)d.TotalMilliseconds : null,
        IsSecurityEvent: p.IsSecurityEvent,
        ErrorCode: p.ErrorCode is { } code ? ErrorCodes.ToWire(code) : null);
}

/// <summary>WILL cadence wire shape.</summary>
/// <param name="NextRoundAt">ISO-8601 of the next scheduled round, or <c>null</c>.</param>
/// <param name="RoundIntervalSeconds">Configured periodic round interval.</param>
public sealed record SyncCadenceWire(
    [property: JsonPropertyName("nextRoundAt")] string? NextRoundAt,
    [property: JsonPropertyName("roundIntervalSeconds")] int RoundIntervalSeconds)
{
    /// <summary>Projects a <see cref="SyncCadence"/> onto the wire shape.</summary>
    public static SyncCadenceWire From(SyncCadence c) => new(
        NextRoundAt: c.NextRoundAt?.ToString("O"),
        RoundIntervalSeconds: c.RoundIntervalSeconds);
}

/// <summary>
/// MAJOR-1 — the enrollment-rebind health wire shape (cerebrum [2026-06-21] verdict). Rides on the sync-status
/// response so a faulted daemon-rebind (the silent half-rebound: a JOIN returned <c>200 {joined:true}</c> but the
/// node has NO running daemon on the joined team) is DETECTABLE on the app-facing surface — not just a log
/// line. <see cref="Complete"/> is true on boot + after every successful rebind; false when the most recent
/// rebind FAULTED, with the team it was joining and a degraded reason.
/// </summary>
/// <param name="Complete">True when the node's enrollment/rebind is settled (boot or a successful join-rebind);
/// false when the most recent rebind FAULTED and the node cannot sync on the joined team.</param>
/// <param name="JoinedTeamId">When not <see cref="Complete"/>, the team id the faulted rebind was joining (the
/// team the node now cannot serve); null when complete.</param>
/// <param name="Reason">When not <see cref="Complete"/>, a short PII-free degraded reason for the Harborline App /
/// diagnostics; null when complete.</param>
public sealed record EnrollmentWire(
    [property: JsonPropertyName("complete")] bool Complete,
    [property: JsonPropertyName("joinedTeamId")] string? JoinedTeamId,
    [property: JsonPropertyName("reason")] string? Reason)
{
    /// <summary>The healthy shape — no rebind has faulted. Reused for the null-rebind-status / pre-join case.</summary>
    public static readonly EnrollmentWire Healthy = new(Complete: true, JoinedTeamId: null, Reason: null);

    /// <summary>
    /// The "status not yet readable" degraded shape — the sync-status read-model could not be resolved/projected in
    /// the transient post-join-trigger / mid-rebind window (a disposed child provider, a still-constructing adopted-
    /// team daemon). <see cref="Complete"/> is false with a PII-free reason so the Harborline App renders a transient /
    /// degraded state and KEEPS POLLING (the readout recovers once the rebind settles), instead of treating an
    /// opaque 500 as the join failing. <paramref name="cause"/> is the exception TYPE name only (no message — no
    /// path/secret leak). This preserves the prior <c>complete:false</c> wire contract the Harborline App already reads.
    /// </summary>
    public EnrollmentWire AsUnreadable(string cause) => this with
    {
        Complete = false,
        Reason = $"sync_status_unreadable:{cause}",
    };

    /// <summary>Projects a <see cref="RebindOutcome"/> (or null ⇒ healthy) onto the wire shape.</summary>
    public static EnrollmentWire From(RebindOutcome? outcome)
    {
        if (outcome is null || outcome.IsHealthy)
        {
            return Healthy;
        }
        return new EnrollmentWire(
            Complete: false,
            JoinedTeamId: outcome.FaultedTeamId?.Value.ToString("D"),
            Reason: "daemon_rebind_failed");
    }
}
