namespace Harborline.Api.Foundation.RuleEngine;

/// <summary>
/// Stable, locale-independent diagnostic codes emitted by the rule engine
/// (SPINE-1 design §2.3, §4). Codes are the cross-tier contract — the TS engine
/// emits the identical strings; clients localize off these, never off prose.
/// </summary>
public static class RuleEngineCodes
{
    // ── Runtime evaluation errors (become a ComputedValue.Error / Validity.Error) ──

    /// <summary>Division (or modulo) by zero.</summary>
    public const string DivByZero = "rule.div_by_zero";

    /// <summary>An operand had the wrong type for the operator.</summary>
    public const string TypeError = "rule.type_error";

    /// <summary>A referenced cell is itself in <c>Error</c> — the error propagates.</summary>
    public const string UpstreamError = "rule.upstream_error";

    /// <summary>An unknown / non-whitelisted operator appeared in a lowered AST.</summary>
    public const string UnknownOperator = "rule.unknown_operator";

    /// <summary>A <c>var</c> / <c>agg</c> reference could not be resolved against the instance.</summary>
    public const string BadReference = "rule.bad_reference";

    /// <summary>A per-row cycle was detected defensively at instance time (SPINE-1 §2.3).</summary>
    public const string Cycle = "rule.cycle";

    /// <summary>The per-instance evaluation step budget was exhausted (SPINE-1 §4).</summary>
    public const string BudgetExceeded = "rule.budget_exceeded";

    /// <summary>The per-evaluation wall-clock ceiling (or a cancellation token) tripped (SPINE-1 §4).
    /// This is the code carried by <see cref="RuleEngineTimeoutException"/> — a non-authoritative
    /// infrastructure fault that PROPAGATES; it is never emitted as an evaluation outcome, so the
    /// wall-clock cannot cause a cross-tier divergent result (D1 ratification 2026-07-01).</summary>
    public const string Timeout = "rule.timeout";

    /// <summary>The instance graph exceeded the max-node bound (SPINE-1 §4).</summary>
    public const string GraphTooLarge = "rule.graph_too_large";

    /// <summary>A child table feeding an aggregate exceeded the max-row bound (SPINE-1 §4).</summary>
    public const string TableTooLarge = "rule.table_too_large";

    /// <summary>A <c>Pending</c> value reached the synchronous .NET integrity tier at save
    /// (SPINE-1 §1.3, Decision DE) — fail-closed.</summary>
    public const string PendingAtSave = "rule.pending_at_save";

    /// <summary>A numeric aggregate (<c>avg</c>) over a money / decimal-string column is
    /// not exact in v1 — decimal division is undefined — so it fails closed (finding F7).</summary>
    public const string MoneyAggUnsupported = "rule.money_agg_unsupported";

    /// <summary>A <c>Compute</c> rule was scoped to <c>Section</c>/<c>Schema</c> in a form graph,
    /// where it addresses no value cell — fail closed instead of a silent no-op (finding F8).</summary>
    public const string ComputeScopeInvalid = "rule.compute_scope_invalid";

    /// <summary>A <c>set-options</c> (Options) rule's expression evaluated to a non-array value —
    /// options must be a JSON array; fail closed instead of coercing (ADR 0146 D2 Wave-1).</summary>
    public const string OptionsNotArray = "rule.options_not_array";

    // ── Publish-time (compile) rejections (throw RuleCompilationException) ──

    /// <summary>A cyclic definition was rejected at publish (carries the cycle path).</summary>
    public const string CompileCycle = "rule.compile.cycle";

    /// <summary>The dependency depth exceeded the static bound at publish.</summary>
    public const string CompileDepthExceeded = "rule.compile.depth_exceeded";

    /// <summary>A single rule referenced more cells than the static bound.</summary>
    public const string CompileTooManyRefs = "rule.compile.too_many_refs";

    /// <summary>A rule's AST exceeded the static node-count bound.</summary>
    public const string CompileAstTooLarge = "rule.compile.ast_too_large";

    /// <summary>A string literal exceeded the static length bound.</summary>
    public const string CompileLiteralTooLong = "rule.compile.literal_too_long";

    /// <summary>A rule expression was not parseable JSON / well-formed JsonLogic.</summary>
    public const string CompileInvalidExpression = "rule.compile.invalid_expression";

    /// <summary>A rule declared a tier the v1 evaluator does not implement (e.g. PowerFx).</summary>
    public const string CompileUnsupportedTier = "rule.compile.unsupported_tier";

    /// <summary>A scope-grammar reference was malformed or invalid for the rule's scope.</summary>
    public const string CompileBadGrammar = "rule.compile.bad_grammar";

    /// <summary>A rule declared an action kind this engine does not recognize (a future /
    /// foreign restricting kind arriving via pack install or sync). Refused at compile
    /// (L1145): lowering an unknown kind to a permit would silently turn a restrict into
    /// show/hide — the write must refuse instead (ADR 0038 as corrected).</summary>
    public const string CompileUnknownAction = "rule.compile.unknown_action";
}
