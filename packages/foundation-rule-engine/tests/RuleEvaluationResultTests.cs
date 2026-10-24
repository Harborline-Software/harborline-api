using System.Text.Json.Nodes;

using Harborline.Api.Foundation.RuleEngine.Model;

namespace Harborline.Api.Foundation.RuleEngine.Tests;

public sealed class RuleEvaluationResultTests
{
    [Fact]
    public void No_blockers_allow_saving()
    {
        var result = new RuleEvaluationResult(
            byRule: new Dictionary<string, RuleOutcome>(),
            values: new Dictionary<string, ComputedValue>(),
            visibility: new Dictionary<string, VisibilityState>(),
            validations: Array.Empty<RuleOutcome>(),
            hasPending: false);

        Assert.False(result.IsSaveBlocked);
    }

    [Fact]
    public void A_failing_validation_alone_blocks_saving()
    {
        var validation = RuleOutcome.OfValidity(
            "amount.required",
            CellAddress.Field("amount"),
            Validity.Invalid(RuleError.Of("rule.required")));
        var result = new RuleEvaluationResult(
            byRule: new Dictionary<string, RuleOutcome>(),
            values: new Dictionary<string, ComputedValue>(),
            visibility: new Dictionary<string, VisibilityState>(),
            validations: new[] { validation },
            hasPending: false);

        Assert.True(result.IsSaveBlocked);
    }

    [Fact]
    public void A_pending_value_alone_blocks_saving()
    {
        var result = new RuleEvaluationResult(
            byRule: new Dictionary<string, RuleOutcome>(),
            values: new Dictionary<string, ComputedValue>
            {
                ["field:amount"] = ComputedValue.OfPending()
            },
            visibility: new Dictionary<string, VisibilityState>(),
            validations: Array.Empty<RuleOutcome>(),
            hasPending: true);

        Assert.True(result.IsSaveBlocked);
    }

    [Fact]
    public void One_errored_value_blocks_saving_beside_a_resolved_value()
    {
        var result = new RuleEvaluationResult(
            byRule: new Dictionary<string, RuleOutcome>(),
            values: new Dictionary<string, ComputedValue>
            {
                ["field:amount"] = ComputedValue.OfError(RuleError.Of("rule.type_error")),
                ["field:quantity"] = ComputedValue.Resolved(JsonValue.Create(7))
            },
            visibility: new Dictionary<string, VisibilityState>(),
            validations: Array.Empty<RuleOutcome>(),
            hasPending: false);

        Assert.True(result.IsSaveBlocked);
    }
}
