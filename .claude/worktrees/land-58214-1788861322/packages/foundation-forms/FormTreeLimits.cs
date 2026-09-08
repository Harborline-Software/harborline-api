namespace Harborline.Api.Foundation.Forms;

/// <summary>
/// Fail-closed authoring-time bounds for a form's recursive item tree (ADR 0055
/// Rev 7 — nested sub-form items; the <b>INV-S2 resource-bounds invariant extended
/// to the tree</b>). A definition whose tree exceeds any of these is REJECTED at
/// registration (publish) with a stable, locale-independent code from
/// <see cref="FormDefinitionCodes"/> — this is <b>security, not ergonomics</b>: an
/// unbounded / adversarial tree is a graph-construction / compute-DoS vector
/// (SPINE-1 §4, FORM-2 design open item 2). The <b>mechanism</b> (fail-closed,
/// static-at-publish) is fixed; the <b>numbers</b> are CIC-tunable.
/// </summary>
/// <remarks>
/// These are STRUCTURAL bounds on the definition (checked once at authoring),
/// complementary to the rule engine's INSTANCE-time bounds
/// (<c>Harborline.Api.Foundation.RuleEngine.RuleEngineLimits</c> — graph nodes, table
/// rows, step budget). Both fail closed; together they bound the definition AND the
/// evaluation.
/// </remarks>
public sealed record FormTreeLimits
{
    /// <summary>Max container-nesting depth. A top-level item is depth 1; each
    /// Group/Collection descent adds 1. A tree deeper than this is rejected
    /// (<see cref="FormDefinitionCodes.TreeDepthExceeded"/>).</summary>
    public int MaxDepth { get; init; } = 8;

    /// <summary>Max TOTAL nodes across all sections' trees (fields + containers).
    /// A tree with more nodes is rejected (<see cref="FormDefinitionCodes.TreeTooManyNodes"/>).</summary>
    public int MaxNodes { get; init; } = 512;

    /// <summary>Max declared Collection <c>Cardinality.Max</c> (and the cap an
    /// unbounded Collection is validated against). Aligned with the rule engine's
    /// <c>MaxTableRowsPerAggregate</c> so an authored max cannot exceed what eval
    /// will fold. A larger declared max is rejected
    /// (<see cref="FormDefinitionCodes.TreeBadCardinality"/>).</summary>
    public int MaxCollectionInstances { get; init; } = 2_000;

    /// <summary>The CIC-default v1 tree limits.</summary>
    public static FormTreeLimits Default { get; } = new();
}
