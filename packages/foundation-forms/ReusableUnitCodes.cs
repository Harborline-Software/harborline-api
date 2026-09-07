namespace Harborline.Api.Foundation.Forms;

/// <summary>
/// Stable, locale-independent diagnostic codes for the D4 reuse-unit primitive
/// (ADR 0135 amendment 2026-07-01) — unit-body authoring rejections and reference-cascade
/// resolution failures. Codes are the cross-tier contract; a client localizes off these,
/// never off the exception's English prose (the fleet's "validation errors are codes+params"
/// rule). Item-tree bounds inside a unit body reuse the <see cref="FormDefinitionCodes"/>
/// <c>Tree*</c> codes (one shared bounded-tree validator, no divergent vocabulary).
/// </summary>
public static class ReusableUnitCodes
{
    // ── unit-body authoring (registration) ───────────────────────────────────────

    /// <summary>A <see cref="Models.ReusableUnitKind.FormComponent"/> unit carries no body,
    /// or a <see cref="Models.ReusableUnitKind.WorkflowSubgraph"/> unit carries a form body.</summary>
    public const string WrongKindBody = "reuse.wrong_kind_body";

    /// <summary>A form-component unit body has no items (a reusable component must be non-empty).</summary>
    public const string EmptyBody = "reuse.empty_body";

    // Note: an undeclared field key inside a unit body surfaces as the shared
    // FormDefinitionCodes.TreeUnknownField (one bounded-tree validator, no divergent vocab).

    // ── reference-cascade resolution ─────────────────────────────────────────────

    /// <summary>A reference points at a unit that does not exist, or (for a latest-published
    /// reference) has no published version, in the tenant. Fail-closed.</summary>
    public const string UnresolvedReference = "reuse.unresolved_reference";

    /// <summary>A form references a <see cref="Models.ReusableUnitKind.WorkflowSubgraph"/>
    /// unit — the Phase-1 fence: the workflow consumer is not built yet, so this is rejected
    /// fail-closed rather than silently expanded as if it were a form component.</summary>
    public const string WorkflowSubgraphUnsupported = "reuse.workflow_subgraph_unsupported";

    /// <summary>The consuming definition declares a field the referenced unit owns — an
    /// attempt to override a CP-locked property at the reference site. Rejected fail-closed
    /// (there is no override channel in Phase 1).</summary>
    public const string LockedFieldOverride = "reuse.locked_field_override";

    /// <summary>Two referenced units (or two reference sites) contribute the same field key —
    /// the effective definition cannot carry two overlays for one key.</summary>
    public const string DuplicateReusedField = "reuse.duplicate_reused_field";
}
