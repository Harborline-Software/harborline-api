using System.Globalization;
using System.Text.Json;

namespace Harborline.Api.Blocks.Workflow.Durable;

// ─────────────────────────────────────────────────────────────────────────────
//  Handler B (ADR 0135 v1) — recurring generation on the `schedule` trigger.
//
//  The second typed v1 handler. Generalizes the existing NodeEfRecurringInvoiceService
//  shape onto the engine: a `schedule` trigger (the daemon) advances a recurring
//  instance one occurrence, REUSING the merged bug-1337 deterministic
//  (scheduleId, occurrenceDate) derivation + the engine's atomic advance (no
//  double-post on resume).
//
//  AP-by-design (ADR 0135 §Prerequisites R3): the `schedule` trigger fires this
//  NON-CP automated step autonomously, with no human — that is what a durable engine
//  IS. The recurring generation step posts a balanced JE INTO AN OPEN PERIOD; it is
//  NOT a period-close / period-lock step. The CP `period-touching step` the ADR names
//  for the CP-park gate is the period-CLOSE class, which this handler NEVER auto-runs:
//  RecurringGenerationSteps.PeriodClose is a human-task-park step (arch-tested by name),
//  exactly mirroring Handler A's CP post-park.
//
//  Crash-resume integrity (build invariant #1 + #2): each occurrence's effect rides
//  the engine's atomic advance, and the host generation derives a deterministic JE id
//  from (scheduleId, occurrenceDate) so a re-run produces the IDENTICAL SourceReference
//  (the JE unique index is a deterministic backstop). The engine's idempotency key is
//  per (instance, iteration, generate-step) so a redelivered schedule tick is a no-op.
//
//  Financial-free: the generation effect is supplied by the host via
//  IRecurringGenerationContext, which delegates to the node recurring-invoice service.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The step identifiers + definition key for the recurring-generation process. Public so the host wiring,
/// the schedule source, and the arch-tests reference the SAME names (no string drift).
/// </summary>
public static class RecurringGenerationSteps
{
    /// <summary>The definition key the dispatcher matches an instance's <c>DefinitionKey</c> against.</summary>
    public const string DefinitionKey = "recurring-generation";

    /// <summary>
    /// The AP generation step PREFIX — a schedule tick targets <c>generate@{occurrenceDate}</c>, so each
    /// occurrence is a distinct step and therefore a distinct engine idempotency key
    /// (<c>(instance, 0, generate@2026-07-01)</c>) — a redelivered tick for the SAME occurrence hits the
    /// dispatcher's idempotency guard and is a no-op (at-least-once safe), while the NEXT occurrence advances
    /// the same instance. Runs autonomously (ADR 0135 R3) — non-CP by design. Posts the occurrence's balanced
    /// JE into an OPEN period; it is NOT a period-close step.
    /// </summary>
    public const string GeneratePrefix = "generate@";

    /// <summary>Builds the occurrence-specific generate step id (<c>generate@yyyy-MM-dd</c>).</summary>
    public static string GenerateStep(DateOnly occurrenceDate)
        => GeneratePrefix + occurrenceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>True when <paramref name="step"/> is an occurrence-specific generate step.</summary>
    public static bool IsGenerateStep(string step)
        => step is not null && step.StartsWith(GeneratePrefix, StringComparison.Ordinal);

    /// <summary>
    /// The CP period-CLOSE step — locking a period is a controlled, non-monotonic operation (a merge can
    /// resurrect a posting into a closed period, ADR 0135 D3). This handler NEVER auto-runs it; it PARKS on a
    /// human-task (arch-tested by name — the recurring handler's CP-park gate).
    /// </summary>
    public const string PeriodClose = "period-close";
}

/// <summary>
/// The host-supplied surface the <see cref="RecurringGenerationHandler"/> uses to stay financial-free: it
/// runs one occurrence's generation (the deterministic-id, atomic-post path) and reports whether anything was
/// generated. The host implements this over the node recurring-invoice service.
/// </summary>
public interface IRecurringGenerationContext
{
    /// <summary>
    /// Builds the generation <see cref="WorkflowEffect"/> for <paramref name="instance"/>'s
    /// <paramref name="occurrenceDate"/> at <paramref name="generateStepKey"/>. The effect stages the
    /// occurrence's balanced JE onto the advance's in-flight unit-of-work, reusing the deterministic
    /// <c>(scheduleId, occurrenceDate)</c> derivation (bug-1337) so a crash-resume re-derives the IDENTICAL
    /// JE id / SourceReference (no double-post). Returns <see langword="null"/> when there is nothing to post
    /// for the occurrence (the advance is then a pure position move).
    /// </summary>
    WorkflowEffect? BuildGenerationEffect(
        WorkflowInstanceRecord instance,
        DateOnly occurrenceDate,
        WorkflowStepKey generateStepKey,
        DateTimeOffset at);
}

/// <summary>
/// Handler B — recurring generation on the <c>schedule</c> trigger (ADR 0135 v1). See the file header.
/// </summary>
public sealed class RecurringGenerationHandler : IWorkflowStepHandler
{
    private readonly IRecurringGenerationContext _context;

    /// <summary>Constructs the handler over the host generation surface.</summary>
    public RecurringGenerationHandler(IRecurringGenerationContext context)
        => _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public string DefinitionKey => RecurringGenerationSteps.DefinitionKey;

    /// <inheritdoc />
    public ValueTask<WorkflowStepOutcome> DecideAsync(
        WorkflowInstanceRecord instance,
        WorkflowTrigger trigger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        // The CP period-close step NEVER auto-executes — it parks on a human-task (the CP-park gate). A
        // schedule/dependency trigger that targets it is held for human action, never auto-run.
        if (trigger.Step == RecurringGenerationSteps.PeriodClose)
        {
            return ValueTask.FromResult(WorkflowStepOutcome.Park(
                RecurringGenerationSteps.PeriodClose,
                "{\"kind\":\"cp-period-close\",\"reason\":\"period close is a controlled, human-gated step (ADR 0135 D3)\"}"));
        }

        // An occurrence-specific generate step (generate@yyyy-MM-dd) — the AP autonomous case.
        if (RecurringGenerationSteps.IsGenerateStep(trigger.Step))
        {
            return ValueTask.FromResult(Generate(instance, trigger));
        }

        throw new InvalidOperationException(
            $"{nameof(RecurringGenerationHandler)} received a trigger for unknown step '{trigger.Step}' " +
            $"(instance '{instance.Id}').");
    }

    /// <summary>
    /// The AP generate step for one occurrence — a schedule tick advances the instance one occurrence. The
    /// host effect stages the occurrence's balanced JE atomically with the advance (build invariant #1); the
    /// deterministic id derivation (bug-1337) makes a crash-resume re-run the IDENTICAL JE (build invariant
    /// #2). The idempotency key is <c>(instance, 0, generate@{date})</c> — distinct per occurrence, so each
    /// occurrence advances once and a redelivered tick is a no-op. Stays Running (the instance is long-lived).
    /// </summary>
    private WorkflowStepOutcome Generate(WorkflowInstanceRecord instance, WorkflowTrigger trigger)
    {
        var generateStep = trigger.Step;
        var occurrence = ParseOccurrence(generateStep);
        // Each occurrence is its OWN generate@{date} step (a distinct step id), so the occurrence dimension —
        // not the iteration — is what keeps occurrences from colliding. The iteration tracks the instance's
        // durable counter (0 here — recurring generation has no loop-back); using it keeps the effect key
        // consistent with the dispatcher's advance key.
        var generateKey = new WorkflowStepKey(instance.Id, instance.Iteration, generateStep);
        var effect = _context.BuildGenerationEffect(instance, occurrence, generateKey, trigger.At);

        var eventData = JsonSerializer.Serialize(new
        {
            kind = "recurring-occurrence-generated",
            occurrenceDate = occurrence.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            generated = effect is not null,
        });

        // Stay on this occurrence's generate step (Running) — the recurring instance is long-lived; the NEXT
        // occurrence arrives as its OWN generate@{date} step (a distinct idempotency key), advancing the same
        // instance again. The effect (when due) posts the occurrence's JE atomically.
        return WorkflowStepOutcome.Advance(
            nextStep: generateStep,
            effect: effect,
            resultJson: eventData,
            eventType: "OccurrenceGenerated",
            eventDataJson: eventData,
            nextStatus: WorkflowStatus.Running);
    }

    private static DateOnly ParseOccurrence(string generateStep)
    {
        var datePart = generateStep[RecurringGenerationSteps.GeneratePrefix.Length..];
        if (!DateOnly.TryParseExact(datePart, "yyyy-MM-dd", out var occurrence))
        {
            throw new InvalidOperationException(
                $"Recurring generate step '{generateStep}' does not carry a yyyy-MM-dd occurrence date.");
        }
        return occurrence;
    }
}
