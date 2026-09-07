using System.Text.Json.Nodes;

using Harborline.Api.Foundation.RuleEngine.Compilation;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.RuleEngine.Skins;

// ─────────────────────────────────────────────────────────────────────────────
//  ADR 0146 D2 — the DECISION-TABLE authoring skin (compile layer).
//
//  "One expression language" (binding constraint #3): the decision table is an
//  authoring SKIN over the ratified D1 core, never a parallel evaluator. A skin
//  lowers to a plain `RuleDefinition` whose `Expression` is ordinary shipyard-
//  jsonlogic/v1 — a single multi-branch `if` the existing engine evaluates. No new
//  operator, no new OutputType, no new eval path (the 5-switches-per-tier rule does
//  NOT apply — the skin is a compile-layer representation over the existing AST).
//
//  Board F1 (BINDING) — a table is MORE than a bag of independent per-row cells:
//    • the skin carries a declared HIT POLICY (priority | first-match), resolved
//      INSIDE the compiled rule (the row ORDER of the emitted `if`), never as
//      independent cells whose overlap is unresolved;
//    • the skin carries an EXPLICIT no-match default (a declared default value OR a
//      mandatory catch-all row) — a silent null on no-match is a compile rejection,
//      not a runtime surprise;
//    • interval cells REIFY their upper bound (`and(>=lo, <hi)`) — the migration of
//      the floor-only `ThresholdDecisionTable` makes the implicit tops explicit.
//
//  The produced `RuleDefinition` flows through `RuleCompiler.Compile` exactly as a
//  hand-authored rule does, so the `RuleCompileAdmission` publish fence (D7) still
//  gates it — a skin never compiles+publishes around admission.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>The conflict-resolution policy a decision table declares (ADR 0146 D2 / board F1).</summary>
public enum HitPolicy
{
    /// <summary>The highest-<see cref="DecisionRow.Priority"/> matching row wins (ties broken by
    /// declared order, deterministically). Compiles to an <c>if</c> whose branches are ordered by
    /// descending priority — this is the <c>ThresholdDecisionTable</c> "highest-floor-wins" semantics.</summary>
    Priority = 0,

    /// <summary>The first matching row in DECLARED order wins. Compiles to an <c>if</c> whose branches
    /// keep declared order.</summary>
    FirstMatch = 1,
}

/// <summary>Which shape a <see cref="DecisionCell"/> takes (one condition per input column, per row).</summary>
public enum CellKind
{
    /// <summary>Wildcard — matches anything (contributes no condition). A row of all-<see cref="Any"/>
    /// cells is a catch-all.</summary>
    Any = 0,

    /// <summary>A single comparison against a literal: <c>{op:[{var:input}, value]}</c>.</summary>
    Compare = 1,

    /// <summary>A reified half-open interval <c>[lo, hi)</c> → <c>and(&gt;=lo, &lt;hi)</c>. Either bound
    /// may be null (open on that side) — an open-high interval is <c>&gt;=lo</c>, the top row of a
    /// floor-ordered table.</summary>
    Range = 2,
}

/// <summary>
/// One condition cell of a decision-table row — the test applied to ONE input column. Use the factory
/// members (<see cref="Wildcard"/> / <see cref="Compare"/> / <see cref="Range"/>) rather than the raw ctor.
/// </summary>
public sealed record DecisionCell(
    CellKind Kind,
    string? Op = null,
    JsonNode? Value = null,
    JsonNode? RangeLoInclusive = null,
    JsonNode? RangeHiExclusive = null)
{
    /// <summary>The set of comparison operators a <see cref="CellKind.Compare"/> cell may use — the
    /// closed operator set's relational/equality ops (no regex, no membership here).</summary>
    public static readonly IReadOnlySet<string> CompareOps =
        new HashSet<string> { "==", "!=", "<", "<=", ">", ">=" };

    /// <summary>A wildcard cell — always matches.</summary>
    public static DecisionCell Wildcard { get; } = new(CellKind.Any);

    /// <summary>A comparison cell: <c>{op:[{var:input}, value]}</c>.</summary>
    public static DecisionCell Compare(string op, JsonNode? value) => new(CellKind.Compare, Op: op, Value: value);

    /// <summary>A reified half-open interval <c>[lo, hi)</c>. Either bound may be null (open on that side).</summary>
    public static DecisionCell Range(JsonNode? loInclusive, JsonNode? hiExclusive)
        => new(CellKind.Range, RangeLoInclusive: loInclusive, RangeHiExclusive: hiExclusive);
}

/// <summary>
/// One row of a decision table: a condition per input column + the output value the row yields + a priority
/// (used only under <see cref="HitPolicy.Priority"/>). <see cref="Output"/> is a raw JSON value the compiled
/// rule returns as its outcome (interpreted per the table's <see cref="DecisionTableSkin.Action"/>).
/// </summary>
public sealed record DecisionRow(
    IReadOnlyList<DecisionCell> When,
    JsonNode? Output,
    int Priority = 0);

/// <summary>
/// The explicit no-match posture (board F1 — a silent null is forbidden). EXACTLY ONE of a declared
/// <see cref="Default"/> value or <see cref="RequireCatchAll"/> is active. Under <see cref="RequireCatchAll"/>
/// the compiler verifies a genuine catch-all row (all-<see cref="CellKind.Any"/> cells) exists and uses it as
/// the terminal else; if none exists the table is REJECTED at compile.
/// </summary>
public sealed record NoMatch(bool HasDefault, JsonNode? Default = null, bool RequireCatchAll = false)
{
    /// <summary>A declared default outcome for the no-match case (the terminal <c>else</c> branch).</summary>
    public static NoMatch WithDefault(JsonNode? value) => new(HasDefault: true, Default: value);

    /// <summary>Require a catch-all row to exist (its output is the terminal else). No silent null.</summary>
    public static NoMatch CatchAll { get; } = new(HasDefault: false, RequireCatchAll: true);
}

/// <summary>
/// The decision-table authoring skin (ADR 0146 D2). Compile it with <see cref="DecisionTableCompiler.Compile"/>
/// to a <see cref="RuleDefinition"/> the D1 core evaluates.
/// </summary>
/// <param name="RuleId">The stable rule id the produced <see cref="RuleDefinition"/> carries.</param>
/// <param name="Scope">The rule scope (where the outcome applies).</param>
/// <param name="ScopeTarget">The scope target (field/section path); empty for Schema.</param>
/// <param name="Action">The output action the outcome is interpreted as (Compute/Options/Validate/…).</param>
/// <param name="HitPolicy">The conflict-resolution policy (board F1).</param>
/// <param name="Inputs">The input columns — one var ref per column (e.g. <c>amount</c> / <c>field.amount</c>),
/// aligned positionally with each row's <see cref="DecisionRow.When"/> cells.</param>
/// <param name="Rows">The rows.</param>
/// <param name="NoMatch">The explicit no-match posture.</param>
public sealed record DecisionTableSkin(
    string RuleId,
    RuleScope Scope,
    string ScopeTarget,
    RuleActionKind Action,
    HitPolicy HitPolicy,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<DecisionRow> Rows,
    NoMatch NoMatch);

/// <summary>
/// Lowers a <see cref="DecisionTableSkin"/> to a <see cref="RuleDefinition"/> whose <c>Expression</c> is a
/// single multi-branch <c>if</c> resolving the hit policy inside the compiled rule (board F1). Pure +
/// deterministic — both tiers produce the byte-identical expression (proven by the shared conformance corpus).
/// </summary>
public static class DecisionTableCompiler
{
    /// <summary>Compiles the skin; throws <see cref="RuleCompilationException"/> (a <c>rule.skin.*</c> code)
    /// on any structural rejection (no rows, no inputs, ragged row, bad cell, unresolved no-match).</summary>
    public static RuleDefinition Compile(DecisionTableSkin skin)
    {
        ArgumentNullException.ThrowIfNull(skin);
        string id = skin.RuleId;

        if (skin.Inputs.Count == 0)
            throw Reject(SkinCodes.DecisionTableNoInputs, id, "a decision table must declare at least one input column");
        if (skin.Rows.Count == 0)
            throw Reject(SkinCodes.DecisionTableEmpty, id, "a decision table must declare at least one row");

        // Validate every row is well-formed against the input arity + the closed cell ops.
        for (int r = 0; r < skin.Rows.Count; r++)
        {
            var row = skin.Rows[r];
            if (row.When.Count != skin.Inputs.Count)
                throw Reject(SkinCodes.DecisionTableBadRow, id,
                    $"row {r} has {row.When.Count} cell(s) but the table declares {skin.Inputs.Count} input column(s)");
            foreach (var cell in row.When)
            {
                if (cell.Kind == CellKind.Compare && (cell.Op is null || !DecisionCell.CompareOps.Contains(cell.Op)))
                    throw Reject(SkinCodes.DecisionTableBadCell, id,
                        $"row {r} has a compare cell with an unsupported operator '{cell.Op}' (allowed: {string.Join(", ", DecisionCell.CompareOps)})");
            }
        }

        // Order rows per the hit policy — this IS the "resolve overlap inside the compiled rule" step.
        // Priority: descending Priority, ties by declared order (OrderBy is a stable sort, so a plain
        // OrderByDescending on Priority preserves declared order within a priority band).
        var ordered = skin.HitPolicy switch
        {
            HitPolicy.Priority => skin.Rows.OrderByDescending(r => r.Priority).ToList(),
            HitPolicy.FirstMatch => skin.Rows.ToList(),
            _ => throw Reject(SkinCodes.DecisionTableInvalidHitPolicy, id, $"unknown hit policy '{skin.HitPolicy}'"),
        };

        // Resolve the explicit no-match terminal (board F1 — never a silent null).
        JsonNode? terminalElse;
        IReadOnlyList<DecisionRow> cascadeRows;
        if (skin.NoMatch.HasDefault)
        {
            terminalElse = skin.NoMatch.Default?.DeepClone();
            cascadeRows = ordered;
        }
        else if (skin.NoMatch.RequireCatchAll)
        {
            // The LAST catch-all row (in evaluation order) is the terminal else; earlier rows still cascade.
            int catchAllIdx = -1;
            for (int i = ordered.Count - 1; i >= 0; i--)
            {
                if (IsCatchAll(ordered[i])) { catchAllIdx = i; break; }
            }
            if (catchAllIdx < 0)
                throw Reject(SkinCodes.NoMatchUnresolved, id,
                    "hit policy requires a catch-all row (all-Any cells) but none is present — a silent null on no-match is forbidden (ADR 0146 D2, board F1)");
            terminalElse = ordered[catchAllIdx].Output?.DeepClone();
            cascadeRows = ordered.Where((_, i) => i != catchAllIdx).ToList();
        }
        else
        {
            throw Reject(SkinCodes.NoMatchUnresolved, id,
                "no explicit no-match default (declared default or catch-all) — a silent null on no-match is forbidden (ADR 0146 D2, board F1)");
        }

        // Emit the single multi-branch if: [cond0, out0, cond1, out1, …, terminalElse].
        var ifArgs = new JsonArray();
        foreach (var row in cascadeRows)
        {
            ifArgs.Add(RowCondition(row, skin.Inputs));
            ifArgs.Add(row.Output?.DeepClone());
        }
        ifArgs.Add(terminalElse);
        var expression = new JsonObject { ["if"] = ifArgs };

        return new RuleDefinition(
            Id: id,
            Tier: RuleTier.JsonLogic,
            Scope: skin.Scope,
            ScopeTarget: skin.ScopeTarget,
            Expression: expression.ToJsonString(),
            Action: skin.Action);
    }

    private static bool IsCatchAll(DecisionRow row) => row.When.All(c => c.Kind == CellKind.Any);

    /// <summary>The AND of every non-wildcard cell in the row. An all-wildcard row's condition is the
    /// boolean literal <c>true</c> (it always fires) — only reached when a declared default coexists.</summary>
    private static JsonNode RowCondition(DecisionRow row, IReadOnlyList<string> inputs)
    {
        var terms = new List<JsonNode>();
        for (int c = 0; c < row.When.Count; c++)
        {
            var term = CellCondition(row.When[c], inputs[c]);
            if (term is not null) terms.Add(term);
        }
        if (terms.Count == 0) return JsonValue.Create(true);
        if (terms.Count == 1) return terms[0];
        var andArgs = new JsonArray();
        foreach (var t in terms) andArgs.Add(t);
        return new JsonObject { ["and"] = andArgs };
    }

    /// <summary>One cell → a JsonLogic condition node (or null for a wildcard / empty range).</summary>
    private static JsonNode? CellCondition(DecisionCell cell, string input)
    {
        switch (cell.Kind)
        {
            case CellKind.Any:
                return null;
            case CellKind.Compare:
                return new JsonObject { [cell.Op!] = new JsonArray(Var(input), cell.Value?.DeepClone()) };
            case CellKind.Range:
            {
                JsonNode? lo = cell.RangeLoInclusive is null
                    ? null
                    : new JsonObject { [">="] = new JsonArray(Var(input), cell.RangeLoInclusive.DeepClone()) };
                JsonNode? hi = cell.RangeHiExclusive is null
                    ? null
                    : new JsonObject { ["<"] = new JsonArray(Var(input), cell.RangeHiExclusive.DeepClone()) };
                if (lo is not null && hi is not null) return new JsonObject { ["and"] = new JsonArray(lo, hi) };
                return lo ?? hi; // an open-ended range with neither bound is a wildcard (null)
            }
            default:
                return null;
        }
    }

    private static JsonObject Var(string path) => new() { ["var"] = JsonValue.Create(path) };

    private static RuleCompilationException Reject(string code, string ruleId, string message)
        => new(code, $"decision-table skin '{ruleId}': {message}", ruleId);
}
