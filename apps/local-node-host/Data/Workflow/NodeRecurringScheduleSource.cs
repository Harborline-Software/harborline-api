using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Scheduling;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Workflow;

/// <summary>
/// The production <see cref="IWorkflowScheduleSource"/> (ADR 0135 D1 — the <c>schedule</c> trigger) for the
/// node. Declarative instances resolve their D7-pinned definition, read the RRULE from the reachable
/// <see cref="WorkflowTriggerBindingDef"/>, and emit a trigger when an occurrence is due. The typed
/// recurring-generation compatibility path expands its domain schedule into one
/// <c>generate@{occurrenceDate}</c> trigger per due occurrence.
/// </summary>
/// <remarks>
/// <para>
/// <b>At-least-once by design.</b> The source MAY over-report — it re-yields a due binding on every tick.
/// The dispatcher's per-step idempotency guard turns a re-yield of an already-advanced step into a no-op
/// (<see cref="WorkflowDispatchResult.ReplayedNoOp"/>), so the source can be simple + stateless.
/// </para>
/// <para>
/// <b>Deliberate typed compatibility case.</b> <c>recurring-generation</c> predates persisted declarative
/// definitions and has no definition record to load. Its domain schedule also supplies an explicit start date
/// and timezone that <see cref="WorkflowTriggerBindingDef"/> does not model. It therefore continues to read
/// those scheduling coordinates from instance state; every authored declarative definition routes through the
/// general binding-backed path. RRULE expansion is bounded (≤1000 occurrences/call) by the foundation service.
/// </para>
/// <para>
/// <b>SC4-C2.</b> Instance reads use the recoverable <see cref="LocalNodeDbContext"/> and definition reads use
/// the admitted workflow-definition store; the RRULE service is pure/in-memory. No kernel CRDT writer /
/// per-team event log is reachable.
/// </para>
/// </remarks>
public sealed class NodeRecurringScheduleSource : IWorkflowScheduleSource
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;
    private readonly IRruleExpansionService _rrule;
    private readonly IWorkflowDefinitionExecutionStore? _definitions;

    /// <summary>Construct over the recoverable instance store + the RRULE expansion engine.</summary>
    public NodeRecurringScheduleSource(
        IDbContextFactory<LocalNodeDbContext> contextFactory,
        IRruleExpansionService rrule)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _rrule = rrule ?? throw new ArgumentNullException(nameof(rrule));
    }

    /// <summary>Construct over the instance store, RRULE engine, and re-admitting definition store.</summary>
    public NodeRecurringScheduleSource(
        IDbContextFactory<LocalNodeDbContext> contextFactory,
        IRruleExpansionService rrule,
        IWorkflowDefinitionExecutionStore definitions)
        : this(contextFactory, rrule)
    {
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkflowTrigger>> GetDueTriggersAsync(
        DateTimeOffset asOf, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Only Running instances participate — a Completed/Failed/Parked instance does not auto-advance.
        var instances = await ctx.Set<WorkflowInstanceRecord>()
            .AsNoTracking()
            .Where(i => i.Status == WorkflowStatus.Running)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var today = DateOnly.FromDateTime(asOf.UtcDateTime);
        var triggers = new List<WorkflowTrigger>();

        foreach (var instance in instances)
        {
            ct.ThrowIfCancellationRequested();

            if (instance.DefinitionKey == RecurringGenerationSteps.DefinitionKey)
            {
                AddTypedRecurringTriggers(instance, today, asOf, triggers);
                continue;
            }

            if (_definitions is not null)
            {
                await AddDefinitionTriggersAsync(instance, today, asOf, triggers, ct).ConfigureAwait(false);
            }
        }

        return triggers;
    }

    private void AddTypedRecurringTriggers(
        WorkflowInstanceRecord instance,
        DateOnly today,
        DateTimeOffset at,
        List<WorkflowTrigger> triggers)
    {
        var schedule = TryParseSchedule(instance);
        if (schedule is null)
        {
            return; // a malformed legacy instance is skipped, not fatal (the daemon must keep ticking).
        }

        // Expand every occurrence from the schedule start through `today` (the DUE / past-or-today set).
        // The RRULE service's leadDays filter skips occurrences earlier than `today + leadDays`, so to
        // KEEP past-due occurrences we anchor the lead at the schedule START (today: StartsOn, leadDays: 0)
        // and cap the upper bound at the real `today` via `end`. We then emit only occurrences whose date
        // has arrived (<= today) — a future-dated occurrence comes due on a LATER tick.
        var occurrences = _rrule.ExpandOccurrences(
            rrule: schedule.Value.Rrule,
            start: schedule.Value.StartsOn,
            end: today,
            lookaheadDays: 0,
            leadDays: 0,
            today: schedule.Value.StartsOn,
            timezone: schedule.Value.Timezone);

        foreach (var occurrence in occurrences)
        {
            if (occurrence > today)
            {
                continue; // never advance a future occurrence ahead of its date.
            }

            triggers.Add(WorkflowTrigger.For(
                WorkflowTriggerKind.Schedule,
                instance.Id,
                RecurringGenerationSteps.GenerateStep(occurrence),
                at));
        }
    }

    private async Task AddDefinitionTriggersAsync(
        WorkflowInstanceRecord instance,
        DateOnly today,
        DateTimeOffset at,
        List<WorkflowTrigger> triggers,
        CancellationToken ct)
    {
        WorkflowDefinitionRecord record;
        try
        {
            record = await _definitions!
                .GetAdmittedAsync(new DefinitionCoordinates(
                    new TenantId(instance.TenantId),
                    instance.DefinitionKey,
                    instance.DefinitionVersion), ct)
                .ConfigureAwait(false);
        }
        catch (WorkflowDefinitionNotFoundException)
        {
            return; // typed handlers need not have a declarative definition record.
        }

        var definition = WorkflowDefinitionWireMapper.ToModel(
            record.Authored, record.Tenant, record.Key, record.Version);
        var schedules = definition.Triggers
            .Where(trigger => trigger.Kind == WorkflowTriggerKind.Schedule)
            .ToDictionary(trigger => trigger.Id, trigger => trigger.Rrule!, StringComparer.Ordinal);
        var anchor = DateOnly.FromDateTime(instance.CreatedAt.UtcDateTime);

        foreach (var transition in definition.Transitions.Where(t => t.From == instance.CurrentStep))
        {
            if (!schedules.TryGetValue(transition.On, out var rrule))
            {
                continue;
            }

            var occurrences = _rrule.ExpandOccurrences(
                rrule, anchor, today, lookaheadDays: 0, leadDays: 0, today: anchor, timezone: "UTC");
            if (occurrences.Count > 0)
            {
                triggers.Add(WorkflowTrigger.For(
                    WorkflowTriggerKind.Schedule, instance.Id, instance.CurrentStep, at));
            }
        }
    }

    private static ScheduleState? TryParseSchedule(WorkflowInstanceRecord instance)
    {
        if (string.IsNullOrWhiteSpace(instance.StateJson) || instance.StateJson == "{}")
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(instance.StateJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("rrule", out var rruleEl) || rruleEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            var rrule = rruleEl.GetString()!;
            var startsOn = root.TryGetProperty("startsOn", out var s) && s.ValueKind == JsonValueKind.String
                ? DateOnly.ParseExact(s.GetString()!, "yyyy-MM-dd")
                : DateOnly.FromDateTime(instance.CreatedAt.UtcDateTime);
            var timezone = root.TryGetProperty("timezone", out var tz) && tz.ValueKind == JsonValueKind.String
                ? tz.GetString()!
                : "UTC";
            return new ScheduleState(rrule, startsOn, timezone);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private readonly record struct ScheduleState(string Rrule, DateOnly StartsOn, string Timezone);
}
