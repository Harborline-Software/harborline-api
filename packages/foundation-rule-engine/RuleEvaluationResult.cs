using System.Text.Json.Nodes;

using Harborline.Api.Foundation.RuleEngine.Model;

namespace Harborline.Api.Foundation.RuleEngine;

/// <summary>
/// The result of evaluating a whole form instance (SPINE-1 design §5.2). The raw
/// per-rule outcomes (keyed by rule key — <c>ruleId</c>, or <c>ruleId#rowId</c> for
/// a Row rule) plus form-facing views: computed values per cell, merged
/// visibility per cell, and the failing validations.
/// </summary>
public sealed class RuleEvaluationResult
{
    internal RuleEvaluationResult(
        IReadOnlyDictionary<string, RuleOutcome> byRule,
        IReadOnlyDictionary<string, ComputedValue> values,
        IReadOnlyDictionary<string, VisibilityState> visibility,
        IReadOnlyList<RuleOutcome> validations,
        bool hasPending,
        IReadOnlyDictionary<string, OptionsOutcome>? options = null)
    {
        ByRule = byRule;
        Values = values;
        Visibility = visibility;
        Validations = validations;
        HasPending = hasPending;
        Options = options ?? EmptyOptions;
    }

    private static readonly IReadOnlyDictionary<string, OptionsOutcome> EmptyOptions
        = new Dictionary<string, OptionsOutcome>();

    /// <summary>Every rule's outcome, keyed by rule key (<c>ruleId</c> / <c>ruleId#rowId</c>).</summary>
    public IReadOnlyDictionary<string, RuleOutcome> ByRule { get; }

    /// <summary>Computed cell values, keyed by <see cref="CellAddress.Key"/> (Compute + aggregate cells).</summary>
    public IReadOnlyDictionary<string, ComputedValue> Values { get; }

    /// <summary>Merged show/hide/required/readonly state per cell, keyed by <see cref="CellAddress.Key"/>.</summary>
    public IReadOnlyDictionary<string, VisibilityState> Visibility { get; }

    /// <summary>The failing <see cref="OutputType.Validity"/> outcomes (the save gate's blockers).</summary>
    public IReadOnlyList<RuleOutcome> Validations { get; }

    /// <summary>The available-options outcome per choice-field cell (<c>set-options</c> rules),
    /// keyed by <see cref="CellAddress.Key"/> (ADR 0146 D2 Wave-1). Empty when no options rules ran.</summary>
    public IReadOnlyDictionary<string, OptionsOutcome> Options { get; }

    /// <summary>
    /// True if any computed value is <see cref="ValueState.Pending"/>. On the .NET integrity
    /// tier a pending value at save is fail-closed: see <see cref="IsSaveBlocked"/>.
    /// </summary>
    public bool HasPending { get; }

    /// <summary>
    /// True iff a write must be blocked fail-closed (SPINE-1 §1.3, §5.3): any failing validity,
    /// any errored computed value, or any pending value reaching the synchronous integrity tier.
    /// </summary>
    public bool IsSaveBlocked =>
        Validations.Count > 0
        || HasPending
        || Values.Values.Any(v => v.State == ValueState.Error);
}
