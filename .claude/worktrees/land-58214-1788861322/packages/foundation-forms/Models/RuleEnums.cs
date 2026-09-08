namespace Harborline.Api.Foundation.Forms.Models;

/// <summary>
/// Tier of a rule's expression language (ADR 0055 §"Three-tier rules").
/// </summary>
/// <remarks>
/// <para>
/// Tier model (reframed per ADR 0140 + CIC 2026-06-30): Tier-1 = JSON-Schema
/// constraints (free); <b>Tier-2 = the ONE portable shipyard-jsonlogic/v1 engine
/// doing BOTH logic AND compute</b> (the dual-tier TS + .NET <c>Harborline.Api.Foundation.RuleEngine</c>,
/// SPINE-1). Power Fx is <b>DEMOTED</b> — no longer a tier; reserved only as a future
/// optional server-side advanced-function provider behind the same contract.
/// </para>
/// <para>
/// Tier-3 (<see cref="PowerFx"/>) is accepted as an enum value so authored
/// definitions can declare future intent without a schema migration; the SPINE-1
/// evaluator rejects Power-Fx-tier expressions in v1 (it is not evaluated).
/// </para>
/// </remarks>
public enum RuleTier
{
    /// <summary>JSON Schema 2020-12 <c>if/then/else</c> + <c>pattern</c> — used for
    /// constraints / pattern validation (Tier 1; ReDoS-bounded by the kernel registry).</summary>
    JsonSchema = 0,

    /// <summary>The shipyard-jsonlogic/v1 operator set — used for cross-field validation,
    /// visibility, presentation, AND compute (Tier 2; the SPINE-1 engine). Both logic and
    /// computed values run here.</summary>
    JsonLogic = 1,

    /// <summary>Microsoft Power Fx — DEMOTED (no longer a tier; ADR 0140 + CIC 2026-06-30).
    /// Reserved as a future optional advanced-function provider behind the Tier-2 contract;
    /// the SPINE-1 v1 evaluator rejects it.</summary>
    PowerFx = 2,
}

/// <summary>
/// Scope at which a rule evaluates (ADR 0055 §"Three-tier rules").
/// </summary>
public enum RuleScope
{
    /// <summary>Rule attaches to a single field; the field is the evaluation root.</summary>
    Field = 0,

    /// <summary>Rule attaches to a section; section visibility / required-state
    /// changes apply to all fields in the section.</summary>
    Section = 1,

    /// <summary>Rule attaches to the whole schema instance; used for cross-section
    /// invariants ("if status is ARCHIVED, all sections become read-only").</summary>
    Schema = 2,

    /// <summary>Rule attaches to a child-table row's field (per-row grain; SPINE-1 /
    /// FORM-2 child-table design). <c>ScopeTarget</c> is the <c>{childTableSectionId}/{rowFieldName}</c>
    /// path; the rule expands across the instance's actual rows at evaluation.</summary>
    Row = 3,

    /// <summary>Rule attaches to a child-table aggregate (table grain; SPINE-1 / FORM-2).
    /// <c>ScopeTarget</c> is the <c>{childTableSectionId}/{fn}/{col}</c> path.</summary>
    Table = 4,
}

/// <summary>
/// What a rule does when it evaluates to true (ADR 0055 §"Three-tier rules").
/// </summary>
public enum RuleActionKind
{
    /// <summary>Toggles the visibility of the rule's scope.</summary>
    Visibility = 0,

    /// <summary>Toggles the required state of the rule's scope.</summary>
    Required = 1,

    /// <summary>Toggles the read-only state of the rule's scope.</summary>
    ReadOnly = 2,

    /// <summary>Raises a validation error if the rule evaluates false at save time.</summary>
    Validate = 3,

    /// <summary>Computes the value of the rule's scope from the expression. As of
    /// SPINE-1 (ADR 0140 + CIC 2026-06-30) <b>Compute runs on Tier-2</b>
    /// (shipyard-jsonlogic/v1, decimal-correct money/date) — the prior restriction
    /// ("Compute is Power-Fx-only; the v1 evaluator rejects Compute on non-Power-Fx
    /// tiers") is lifted because ADR 0140 + the child-table spill/computed-field
    /// design require Tier-2 computed values. Power Fx is demoted to a future
    /// advanced-function provider behind the same contract.</summary>
    Compute = 4,

    /// <summary>Applies a presentation hint (style class / badge / severity) carried by
    /// the rule's <see cref="RuleDefinition.Presentation"/> when the rule evaluates true
    /// (SPINE-1 Decision DA — additive ADR-0055 contract extension).</summary>
    Presentation = 5,

    /// <summary>Sets the available options of the rule's scope (a choice/select field) from
    /// the expression — the <c>set-options</c> authoring verb (ADR 0140 D1 amendment vocabulary
    /// lock; built ADR 0146 D2 Wave-1). The expression evaluates to a JSON array of option
    /// values; a non-array fails closed (<c>rule.options_not_array</c>). Maps to the additive
    /// <c>OutputType.Options</c> (in the rule-engine tier, which depends on this assembly).
    /// Additive + back-compat: no pre-0146 rule carries this action.</summary>
    Options = 6,
}
