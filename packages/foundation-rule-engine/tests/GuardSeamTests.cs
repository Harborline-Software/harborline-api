using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.RuleEngine.Tests;

public sealed class GuardSeamTests
{
    // T-687: the seam, not each caller, owns the fail-closed guarantee, so a new caller cannot reintroduce the throw.
    [Fact]
    public void T687_uncompilable_guard_is_invalid_at_the_seam_not_a_throw()
    {
        var rule = new RuleDefinition(
            Id: "t687.guard",
            Tier: RuleTier.JsonLogic,
            Scope: RuleScope.Schema,
            ScopeTarget: string.Empty,
            Expression: "this is not JsonLogic",
            Action: RuleActionKind.Validate);

        var verdict = new GuardEvaluator(clock: TimeProvider.System)
            .EvaluateGuardAt(rule, new Dictionary<string, JsonNode?>(), DateTimeOffset.UnixEpoch);

        Assert.False(verdict.Ok);
        Assert.Equal(RuleEngineCodes.CompileInvalidExpression, verdict.Error!.Code);
        Assert.DoesNotContain("this is not JsonLogic", verdict.Error.Params.Values);
    }
}
