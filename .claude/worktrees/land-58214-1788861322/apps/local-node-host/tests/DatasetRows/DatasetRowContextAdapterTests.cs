using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.RuleEngine;
using Harborline.Api.Foundation.RuleEngine.Context;

namespace Harborline.Api.LocalNodeHost.Tests.DatasetRows;

public sealed class DatasetRowContextAdapterTests
{
    [Fact]
    public void EvaluateGuard_ExistingComparisonOperatorReadsDatasetRowField()
    {
        var row = new Dictionary<string, JsonNode?>
        {
            ["amount"] = JsonValue.Create(125),
        };
        var rule = new RuleDefinition(
            Id: "view.amount-over-threshold",
            Tier: RuleTier.JsonLogic,
            Scope: RuleScope.Schema,
            ScopeTarget: string.Empty,
            Expression: "{\">\":[{\"var\":\"field.amount\"},100]}",
            Action: RuleActionKind.Validate);

        var result = new GuardEvaluator(clock: TimeProvider.System).EvaluateGuard(
            rule,
            new DatasetRowContextAdapter(row),
            RuleEvalScope.Root);

        Assert.True(result.Ok);
    }
}
