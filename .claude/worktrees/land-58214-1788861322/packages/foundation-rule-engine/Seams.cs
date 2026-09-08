using System.Text.Json.Nodes;

using Harborline.Api.Foundation.RuleEngine.Compilation;
using Harborline.Api.Foundation.RuleEngine.Graph;
using Harborline.Api.Foundation.RuleEngine.Model;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.RuleEngine;

/// <summary>
/// The atom both in-assembly consumers share (SPINE-1 design §5.2): evaluate one
/// compiled rule against a resolved context into a <see cref="RuleOutcome"/>.
/// Internal plumbing — external consumers use <see cref="IFormRuleGraph"/> /
/// <see cref="IGuardEvaluator"/> (which expose only public shapes).
/// </summary>
internal interface IRuleEvaluator
{
    /// <summary>Evaluates one compiled rule for one target cell against a resolver.</summary>
    RuleOutcome Evaluate(CompiledRule rule, CellAddress target, string ruleKey,
        Evaluation.IValueResolver resolver, DateTimeOffset now, RuleEngineLimits limits, CancellationToken ct);
}

/// <summary>
/// The form orchestrator (SPINE-1 design §5.2, §5.3): compile a definition's rules
/// into a graph, evaluate a whole instance, and reactively re-evaluate transitive
/// dependents on a single-cell change or a child-table row add/remove.
/// </summary>
/// <remarks>
/// Stateful: <see cref="EvaluateInstance"/> binds the current instance; subsequent
/// <see cref="Reevaluate"/> / <see cref="AddRow"/> / <see cref="RemoveRow"/> mutate it
/// and recompute only the affected front (the reactive as-you-type path).
/// </remarks>
public interface IFormRuleGraph
{
    /// <summary>The compiled (publish-time-validated) rule graph.</summary>
    CompiledGraph Compiled { get; }

    /// <summary>Binds <paramref name="instance"/> and evaluates the whole graph (the integrity-tier path).</summary>
    RuleEvaluationResult EvaluateInstance(RuleInstance instance, CancellationToken ct = default);

    /// <summary>Applies a single top-level field change and re-evaluates the transitive dependents only.</summary>
    RuleEvaluationResult Reevaluate(string fieldName, JsonNode? newValue, CancellationToken ct = default);

    /// <summary>Adds a child-table row (incremental graph edit) and re-evaluates the affected aggregates + dependents.</summary>
    RuleEvaluationResult AddRow(string section, RuleRow row, CancellationToken ct = default);

    /// <summary>Removes a child-table row by id (incremental graph edit); returns the re-evaluated result.</summary>
    RuleEvaluationResult RemoveRow(string section, string rowId, CancellationToken ct = default);
}

/// <summary>
/// The workflow orchestrator (SPINE-1 design §5.2, §5.4): evaluate a single
/// boolean/value expression (a transition guard / action condition) against a flat
/// context bag — a one-node graph.
/// </summary>
public interface IGuardEvaluator
{
    /// <summary>Evaluates a guard rule (a <c>Validate</c>-shaped boolean) over a context bag.</summary>
    Validity EvaluateGuard(RuleDefinition rule, IReadOnlyDictionary<string, JsonNode?> context,
        CancellationToken ct = default);

    /// <summary>Evaluates a value expression (a <c>Compute</c>-shaped rule) over a context bag.</summary>
    ComputedValue EvaluateValue(RuleDefinition rule, IReadOnlyDictionary<string, JsonNode?> context,
        CancellationToken ct = default);
}
