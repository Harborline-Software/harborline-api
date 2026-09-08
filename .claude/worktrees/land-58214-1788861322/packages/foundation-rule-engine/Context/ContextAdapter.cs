using System.Text.Json.Nodes;

using Harborline.Api.Foundation.RuleEngine.Evaluation;
using Harborline.Api.Foundation.RuleEngine.Graph;
using Harborline.Api.Foundation.RuleEngine.Model;

namespace Harborline.Api.Foundation.RuleEngine.Context;

/// <summary>
/// The evaluation scope a resolver is produced for (ADR 0146 D3). Carries the optional
/// child-collection row — <c>section</c> + <c>row id</c> — that a rule's <c>row.</c>
/// references resolve against. A pillar with no repeating sub-collection (a flat event
/// payload, a single merge record) evaluates at <see cref="Root"/>; its resolver returns a
/// bad-reference for any <c>row.</c> path, exactly as the flat workflow guard bag does.
/// </summary>
public readonly record struct RuleEvalScope(string? RowSection = null, string? RowId = null)
{
    /// <summary>The top-level (non-row) scope.</summary>
    public static RuleEvalScope Root { get; } = new();
}

/// <summary>
/// The context-adapter seam (ADR 0146 D3, board F3). The contract a consumer pillar
/// implements to map its own data shape into the rule engine's scope grammar: given an
/// <see cref="RuleEvalScope"/>, produce the <see cref="IValueResolver"/> the interpreter
/// resolves a compiled rule's <c>var</c>/<c>agg</c> references against.
/// </summary>
/// <remarks>
/// The operator set + interpreter are sized ONCE (D3); each pillar adapts its data HERE
/// rather than growing per-pillar operators. This interface — the seam — is owned by
/// ADR 0146 Wave 1; the forms (<see cref="FormContextAdapter"/>) and workflow-guard
/// (<see cref="ContextBagAdapter"/>) contexts are its first (migrated) implementations. The
/// three concrete new adapters (document-merge, event-payload, dataset-row) land with their
/// pillar programs (#111/#112/#113) implementing this same contract — own the seam, scatter
/// the implementations.
/// </remarks>
public interface IContextAdapter
{
    /// <summary>Produces the resolver bound to this adapter's pillar data for <paramref name="scope"/>.</summary>
    IValueResolver CreateResolver(RuleEvalScope scope);
}

/// <summary>
/// The forms context as the first <see cref="IContextAdapter"/> implementation (ADR 0146 D3).
/// Maps a bound <see cref="RuleInstance"/> plus the graph's already-evaluated computed cells
/// into a <see cref="CellResolver"/> for the requested scope. Behaviour-neutral: the reactive
/// form graph obtains its per-cell / per-row resolvers through this seam instead of
/// constructing <see cref="CellResolver"/> inline. The computed-cell dictionary is held by
/// reference — it is filled live as the graph evaluates in topo order, so a resolver produced
/// mid-evaluation reads the up-to-date upstream cells, identical to the pre-seam construction.
/// </summary>
internal sealed class FormContextAdapter : IContextAdapter
{
    private readonly IReadOnlyDictionary<string, ComputedValue> _computed;
    private readonly RuleInstance _instance;

    public FormContextAdapter(IReadOnlyDictionary<string, ComputedValue> computed, RuleInstance instance)
    {
        _computed = computed;
        _instance = instance;
    }

    public IValueResolver CreateResolver(RuleEvalScope scope)
        => new CellResolver(_computed, _instance, scope.RowSection, scope.RowId);
}

/// <summary>
/// The workflow-guard context as an <see cref="IContextAdapter"/> (ADR 0146 D3): maps a flat
/// process / context bag into a <see cref="ContextBagResolver"/>. The bag has no repeating
/// sub-collection, so <see cref="RuleEvalScope.RowSection"/> is ignored (a <c>row.</c>
/// reference resolves to a bad-reference, as before).
/// </summary>
internal sealed class ContextBagAdapter : IContextAdapter
{
    private readonly IReadOnlyDictionary<string, JsonNode?> _bag;

    public ContextBagAdapter(IReadOnlyDictionary<string, JsonNode?> bag) => _bag = bag;

    public IValueResolver CreateResolver(RuleEvalScope scope) => new ContextBagResolver(_bag);
}
