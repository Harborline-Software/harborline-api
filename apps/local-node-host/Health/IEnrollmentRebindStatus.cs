namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// MAJOR-1 (cerebrum [2026-06-21] verdict) — a narrow, queryable view of whether the node's most recent
/// daemon-rebind (the active-team switch a wire-enrollment JOIN performs) completed successfully. The rebind runs
/// OFF the join's call stack, so a faulted rebind (incl. the BLOCKER-1 <c>EADDRINUSE</c> on the fixed production
/// <c>0.0.0.0:7473</c> bind) used to be undetectable: the join route had already returned <c>200 {joined:true}</c>
/// and the only signal was a log line, leaving the node silently active-team=A with NO running gossip daemon
/// (presenting no HELLO, unable to sync — the exact <c>PEER_UNTRUSTED</c> symptom this arc set out to fix).
/// </summary>
/// <remarks>
/// <para>
/// This is the SEAM that makes the half-rebound state LOUD. <see cref="LocalNodeWorker"/> implements it; the
/// sync-status route (<see cref="SyncStatusRoutes"/>) reads it to surface an <c>enrollment</c> degraded signal on
/// the app-facing surface, and the join route can re-read it after the rebind settles to qualify its response.
/// It is intentionally narrow (a single property) so the route does not take a hard dependency on the concrete
/// worker — the route + its tests close over this interface, not <see cref="LocalNodeWorker"/>.
/// </para>
/// </remarks>
public interface IEnrollmentRebindStatus
{
    /// <summary>
    /// The outcome of the most recent daemon-rebind. <see cref="RebindOutcome.IsHealthy"/> is true on boot (no
    /// rebind has run) and after every SUCCESSFUL rebind; it goes false (carrying the team the rebind was
    /// switching to + a fault summary) when a rebind FAULTED and the node cannot sync on the joined team.
    /// </summary>
    RebindOutcome LastRebind { get; }
}
