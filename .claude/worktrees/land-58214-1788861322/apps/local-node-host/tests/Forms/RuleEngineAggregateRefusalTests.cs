using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.RuleEngine;
using Harborline.Api.Foundation.RuleEngine.Compilation;
using Harborline.Api.Foundation.RuleEngine.Graph;
using Harborline.Api.Foundation.RuleEngine.Model;

namespace Harborline.Api.LocalNodeHost.Tests.Forms;

/// <summary>
/// Ticket 162, both halves. An agg node the compiler cannot statically register is a
/// publish-time refusal (<c>rule.compile.bad_grammar</c>) rather than a per-keystroke runtime
/// refusal with no authoring signal; a well-formed aggregate whose evaluation context cannot
/// provide it refuses with the one shared bad-reference shape
/// (<c>RefValue.UnavailableAggregate</c>) instead of the fabricated <c>Resolved(null)</c> the
/// form graph's CellResolver used to answer. Mirrors the harborline-platform rule-runtime twins.
/// </summary>
/// <remarks>
/// Ticket 193 removed the <c>Corpus_aggregate_refusal_case_holds</c> Theory that used to close this
/// file. It was a one-file scaffold: no rule-engine test project existed, so ticket 162 executed the
/// single corpus case from here and could not reach the <c>expectedAst</c> lane at all
/// (<c>CompiledGraph.Rules</c> is internal and this suite is an external consumer). The real runner is
/// <c>packages/foundation-rule-engine/tests/ConformanceTests.cs</c>, which executes all ten corpus
/// files including that lane. What stays here are the unit twins, which are host-suite material.
/// </remarks>
public sealed class RuleEngineAggregateRefusalTests
{
    private static RuleDefinition Compute(string id, string target, string expression,
        RuleScope scope = RuleScope.Field)
        => new(id, RuleTier.JsonLogic, scope, target, expression, RuleActionKind.Compute);

    private static RuleEvaluationResult Evaluate(RuleDefinition rule, string instanceJson)
        => new FormRuleGraph(RuleCompiler.Compile(new[] { rule }), clock: TimeProvider.System)
            .EvaluateInstance(RuleInstance.FromJson(JsonNode.Parse(instanceJson)!.AsObject()));

    // ── the two refusals ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("{\"agg\":[\"sum\",\"items\",\"amount\",\"extra\"]}")]         // arity 4
    [InlineData("{\"agg\":[\"sum\",{\"var\":\"field.section\"},\"amount\"]}")] // expression-valued arg
    public void Unregistrable_aggregate_is_a_publish_time_refusal_not_runtime_data(string expression)
    {
        // Reference extraction cannot register a fold cell for these, so they used to be skipped
        // and then refused per keystroke at runtime with nothing to tell the author. The compiler
        // refuses them at publish instead, identically to the TS tier (150-family direction).
        var ex = Assert.Throws<RuleCompilationException>(
            () => RuleCompiler.Compile(new[] { Compute("c.dyn", "dyn", expression) }));
        Assert.Equal(RuleEngineCodes.CompileBadGrammar, ex.Code);
    }

    [Fact]
    public void Unavailable_aggregate_refuses_with_the_one_shared_shape()
    {
        // The guard tier's flat context bag carries no tables, so a well-formed aggregate is
        // unavailable data there — the surviving runtime path through the shared constructor.
        // The form graph's CellResolver answers the identical shape; since the compiler now
        // refuses every agg it cannot register, that branch is defense-in-depth.
        var value = new GuardEvaluator(clock: TimeProvider.System).EvaluateValue(
            Compute("g.total", "", "{\"var\":\"table.sum(items.amount)\"}", RuleScope.Schema),
            new Dictionary<string, JsonNode?>());

        Assert.Equal(ValueState.Error, value.State);
        Assert.Equal(RuleEngineCodes.BadReference, value.Error!.Code);
        Assert.Equal("items/sum/amount", value.Error!.Params["agg"]);
    }

    // ── the boundaries the refusal must not swallow ───────────────────────────

    [Fact]
    public void Statically_registered_aggregate_still_computes()
    {
        var result = Evaluate(
            Compute("c.total", "total", "{\"var\":\"table.sum(items.amount)\"}"),
            "{\"items\":[{\"amount\":1},{\"amount\":2}]}");

        Assert.Equal(ValueState.Resolved, result.Values["field:total"].State);
        Assert.Equal("3", result.Values["field:total"].Value!.ToJsonString());
        Assert.Equal("3", result.Values["agg:items/sum/amount"].Value!.ToJsonString());
        Assert.False(result.IsSaveBlocked);
    }

    [Fact]
    public void Aggregate_that_cannot_be_computed_closes_the_save_gate()
    {
        // The save-gate half of the contract, on a route the compiler cannot refuse: exact-decimal
        // avg over a money column is undefined in v1 and fails closed, so the aggregate cell errors,
        // the dependent field propagates it, and the write is blocked rather than saving a guess.
        var result = Evaluate(
            Compute("c.avg", "avg", "{\"var\":\"table.avg(items.price)\"}"),
            "{\"items\":[{\"price\":\"0.10\"},{\"price\":\"0.20\"}]}");

        Assert.Equal(ValueState.Error, result.Values["agg:items/avg/price"].State);
        Assert.Equal(RuleEngineCodes.MoneyAggUnsupported, result.Values["agg:items/avg/price"].Error!.Code);
        Assert.Equal(ValueState.Error, result.Values["field:avg"].State);
        Assert.True(result.IsSaveBlocked);
    }
}
