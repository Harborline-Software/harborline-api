using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.RuleEngine.Compilation;

namespace Harborline.Api.Foundation.Forms.Engine;

/// <summary>
/// F3 (deep review of earlier repository PR #1671) — the rule-compile ADMISSION gate.
/// Compiles every Tier-2 rule expression AND every page <c>VisibleWhen</c> guard
/// with the node's own compiler at definition-save, throwing a
/// <see cref="FormDefinitionValidationException"/> with a stable
/// <c>form.rules.*</c> code on failure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why at admission.</b> <see cref="SubmitValidationGate"/> REFUSES the write
/// when the rule set OR a page guard fails to compile (ticket 150; ADR 0038 as
/// corrected — a rule that cannot be interpreted refuses rather than being skipped),
/// and the renderer fail-closes an erroring guard to hidden. Rejecting the uncompilable definition
/// at the door means users never reach that submit refusal for a route-published
/// definition: the author fixes the rule at publish, where the fault is
/// attributable and actionable. The gate's refusal remains the backstop for a
/// definition that arrives by any other path (pack install, sync, pre-admission
/// data). Wall-clock timeouts stay infrastructure faults that propagate, never
/// verdicts (D1).
/// </para>
/// <para>
/// <b>Same compile path as runtime.</b> Rules compile via
/// <see cref="RuleCompiler.Compile(System.Collections.Generic.IReadOnlyList{RuleDefinition}, Harborline.Api.Foundation.RuleEngine.RuleEngineLimits?)"/>
/// over the WHOLE rule set (so cross-rule faults — cycles, depth — reject too);
/// each guard compiles wrapped in the exact one-node <c>Validate</c> shape
/// <see cref="SubmitValidationGate"/>, the wizard, and the engine's
/// <c>GuardEvaluator</c> all use. Tier-1 (<see cref="RuleTier.JsonSchema"/>)
/// rules are skipped by the compiler itself (the kernel schema registry owns
/// them); Tier-3 (<see cref="RuleTier.PowerFx"/>) rules are consequently
/// REJECTED at this authoring surface — v1 never evaluates them, so admitting
/// one would degrade the whole submit gate silently.
/// </para>
/// </remarks>
public static class RuleCompileAdmission
{
    /// <summary>
    /// Compiles the definition's Tier-2 rules + page guards; throws
    /// <see cref="FormDefinitionValidationException"/> (code
    /// <see cref="FormDefinitionCodes.RulesUncompilable"/> /
    /// <see cref="FormDefinitionCodes.RulesGuardUncompilable"/>) on the first failure.
    /// </summary>
    public static void ValidateOrThrow(FormDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var overlay = definition.Overlay;

        if (overlay.Rules is { Count: > 0 } rules)
        {
            try
            {
                _ = RuleCompiler.Compile(rules);
            }
            catch (RuleCompilationException ex)
            {
                var which = ex.RuleId is { Length: > 0 } ruleId ? $"rule '{ruleId}'" : "the rule set";
                throw new FormDefinitionValidationException(
                    definition.Id,
                    $"{which} does not compile ({ex.Code}): {ex.Message}",
                    FormDefinitionCodes.RulesUncompilable);
            }
        }

        foreach (var page in overlay.Pages ?? Array.Empty<FormPage>())
        {
            if (page.VisibleWhen is not { } guard || string.IsNullOrWhiteSpace(guard))
            {
                continue;
            }

            // The exact wrap SubmitValidationGate / GuardEvaluator / the wizard use —
            // admission compiles what runtime will actually run.
            var guardRule = new RuleDefinition(
                Id: $"page-guard:{page.Id}",
                Tier: RuleTier.JsonLogic,
                Scope: RuleScope.Schema,
                ScopeTarget: string.Empty,
                Expression: guard,
                Action: RuleActionKind.Validate);
            try
            {
                _ = RuleCompiler.Compile(new[] { guardRule });
            }
            catch (RuleCompilationException ex)
            {
                throw new FormDefinitionValidationException(
                    definition.Id,
                    $"page '{page.Id}' has a VisibleWhen guard that does not compile ({ex.Code}): {ex.Message}",
                    FormDefinitionCodes.RulesGuardUncompilable);
            }
        }
    }
}
