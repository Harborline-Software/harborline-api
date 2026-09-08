namespace Harborline.Api.Foundation.RuleEngine;

/// <summary>
/// Fail-closed resource bounds for the rule engine (SPINE-1 design §4 — "the
/// security crux"). Every bound is fail-closed: exceeding a static bound rejects
/// the definition at publish; exceeding a runtime bound aborts evaluation with a
/// stable code and fails closed. The <b>mechanism</b> (fail-closed, static where
/// possible) is fixed (Decision DG); the <b>numbers</b> are CIC-tunable (Decision DF).
/// </summary>
/// <remarks>
/// Both tiers enforce the identical static caps (so the client rejects the same
/// definitions the server does); the runtime step budget + wall-clock ceiling are
/// the server's authoritative backstop (the client's are advisory UX).
/// </remarks>
public sealed record RuleEngineLimits
{
    /// <summary>Max addressable graph nodes (fields × rows + aggregates). Instance-time.</summary>
    public int MaxGraphNodes { get; init; } = 5_000;

    /// <summary>Max child-table rows feeding one aggregate. Instance-time.</summary>
    public int MaxTableRowsPerAggregate { get; init; } = 2_000;

    /// <summary>Max dependency depth (longest path). Publish-time (static).</summary>
    public int MaxDependencyDepth { get; init; } = 64;

    /// <summary>Max references from a single rule. Publish-time (static).</summary>
    public int MaxReferencesPerRule { get; init; } = 64;

    /// <summary>Max AST node count for one rule. Publish-time (static).</summary>
    public int MaxAstNodes { get; init; } = 256;

    /// <summary>Max string-literal length inside an expression. Publish-time (static).</summary>
    public int MaxLiteralLength { get; init; } = 4_096;

    /// <summary>Per-instance total evaluation step budget (ops). Runtime.</summary>
    public int StepBudget { get; init; } = 250_000;

    /// <summary>Per-evaluation wall-clock ceiling. Runtime (.NET authoritative backstop).</summary>
    public TimeSpan WallClockCeiling { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The CIC-default v1 limits (SPINE-1 §4 table).</summary>
    public static RuleEngineLimits Default { get; } = new();
}
