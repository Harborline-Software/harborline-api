namespace Harborline.Api.Foundation.RuleEngine.Skins;

/// <summary>
/// Stable, locale-independent rejection codes emitted by the authoring skins (ADR 0146 D2). Byte-identical
/// to the TS <c>SkinCodes</c> (<c>rule-engine/src/skins/codes.ts</c>) — clients localize off these, never off
/// prose. A skin rejection is a publish-time <see cref="Compilation.RuleCompilationException"/> (fail-closed:
/// a rejected skin never produces a rule that reaches an instance).
/// </summary>
public static class SkinCodes
{
    // ── decision-table skin ──────────────────────────────────────────────────

    /// <summary>The table declared no input columns.</summary>
    public const string DecisionTableNoInputs = "rule.skin.decision_table_no_inputs";

    /// <summary>The table declared no rows.</summary>
    public const string DecisionTableEmpty = "rule.skin.decision_table_empty";

    /// <summary>A row's cell count does not match the declared input arity.</summary>
    public const string DecisionTableBadRow = "rule.skin.decision_table_bad_row";

    /// <summary>A compare cell used an operator outside the closed comparison set.</summary>
    public const string DecisionTableBadCell = "rule.skin.decision_table_bad_cell";

    /// <summary>An unknown hit policy was declared.</summary>
    public const string DecisionTableInvalidHitPolicy = "rule.skin.decision_table_invalid_hit_policy";

    /// <summary>No explicit no-match posture (no declared default and no catch-all row) — a silent null on
    /// no-match is forbidden (board F1).</summary>
    public const string NoMatchUnresolved = "rule.skin.no_match_unresolved";

    // ── formula skin ─────────────────────────────────────────────────────────

    /// <summary>The formula declared an empty / missing expression.</summary>
    public const string FormulaEmpty = "rule.skin.formula_empty";

    /// <summary>The formula's expression references a var that is not a declared input (named-input
    /// discipline — an undeclared reference is a compile rejection, not a silent free variable).</summary>
    public const string FormulaUndeclaredRef = "rule.skin.formula_undeclared_ref";
}
