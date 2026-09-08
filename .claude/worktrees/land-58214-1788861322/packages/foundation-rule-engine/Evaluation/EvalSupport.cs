using System.Text.Json.Nodes;

using Harborline.Api.Foundation.RuleEngine.Model;

namespace Harborline.Api.Foundation.RuleEngine.Evaluation;

/// <summary>The resolution of a referenced cell handed to the evaluator. Public as the value
/// vocabulary an <see cref="Context.IContextAdapter"/> resolver speaks (ADR 0146 D3).</summary>
public readonly struct RefValue
{
    private RefValue(ValueState state, JsonNode? value, RuleError? error)
    {
        State = state;
        Value = value;
        Error = error;
    }

    public ValueState State { get; }
    public JsonNode? Value { get; }
    public RuleError? Error { get; }

    public static RefValue Resolved(JsonNode? value) => new(ValueState.Resolved, value, null);
    public static RefValue OfError(RuleError error) => new(ValueState.Error, null, error);
    public static RefValue Pending { get; } = new(ValueState.Pending, null, null);

    /// <summary>The ONE bad-aggregate refusal shape in this tier (ticket 162): an aggregate the
    /// evaluation context cannot provide refuses with <c>rule.bad_reference</c> carrying the
    /// address params <c>{agg: "section/fn/col"}</c>. EVERY resolver in this tier constructs it
    /// HERE (the form graph's CellResolver, the guard evaluator's ContextBagResolver, and the
    /// document-merge adapter), so the param order cannot drift between them; the corpus case
    /// <c>Conformance/corpus/aggregate-refusal.json</c> pins the shape, and the
    /// harborline-platform rule-runtime twins (TS and .NET) build the identical one.</summary>
    public static RefValue UnavailableAggregate(string fn, string section, string col)
        => OfError(RuleError.Of(RuleEngineCodes.BadReference, "agg", section + "/" + fn + "/" + col));
}

/// <summary>
/// Resolves <c>var</c> / <c>agg</c> references for one rule evaluation against the
/// already-evaluated upstream cells + raw instance values (SPINE-1 design §2.2). Public as
/// the resolver an <see cref="Context.IContextAdapter"/> produces for its pillar data (ADR 0146 D3).
/// </summary>
public interface IValueResolver
{
    /// <summary>Resolves a canonical var path (<c>field.x</c> / <c>row.y</c> / <c>parent.f</c>).</summary>
    RefValue ResolveVar(string path);

    /// <summary>Resolves a table aggregate (<c>fn</c> over <c>section</c>.<c>col</c>).</summary>
    RefValue ResolveAgg(string fn, string section, string col);
}

/// <summary>
/// The shared step budget + wall-clock token for one whole-instance evaluation
/// (SPINE-1 design §4). One <see cref="EvalBudget"/> spans every cell + rule in the
/// instance so the 250k-op budget is a per-instance total, not per-rule.
/// </summary>
internal sealed class EvalBudget
{
    public EvalBudget(RuleEngineLimits limits, CancellationToken ct)
    {
        Limits = limits;
        CancellationToken = ct;
    }

    public RuleEngineLimits Limits { get; }
    public CancellationToken CancellationToken { get; }

    /// <summary>Steps consumed so far across the whole instance evaluation.</summary>
    public int Steps { get; private set; }

    /// <summary>Charges one step. The op-budget is the authoritative, outcome-affecting bound (fails
    /// closed with <see cref="RuleBudgetException"/>). The wall-clock / cancellation token is a
    /// non-authoritative liveness guard: it throws <see cref="RuleEngineTimeoutException"/>, which
    /// propagates as an infrastructure fault and NEVER becomes a divergent evaluation outcome
    /// (D1 ratification 2026-07-01).</summary>
    public void Charge()
    {
        if (CancellationToken.IsCancellationRequested) throw new RuleEngineTimeoutException();
        if (++Steps > Limits.StepBudget) throw new RuleBudgetException();
    }

    /// <summary>Charges <paramref name="size"/> steps proportional to a large operand's size — string
    /// concat and money arithmetic do O(size) … super-linear work for ONE AST node, so a single big
    /// operand must be charged proportionally rather than the flat one step (finding F4). Fails closed
    /// identically to <see cref="Charge"/>; the per-step add is clamped so it cannot overflow.</summary>
    public void ChargeSize(int size)
    {
        if (CancellationToken.IsCancellationRequested) throw new RuleEngineTimeoutException();
        Steps += size > Limits.StepBudget ? Limits.StepBudget + 1 : (size < 1 ? 1 : size);
        if (Steps > Limits.StepBudget) throw new RuleBudgetException();
    }
}

/// <summary>Per-cell evaluation state: the shared <see cref="EvalBudget"/> + this cell's resolver + clock.</summary>
internal sealed class EvalContext
{
    public EvalContext(IValueResolver resolver, DateTimeOffset now, EvalBudget budget)
    {
        Resolver = resolver;
        Now = now;
        Budget = budget;
    }

    public IValueResolver Resolver { get; }
    public DateTimeOffset Now { get; }
    public EvalBudget Budget { get; }

    /// <summary>Charges one step against the shared budget; fails closed on overflow/cancellation.</summary>
    public void Charge() => Budget.Charge();
}

/// <summary>A recoverable evaluation error that becomes a <c>ComputedValue.Error</c> / <c>Validity</c> failure.</summary>
internal sealed class RuleEvalException : Exception
{
    public RuleEvalException(RuleError error) => Error = error;
    public RuleError Error { get; }
}

/// <summary>Signals a referenced cell is <c>Pending</c> — the whole outcome becomes Pending.</summary>
internal sealed class RulePendingException : Exception;

/// <summary>Signals the per-instance step budget was exhausted — the authoritative, deterministic,
/// outcome-affecting bound (becomes a fail-closed <c>rule.budget_exceeded</c> result).</summary>
internal sealed class RuleBudgetException : Exception;
