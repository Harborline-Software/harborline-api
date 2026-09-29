using Harborline.Api.Foundation.Forms.Models;

using ContractRule = Harborline.Contracts.Forms.RuleDefinition;

namespace Harborline.Api.Foundation.Forms.Engine;

/// <summary>
/// T-304: the api Forms rule model as the platform contract the platform rule runtime compiles. The submit gate,
/// the render projection and definition-save admission all cross here, so the three compile the same rules.
/// </summary>
internal static class PlatformRuleContract
{
    // Only the fields the compiler reads cross; the enums map by member name, so a renamed member fails loudly
    // instead of shifting.
    internal static ContractRule ToContract(RuleDefinition rule) => new()
    {
        Id = rule.Id,
        Tier = Enum.Parse<Harborline.Contracts.Forms.RuleTier>(rule.Tier.ToString()),
        Scope = Enum.Parse<Harborline.Contracts.Forms.RuleScope>(rule.Scope.ToString()),
        ScopeTarget = rule.ScopeTarget,
        Expression = rule.Expression,
        Action = Enum.Parse<Harborline.Contracts.Forms.RuleActionKind>(rule.Action.ToString()),
    };

    internal static ContractRule[] ToContract(IEnumerable<RuleDefinition> rules) => rules.Select(ToContract).ToArray();

    /// <summary>The one-node <c>Validate</c> shape a page <c>VisibleWhen</c> guard compiles and evaluates as.</summary>
    internal static ContractRule PageGuard(string pageId, string guard) => new()
    {
        Id = $"page-guard:{pageId}",
        Tier = Harborline.Contracts.Forms.RuleTier.JsonLogic,
        Scope = Harborline.Contracts.Forms.RuleScope.Schema,
        ScopeTarget = string.Empty,
        Expression = guard,
        Action = Harborline.Contracts.Forms.RuleActionKind.Validate,
    };
}
