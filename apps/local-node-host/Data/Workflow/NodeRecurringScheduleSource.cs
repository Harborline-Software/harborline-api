using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
/// general binding-backed path. The strict shared producer returns a complete in-window set or refuses;
/// a refused instance is logged and skipped for this tick without suppressing healthy instances.
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
    private readonly ILogger<NodeRecurringScheduleSource> _logger;

    /// <summary>Construct over the recoverable instance store + the RRULE expansion engine.</summary>
    public NodeRecurringScheduleSource(
        IDbContextFactory<LocalNodeDbContext> contextFactory,
        IRruleExpansionService rrule)
        : this(contextFactory, rrule, NullLogger<NodeRecurringScheduleSource>.Instance)
    {
    }

    /// <summary>Constructs the typed schedule source with its host logger.</summary>
    /// <param name="contextFactory">The recoverable instance context factory.</param>
    /// <param name="rrule">The strict shared recurrence producer.</param>
    /// <param name="logger">The host logger receiving per-instance expansion refusals.</param>
    public NodeRecurringScheduleSource(
        IDbContextFactory<LocalNodeDbContext> contextFactory,
        IRruleExpansionService rrule,
        ILogger<NodeRecurringScheduleSource> logger)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _rrule = rrule ?? throw new ArgumentNullException(nameof(rrule));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Construct over the instance store, RRULE engine, and re-admitting definition store.</summary>
    public NodeRecurringScheduleSource(
        IDbContextFactory<LocalNodeDbContext> contextFactory,
        IRruleExpansionService rrule,
        IWorkflowDefinitionExecutionStore definitions)
        : this(contextFactory, rrule, definitions, NullLogger<NodeRecurringScheduleSource>.Instance)
    {
    }

    /// <summary>Constructs the binding-backed schedule source with its host logger.</summary>
    /// <param name="contextFactory">The recoverable instance context factory.</param>
    /// <param name="rrule">The strict shared recurrence producer.</param>
    /// <param name="definitions">The re-admitting, exact-published-definition store.</param>
    /// <param name="logger">The host logger receiving per-instance expansion refusals.</param>
    public NodeRecurringScheduleSource(
        IDbContextFactory<LocalNodeDbContext> contextFactory,
        IRruleExpansionService rrule,
        IWorkflowDefinitionExecutionStore definitions,
        ILogger<NodeRecurringScheduleSource> logger)
        : this(contextFactory, rrule, logger)
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
            var instanceTriggers = new List<WorkflowTrigger>();

            if (instance.DefinitionKey == RecurringGenerationSteps.DefinitionKey)
            {
                if (AddTypedRecurringTriggers(instance, today, asOf, instanceTriggers))
                {
                    triggers.AddRange(instanceTriggers);
                }
                continue;
            }

            if (_definitions is not null
                && await AddDefinitionTriggersAsync(instance, today, asOf, instanceTriggers, ct).ConfigureAwait(false))
            {
                triggers.AddRange(instanceTriggers);
            }
        }

        ct.ThrowIfCancellationRequested();
        return triggers;
    }

    private bool AddTypedRecurringTriggers(
        WorkflowInstanceRecord instance,
        DateOnly today,
        DateTimeOffset at,
        List<WorkflowTrigger> triggers)
    {
        var schedule = TryParseSchedule(instance);
        if (schedule is null)
        {
            return true; // a malformed legacy instance is skipped, not fatal (the daemon must keep ticking).
        }

        // DTSTART remains the original anchor (phase and COUNT). Only the returned window advances
        // with the durable position. Inclusive re-yield of a completed occurrence preserves replay.
        var lowerBound = schedule.Value.StartsOn;
        if (RecurringGenerationSteps.IsGenerateStep(instance.CurrentStep)
            && DateOnly.TryParseExact(instance.CurrentStep[RecurringGenerationSteps.GeneratePrefix.Length..],
                "yyyy-MM-dd", out var currentOccurrence))
        {
            lowerBound = currentOccurrence;
        }
        if (!TryExpandOccurrences(instance.Id, schedule.Value.Rrule, schedule.Value.StartsOn,
            today, lowerBound, schedule.Value.Timezone, out var occurrences))
        {
            return false;
        }

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
        return true;
    }

    private async Task<bool> AddDefinitionTriggersAsync(
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
            return true; // typed handlers need not have a declarative definition record.
        }
        catch (WorkflowAdmissionException ex)
        {
            LogExpansionRefusal(instance.Id, ex);
            return false;
        }

        var definition = WorkflowDefinitionWireMapper.ToModel(
            record.Authored, record.Tenant, record.Key, record.Version);
        var schedules = definition.Triggers
            .Where(trigger => trigger.Kind == WorkflowTriggerKind.Schedule)
            .ToDictionary(trigger => trigger.Id, trigger => trigger.Rrule!, StringComparer.Ordinal);
        var anchor = DateOnly.FromDateTime(instance.CreatedAt.UtcDateTime);
        // UpdatedAt is the persisted last advance (CreatedAt before the first advance), not DTSTART.
        var lowerBound = DateOnly.FromDateTime(instance.UpdatedAt.UtcDateTime);

        foreach (var transition in definition.Transitions.Where(t => t.From == instance.CurrentStep))
        {
            if (!schedules.TryGetValue(transition.On, out var rrule))
            {
                continue;
            }

            if (!TryExpandOccurrences(instance.Id, rrule, anchor, today, lowerBound, "UTC", out var occurrences))
            {
                return false; // discard this instance's earlier candidates as well as this binding.
            }
            if (occurrences.Count > 0)
            {
                triggers.Add(WorkflowTrigger.For(
                    WorkflowTriggerKind.Schedule, instance.Id, instance.CurrentStep, at));
            }
        }
        return true;
    }

    private bool TryExpandOccurrences(
        string instanceId,
        string rrule,
        DateOnly anchor,
        DateOnly end,
        DateOnly lowerBound,
        string timezone,
        out IReadOnlyList<DateOnly> occurrences)
    {
        try
        {
            occurrences = _rrule.ExpandOccurrences(
                rrule, anchor, end, lookaheadDays: 0, leadDays: 0, today: lowerBound, timezone: timezone);
            return true;
        }
        catch (NotSupportedException ex)
        {
            LogExpansionRefusal(instanceId, ex);
        }
        catch (FormatException ex)
        {
            LogExpansionRefusal(instanceId, ex);
        }
        catch (global::Harborline.Foundation.Scheduling.RruleExpansionCapExceededException ex)
        {
            LogExpansionRefusal(instanceId, ex);
        }
        occurrences = Array.Empty<DateOnly>();
        return false;
    }

    private void LogExpansionRefusal(string instanceId, Exception exception)
        => _logger.LogWarning(exception,
            "Workflow schedule expansion refused for instance {InstanceId}; skipping this instance for this tick.",
            instanceId);

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
            if (string.IsNullOrWhiteSpace(rrule)) return null;
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
