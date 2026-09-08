namespace Harborline.Api.Blocks.Workflow.Durable;

/// <summary>
/// Supplies the <c>schedule</c>-triggered occurrences that are due as of a point in time (ADR 0135 D1 —
/// the daemon trigger). The schedule daemon (<c>WorkflowScheduleDaemon</c>, a <c>BackgroundService</c>)
/// polls this and dispatches each returned trigger through the <see cref="IWorkflowTriggerDispatcher"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The seam, not the schedule store.</b> Slice 1 ships the daemon + this seam so the <c>schedule</c>
/// trigger is real and exercised; the concrete recurring-generation source (RRULE expansion over the
/// recoverable schedule rows) is the slice-2 recurring handler's job. A v1 source can be as simple as
/// "every Running instance parked at a timer step whose due-time has passed", or a test fixture that
/// yields a fixed set — the daemon treats them identically.
/// </para>
/// <para>
/// <b>Idempotency is the dispatcher's job, not the source's.</b> The source MAY over-report (yield a
/// trigger that already advanced) — the dispatcher's idempotency guard makes a redelivered trigger a
/// no-op (<see cref="WorkflowDispatchResult.ReplayedNoOp"/>). So the source can be at-least-once without
/// risking a double-effect.
/// </para>
/// </remarks>
public interface IWorkflowScheduleSource
{
    /// <summary>
    /// Returns the schedule-triggered occurrences due as of <paramref name="asOf"/>. May be empty. May
    /// over-report (the dispatcher dedups).
    /// </summary>
    Task<IReadOnlyList<WorkflowTrigger>> GetDueTriggersAsync(DateTimeOffset asOf, CancellationToken ct = default);
}
