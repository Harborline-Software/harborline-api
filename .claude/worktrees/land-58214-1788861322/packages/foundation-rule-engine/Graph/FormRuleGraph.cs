using System.Text.Json.Nodes;

using Harborline.Api.Foundation.RuleEngine.Compilation;
using Harborline.Api.Foundation.RuleEngine.Context;
using Harborline.Api.Foundation.RuleEngine.Evaluation;
using Harborline.Api.Foundation.RuleEngine.Model;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.RuleEngine.Graph;

/// <summary>
/// The form orchestrator (SPINE-1 design §2, §5.2). Builds the instance dependency
/// graph (addressable-cell DAG), topo-sorts it (Kahn), evaluates value cells with
/// Error/Pending propagation, exposes per-rule outcomes + form-facing views, and
/// reactively re-evaluates the transitive-dependent set on a single-cell change or a
/// child-table row add/remove.
/// </summary>
public sealed class FormRuleGraph : IFormRuleGraph
{
    private readonly RuleEngineLimits _limits;
    private readonly TimeProvider _clock;

    private RuleInstance _instance = new();

    // Build artifacts (rebuilt on structural change).
    private readonly List<ComputedCell> _order = new();              // topo-ordered computed cells
    private readonly Dictionary<string, ComputedCell> _cells = new();
    private readonly Dictionary<string, HashSet<string>> _deps = new();      // cell -> its dependency cells
    private readonly Dictionary<string, HashSet<string>> _dependents = new();// cell -> computed cells depending on it
    // Broad reverse index: ANY referenced cell key (raw field OR computed) -> computed cells that read it.
    // Used by the reactive path so a change to a RAW field still dirties its dependents.
    private readonly Dictionary<string, HashSet<string>> _readers = new();
    private readonly List<OutcomePlan> _plans = new();

    // Last evaluation state (mutated incrementally).
    private readonly Dictionary<string, ComputedValue> _values = new();
    private readonly Dictionary<string, RuleOutcome> _outcomes = new();

    public FormRuleGraph(CompiledGraph compiled, RuleEngineLimits? limits = null, TimeProvider? clock = null)
    {
        Compiled = compiled ?? throw new ArgumentNullException(nameof(compiled));
        _limits = limits ?? RuleEngineLimits.Default;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public CompiledGraph Compiled { get; }

    /// <inheritdoc />
    public RuleEvaluationResult EvaluateInstance(RuleInstance instance, CancellationToken ct = default)
    {
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
        return BuildAndEvaluate(ct);
    }

    /// <inheritdoc />
    public RuleEvaluationResult Reevaluate(string fieldName, JsonNode? newValue, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fieldName);
        _instance.Fields[fieldName] = newValue?.DeepClone();
        string changedKey = CellAddress.Field(fieldName).Key;

        // Transitive dependents of the changed (possibly raw) cell, via the broad reader index.
        var dirty = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(changedKey);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (_readers.TryGetValue(node, out var readers))
            {
                foreach (var d in readers)
                {
                    if (dirty.Add(d)) queue.Enqueue(d);
                }
            }
        }

        var budget = NewBudget(ct);
        var adapter = new FormContextAdapter(_values, _instance); // ADR 0146 D3 context seam
        string? inFlightRule = null; // ticket 150: the rule being evaluated when a budget abort trips
        string? inFlightCell = null; // …or the aggregate cell, when the in-flight cell has no rule
        try
        {
            // Re-eval only the dirty computed cells, in topo order.
            foreach (var cell in _order.Where(c => dirty.Contains(c.Key)))
            {
                inFlightRule = cell.Rule?.Source.Id;
                inFlightCell = cell.Rule is null ? cell.Key : null;
                _values[cell.Key] = EvalComputedCell(cell, budget, adapter);
            }
            // Re-eval only the outcomes that read the changed field or a dirty cell (referential stability elsewhere).
            var touched = new HashSet<string>(dirty) { changedKey };
            foreach (var plan in _plans.Where(p => p.Reads.Overlaps(touched) || dirty.Contains(p.Target.Key)))
            {
                inFlightRule = plan.Rule.Source.Id;
                inFlightCell = null;
                _outcomes[plan.Key] = BuildOutcome(plan, budget, adapter);
            }
        }
        catch (RuleBudgetException) { return FailClosed(RuleEngineCodes.BudgetExceeded, inFlightRule, inFlightCell); }
        // RuleEngineTimeoutException is a non-authoritative liveness fault (D1 ratification): it is NOT
        // caught here — it propagates as an infrastructure exception so the wall-clock can never emit a
        // divergent evaluation outcome. The op-budget above is the sole authoritative fail-closed bound.

        return Project();
    }

    /// <inheritdoc />
    public RuleEvaluationResult AddRow(string section, RuleRow row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(row);
        if (!_instance.Tables.TryGetValue(section, out var rows)) _instance.Tables[section] = rows = new List<RuleRow>();
        rows.Add(row);
        return BuildAndEvaluate(ct); // structural change: rebuild graph + re-link aggregates
    }

    /// <inheritdoc />
    public RuleEvaluationResult RemoveRow(string section, string rowId, CancellationToken ct = default)
    {
        if (_instance.Tables.TryGetValue(section, out var rows)) rows.RemoveAll(r => r.Id == rowId);
        return BuildAndEvaluate(ct);
    }

    // ── build + full evaluate ────────────────────────────────────────────────

    private EvalBudget NewBudget(CancellationToken ct)
    {
        // The .NET integrity tier's authoritative wall-clock backstop: a linked CTS with the ceiling.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_limits.WallClockCeiling);
        return new EvalBudget(_limits, cts.Token);
    }

    private RuleEvaluationResult BuildAndEvaluate(CancellationToken ct)
    {
        var preflight = PreflightBounds();
        if (preflight is { } refusal) return FailClosed(refusal.Code, refusal.RuleId);

        Build();

        if (_cells.Count > _limits.MaxGraphNodes)
        {
            return FailClosed(RuleEngineCodes.GraphTooLarge);
        }

        var budget = NewBudget(ct);
        _values.Clear();
        _outcomes.Clear();
        var adapter = new FormContextAdapter(_values, _instance); // ADR 0146 D3 context seam
        string? inFlightRule = null; // ticket 150: the rule being evaluated when a budget abort trips
        string? inFlightCell = null; // …or the aggregate cell, when the in-flight cell has no rule
        try
        {
            foreach (var cell in _order)
            {
                inFlightRule = cell.Rule?.Source.Id;
                inFlightCell = cell.Rule is null ? cell.Key : null;
                _values[cell.Key] = EvalComputedCell(cell, budget, adapter);
            }
            foreach (var plan in _plans)
            {
                inFlightRule = plan.Rule.Source.Id;
                inFlightCell = null;
                _outcomes[plan.Key] = BuildOutcome(plan, budget, adapter);
            }
        }
        catch (RuleBudgetException) { return FailClosed(RuleEngineCodes.BudgetExceeded, inFlightRule, inFlightCell); }
        // RuleEngineTimeoutException is a non-authoritative liveness fault (D1 ratification 2026-07-01):
        // it is NOT caught here — it propagates as an infrastructure exception so the wall-clock can never
        // emit a divergent evaluation outcome. The op-budget above is the sole authoritative fail-closed bound.

        return Project();
    }

    private (string Code, string? RuleId)? PreflightBounds()
    {
        // Bound the otherwise un-budgeted graph CONSTRUCTION (finding F3): project the cell count
        // BEFORE Build() expands Row rules across every (user-controlled) instance row + Kahn-sorts.
        // Also reject a Compute rule that addresses no form cell — Section/Schema scope — as a
        // fail-closed config error rather than a silent no-op (finding F8). (Guards use the shared
        // compiler directly, not this form graph, so their Schema-scoped Compute is unaffected.)
        // Ticket 150 (review): a refusal the loop can attribute NAMES the tripping rule.
        long projectedCells = 0;
        var aggCells = new HashSet<string>();
        foreach (var rule in Compiled.Rules)
        {
            if (rule.Source.Action == RuleActionKind.Compute)
            {
                if (rule.Source.Scope is not (RuleScope.Field or RuleScope.Row or RuleScope.Table))
                    return (RuleEngineCodes.ComputeScopeInvalid, rule.Source.Id);
                projectedCells += rule.Source.Scope == RuleScope.Row ? RowsOf(rule.RowSection!).Count : 1;
            }
            foreach (var rf in rule.References)
                if (rf is AggRef a) aggCells.Add(CellAddress.TableAggregate(a.Section, a.Fn, a.Col).Key);
            if (projectedCells > _limits.MaxGraphNodes) return (RuleEngineCodes.GraphTooLarge, rule.Source.Id);
        }
        return projectedCells + aggCells.Count > _limits.MaxGraphNodes
            ? (RuleEngineCodes.GraphTooLarge, null)
            : null;
    }

    private void Build()
    {
        _cells.Clear();
        _deps.Clear();
        _dependents.Clear();
        _readers.Clear();
        _order.Clear();
        _plans.Clear();

        // 1. Compute cells (rule-driven) + their outcome plans; 2. implicit aggregate cells.
        foreach (var rule in Compiled.Rules)
        {
            if (rule.Source.Action == RuleActionKind.Compute)
            {
                AddComputeCellsAndPlans(rule);
            }
            else
            {
                AddNonComputePlans(rule);
            }

            // Aggregate cells referenced anywhere become implicit fold cells (unless a Table-compute defines them).
            foreach (var rf in rule.References)
            {
                if (rf is AggRef a)
                {
                    var addr = CellAddress.TableAggregate(a.Section, a.Fn, a.Col);
                    if (!_cells.ContainsKey(addr.Key))
                    {
                        _cells[addr.Key] = new ComputedCell(addr.Key, addr, null, null, AggFold: (a.Fn, a.Section, a.Col));
                    }
                }
            }
        }

        // 3. Edges.
        foreach (var cell in _cells.Values)
        {
            _deps.TryAdd(cell.Key, new HashSet<string>());
            _dependents.TryAdd(cell.Key, new HashSet<string>());
        }
        foreach (var cell in _cells.Values)
        {
            if (cell.AggFold is { } fold)
            {
                // Aggregate depends on each row's column cell. Bound this un-budgeted edge walk:
                // a table over the per-aggregate row cap fails closed in FoldAggregate, so don't
                // pre-walk all its (user-controlled) rows here (finding F3).
                if (_instance.Tables.TryGetValue(fold.Section, out var rows)
                    && rows.Count <= _limits.MaxTableRowsPerAggregate)
                {
                    foreach (var row in rows)
                    {
                        var rowKey = CellAddress.Row(fold.Section, row.Id, fold.Col).Key;
                        AddReader(rowKey, cell.Key);
                        if (_cells.ContainsKey(rowKey)) Edge(cell.Key, rowKey);
                    }
                }
            }
            else if (cell.Rule is { } cr)
            {
                foreach (var rf in cr.References)
                {
                    var depKey = ResolveRefKey(rf, cr, cell.RowId);
                    if (depKey is not null)
                    {
                        AddReader(depKey, cell.Key);
                        if (_cells.ContainsKey(depKey)) Edge(cell.Key, depKey);
                    }
                }
            }
        }

        // 4. Topological order (Kahn); cells that remain are in a (defensive) cycle.
        TopoSort();
    }

    private void AddComputeCellsAndPlans(CompiledRule rule)
    {
        switch (rule.Source.Scope)
        {
            case RuleScope.Field:
            {
                var addr = CellAddress.Field(rule.Source.ScopeTarget);
                _cells[addr.Key] = new ComputedCell(addr.Key, addr, rule, null, null);
                _plans.Add(MakePlan(rule, addr, null));
                break;
            }
            case RuleScope.Row:
            {
                foreach (var row in RowsOf(rule.RowSection!))
                {
                    var addr = CellAddress.Row(rule.RowSection!, row.Id, rule.RowField!);
                    _cells[addr.Key] = new ComputedCell(addr.Key, addr, rule, row.Id, null);
                    _plans.Add(MakePlan(rule, addr, row.Id));
                }
                break;
            }
            case RuleScope.Table:
            {
                var addr = rule.StaticTarget!.Value;
                _cells[addr.Key] = new ComputedCell(addr.Key, addr, rule, null, null);
                _plans.Add(MakePlan(rule, addr, null));
                break;
            }
        }
    }

    private void AddNonComputePlans(CompiledRule rule)
    {
        switch (rule.Source.Scope)
        {
            case RuleScope.Row:
                foreach (var row in RowsOf(rule.RowSection!))
                {
                    var addr = CellAddress.Row(rule.RowSection!, row.Id, rule.RowField!);
                    _plans.Add(MakePlan(rule, addr, row.Id));
                }
                break;
            default:
                _plans.Add(MakePlan(rule, rule.StaticTarget!.Value, null));
                break;
        }
    }

    private OutcomePlan MakePlan(CompiledRule rule, CellAddress target, string? rowId)
    {
        string key = rowId is null ? rule.Source.Id : rule.Source.Id + "#" + rowId;
        var reads = new HashSet<string>();
        foreach (var rf in rule.References)
        {
            var k = ResolveRefKey(rf, rule, rowId);
            if (k is not null) reads.Add(k);
        }
        return new OutcomePlan(key, rule, target, rowId, reads, rule.Source.Action == RuleActionKind.Compute);
    }

    private string? ResolveRefKey(RuleRef rf, CompiledRule rule, string? rowId) => rf switch
    {
        FieldRef f => CellAddress.Field(f.Name).Key,
        RowFieldRef rfld when rule.RowSection is not null && rowId is not null
            => CellAddress.Row(rule.RowSection, rowId, rfld.Field).Key,
        AggRef a => CellAddress.TableAggregate(a.Section, a.Fn, a.Col).Key,
        _ => null,
    };

    private IReadOnlyList<RuleRow> RowsOf(string section)
        => _instance.Tables.TryGetValue(section, out var rows) ? rows : Array.Empty<RuleRow>();

    private void Edge(string from, string dep)
    {
        _deps[from].Add(dep);
        _dependents[dep].Add(from);
    }

    private void AddReader(string key, string reader)
    {
        if (!_readers.TryGetValue(key, out var set)) _readers[key] = set = new HashSet<string>();
        set.Add(reader);
    }

    private void TopoSort()
    {
        var inDeg = _cells.Keys.ToDictionary(k => k, k => _deps[k].Count);
        var ready = new Queue<string>(inDeg.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var ordered = new HashSet<string>();
        while (ready.Count > 0)
        {
            var node = ready.Dequeue();
            _order.Add(_cells[node]);
            ordered.Add(node);
            foreach (var dependent in _dependents[node])
            {
                if (--inDeg[dependent] == 0) ready.Enqueue(dependent);
            }
        }
        // Any cell not ordered is in a defensive per-row cycle — appended; eval marks them Error(cycle).
        foreach (var cell in _cells.Values)
        {
            if (!ordered.Contains(cell.Key)) _order.Add(cell with { Cyclic = true });
        }
    }

    // ── cell + outcome evaluation ────────────────────────────────────────────

    private ComputedValue EvalComputedCell(ComputedCell cell, EvalBudget budget, IContextAdapter adapter)
    {
        if (cell.Cyclic) return ComputedValue.OfError(RuleError.Of(RuleEngineCodes.Cycle, "cell", cell.Key));

        if (cell.AggFold is { } fold) return FoldAggregate(fold.Fn, fold.Section, fold.Col, budget);

        var resolver = adapter.CreateResolver(new RuleEvalScope(cell.Rule!.RowSection, cell.RowId));
        var ctx = new EvalContext(resolver, _clock.GetUtcNow(), budget);
        try
        {
            return ComputedValue.Resolved(HarborlineJsonLogic.Evaluate(cell.Rule.Ast, ctx));
        }
        catch (RulePendingException) { return ComputedValue.OfPending(); }
        catch (RuleEvalException ex) { return ComputedValue.OfError(ex.Error); }
    }

    private RuleOutcome BuildOutcome(OutcomePlan plan, EvalBudget budget, IContextAdapter adapter)
    {
        if (plan.IsComputeValue)
        {
            var cv = _values.TryGetValue(plan.Target.Key, out var v) ? v : ComputedValue.Resolved(null);
            return RuleOutcome.OfValue(plan.Rule.Source.Id, plan.Target, cv);
        }
        var resolver = adapter.CreateResolver(new RuleEvalScope(plan.Rule.RowSection, plan.RowId));
        var ctx = new EvalContext(resolver, _clock.GetUtcNow(), budget);
        return OutcomeBuilder.Build(plan.Rule, plan.Target, resolver, ctx).Outcome;
    }

    private ComputedValue FoldAggregate(string fn, string section, string col, EvalBudget budget)
    {
        if (!_instance.Tables.TryGetValue(section, out var rows)) rows = new List<RuleRow>();
        if (rows.Count > _limits.MaxTableRowsPerAggregate)
        {
            return ComputedValue.OfError(RuleError.Of(RuleEngineCodes.TableTooLarge, "section", section));
        }

        var values = new List<JsonNode?>();
        foreach (var row in rows)
        {
            budget.Charge();
            var rowKey = CellAddress.Row(section, row.Id, col).Key;
            ComputedValue cv;
            if (_values.TryGetValue(rowKey, out var computed))
            {
                cv = computed;
            }
            else
            {
                var raw = row.Fields.TryGetValue(col, out var rv) ? rv : null;
                cv = PendingSentinel.Is(raw) ? ComputedValue.OfPending() : ComputedValue.Resolved(raw);
            }
            if (cv.State == ValueState.Pending) return ComputedValue.OfPending();
            if (cv.State == ValueState.Error) return ComputedValue.OfError(RuleError.Of(RuleEngineCodes.UpstreamError, "cell", rowKey));
            values.Add(cv.Value);
        }

        // A column whose every value is a decimal string is money — sum/min/max/avg must be exact
        // decimal, never IEEE double (finding F7). Number columns keep the numeric (double) fold.
        bool allDecimalStrings = values.Count > 0 && values.All(v => v is JsonValue jv && jv.TryGetValue<string>(out _));
        try
        {
            if (allDecimalStrings && fn is "sum" or "min" or "max" or "avg")
            {
                return MoneyAggregate(fn, values, section);
            }
            return fn switch
            {
                "count" => ComputedValue.Resolved(JsonValue.Create(values.Count)),
                "sum" => ComputedValue.Resolved(NumNode(values.Sum(v => HarborlineJsonLogic.ToNumber(v)))),
                "avg" => ComputedValue.Resolved(values.Count == 0
                    ? JsonValue.Create(0)
                    : NumNode(values.Sum(v => HarborlineJsonLogic.ToNumber(v)) / values.Count)),
                "min" => ComputedValue.Resolved(values.Count == 0 ? null : NumNode(values.Min(v => HarborlineJsonLogic.ToNumber(v)))),
                "max" => ComputedValue.Resolved(values.Count == 0 ? null : NumNode(values.Max(v => HarborlineJsonLogic.ToNumber(v)))),
                "any" => ComputedValue.Resolved(JsonValue.Create(values.Any(HarborlineJsonLogic.IsTruthy))),
                "all" => ComputedValue.Resolved(JsonValue.Create(values.All(HarborlineJsonLogic.IsTruthy))),
                _ => ComputedValue.OfError(RuleError.Of(RuleEngineCodes.UnknownOperator, "agg", fn)),
            };
        }
        catch (RuleEvalException ex)
        {
            return ComputedValue.OfError(ex.Error);
        }
    }

    private ComputedValue MoneyAggregate(string fn, List<JsonNode?> values, string section)
    {
        // Exact decimal sum/min/max over a money (decimal-string) column (finding F7). avg needs
        // exact decimal division (undefined in v1) → fail closed rather than silently use double.
        if (fn == "avg")
            return ComputedValue.OfError(RuleError.Of(RuleEngineCodes.MoneyAggUnsupported, "section", section));
        try
        {
            var acc = MoneyDecimal.Parse(((JsonValue)values[0]!).GetValue<string>());
            for (int i = 1; i < values.Count; i++)
            {
                var m = MoneyDecimal.Parse(((JsonValue)values[i]!).GetValue<string>());
                acc = fn switch
                {
                    "sum" => acc + m,
                    "min" => m.Compare(acc) < 0 ? m : acc,
                    "max" => m.Compare(acc) > 0 ? m : acc,
                    _ => acc,
                };
            }
            return ComputedValue.Resolved(JsonValue.Create(acc.ToCanonicalString()));
        }
        catch (FormatException)
        {
            return ComputedValue.OfError(RuleError.Of(RuleEngineCodes.TypeError, "op", "agg"));
        }
    }

    private static JsonNode NumNode(double d)
        => d == Math.Floor(d) && Math.Abs(d) < 9.007e15 ? JsonValue.Create((long)d) : JsonValue.Create(d);

    // ── projection ───────────────────────────────────────────────────────────

    private RuleEvaluationResult Project()
    {
        var visibility = new Dictionary<string, VisibilityState>();
        var validations = new List<RuleOutcome>();
        var options = new Dictionary<string, OptionsOutcome>();
        bool hasPending = _values.Values.Any(v => v.State == ValueState.Pending);

        foreach (var outcome in _outcomes.Values)
        {
            switch (outcome.OutputType)
            {
                case OutputType.Visibility:
                    visibility[outcome.Target.Key] = Merge(
                        visibility.TryGetValue(outcome.Target.Key, out var prev) ? prev : new VisibilityState(),
                        outcome.Visibility!);
                    break;
                case OutputType.Validity when outcome.Validity is { Ok: false }:
                    validations.Add(outcome);
                    break;
                case OutputType.Value when outcome.Value is { State: ValueState.Pending }:
                    hasPending = true;
                    break;
                case OutputType.Options:
                    // Last-writer-wins per cell (multiple set-options rules on one field is an
                    // authoring smell, not an engine concern — the graph is deterministically ordered).
                    options[outcome.Target.Key] = outcome.Options!;
                    break;
            }
        }

        return new RuleEvaluationResult(
            new Dictionary<string, RuleOutcome>(_outcomes),
            new Dictionary<string, ComputedValue>(_values),
            visibility,
            validations,
            hasPending,
            options);
    }

    // Merge rule: any visibility-rule false hides; required/readOnly are OR-merged.
    private static VisibilityState Merge(VisibilityState a, VisibilityState b)
        => new(a.Visible && b.Visible, a.Required || b.Required, a.ReadOnly || b.ReadOnly);

    private RuleEvaluationResult FailClosed(string code, string? ruleId = null, string? cellKey = null)
    {
        // Ticket 150: when the abort is attributable (the rule / aggregate cell in flight when the
        // budget tripped), the synthetic refusal NAMES the responsible definition in its PARAMS so
        // the reason is actionable. The outcome's RuleId is ALWAYS the reserved "rule.engine": an
        // authored rule id here can collide with the submit gate's hidden-page check filter and let
        // the sole blocker be skipped — submit accepted schema-only on budget exhaustion (review
        // regression on 7951dc5). Pre-change the synthetic id could never collide with authored ids.
        var error = ruleId is not null ? RuleError.Of(code, "rule", ruleId)
            : cellKey is not null ? RuleError.Of(code, "cell", cellKey)
            : RuleError.Of(code);
        var synthetic = RuleOutcome.OfValidity("rule.engine", CellAddress.Schema(), Validity.Invalid(error));
        return new RuleEvaluationResult(
            new Dictionary<string, RuleOutcome> { [synthetic.RuleId] = synthetic },
            new Dictionary<string, ComputedValue>(),
            new Dictionary<string, VisibilityState>(),
            new List<RuleOutcome> { synthetic },
            hasPending: false);
    }

    // ── internal node + plan records ─────────────────────────────────────────

    private sealed record ComputedCell(
        string Key,
        CellAddress Address,
        CompiledRule? Rule,
        string? RowId,
        (string Fn, string Section, string Col)? AggFold)
    {
        public bool Cyclic { get; init; }
    }

    private sealed record OutcomePlan(
        string Key,
        CompiledRule Rule,
        CellAddress Target,
        string? RowId,
        HashSet<string> Reads,
        bool IsComputeValue);
}
