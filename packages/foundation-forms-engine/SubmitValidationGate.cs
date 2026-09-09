using System.Text.Json;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.RuleEngine;
using Harborline.Api.Foundation.RuleEngine.Compilation;
using Harborline.Api.Foundation.RuleEngine.Graph;
using Harborline.Api.Foundation.RuleEngine.Model;

namespace Harborline.Api.Foundation.Forms.Engine;

/// <summary>
/// F-20 — the node-side submit-time rule gate. Re-derives, ON THE NODE, the same
/// visibility / validity verdicts the client renderer projects, so submit-time
/// validation has PARITY with what the user saw (fail-closed, never trust the
/// client):
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>The invariant — required respects visibility/enablement.</b>
///   A field hidden by a Visibility rule, in a hidden section, or on a page whose
///   guard is false NEVER blocks submit; a rule-disabled (read-only) field's
///   <c>required</c> never blocks either. The gate reports the hidden/read-only sets
///   so the engine can prune and filter.</description></item>
/// <item><description><b>Hidden-value pruning (D3 minimization).</b> The submitted
///   payload keeps only values visible at submit; the gate names the top-level keys
///   to prune (hidden flat fields + the container keys of hidden sections/pages).
///   Pruning nested rows INSIDE a visible collection is the item-5 grid seam.</description></item>
/// <item><description><b>Tier-2 expression validity.</b> Every failing
///   <see cref="OutputType.Validity"/> outcome (field-scope validity rules, page
///   checks, schema-scope checks) becomes a submit blocker with its stable code —
///   EXCEPT rules targeting hidden cells and checks bound only to hidden pages
///   (a rule the user never saw must not block).</description></item>
/// <item><description><b>Rule-driven required.</b> A <c>Required</c>-action rule's
///   merged state is enforced here too (the JSON-Schema <c>required</c> array only
///   carries statically-required fields).</description></item>
/// </list>
/// <para>
/// Fail-closed posture (ticket 150; ADR 0038 as corrected — "a rule that cannot be
/// interpreted refuses the write rather than being skipped"): a rule set OR page guard
/// that fails to COMPILE refuses the submit with a <see cref="ValidationError"/> naming
/// the responsible definition (<c>RuleCompileAdmission</c> makes this unreachable for
/// route-published definitions; a definition arriving by any other path — pack
/// install, sync, pre-admission data — still refuses here). A rule that ERRORS AT
/// EVALUATION is fail-closed per the engine's own outcome semantics (a guard whose
/// EXPRESSION errors at evaluation hides its page; an erroring validity or Required
/// rule blocks; an errored or pending computed value blocks unless its cell is on a
/// branched-away page). A wall-clock timeout (<c>RuleEngineTimeoutException</c>)
/// PROPAGATES on EVERY path — rule-graph evaluation and page-guard evaluation alike:
/// per the D1 ratification (2026-07-01) it is a non-authoritative infrastructure fault
/// that must never become an evaluation outcome — so the request fails (the write is
/// still refused, fail closed) without synthesizing a verdict. Async lookup checks
/// (<see cref="AsyncValidationCheck"/>) are NOT re-executed node-side in this slice —
/// config is validated at admission; connector re-execution is the documented follow-up.
/// </para>
/// </remarks>
internal static class SubmitValidationGate
{
    /// <summary>What the gate derived from the candidate + definition.</summary>
    internal sealed record GateResult(
        IReadOnlySet<string> PrunedKeys,
        IReadOnlySet<string> HiddenFields,
        IReadOnlySet<string> ReadOnlyFields,
        IReadOnlyList<ValidationError> RuleErrors)
    {
        public static readonly GateResult Empty = new(
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            Array.Empty<ValidationError>());
    }

    /// <summary>
    /// Evaluates the definition's rules + page guards over the candidate and derives
    /// the hidden/read-only/prune sets plus the Tier-2 validity blockers.
    /// </summary>
    public static GateResult Evaluate(
        FormDefinition formDef,
        JsonDocument candidate,
        TimeProvider clock,
        CancellationToken ct)
    {
        var overlay = formDef.Overlay;
        var hasRules = overlay.Rules is { Count: > 0 };
        var hasGuardedPages = overlay.Pages is { Count: > 0 } && overlay.Pages.Any(p => p.VisibleWhen is not null);
        var hasRequiredOrValidity = hasRules;
        if (!hasRules && !hasGuardedPages)
        {
            return GateResult.Empty;
        }

        if (candidate.RootElement.ValueKind != JsonValueKind.Object)
        {
            // Not an object — schema validation owns that rejection; nothing to derive.
            return GateResult.Empty;
        }

        var bodyObj = JsonNode.Parse(candidate.RootElement.GetRawText()) as JsonObject ?? new JsonObject();

        // ── 1. Evaluate the Tier-2 rule graph over the candidate (same seam as render). ──
        RuleEvaluationResult? result = null;
        if (hasRules)
        {
            try
            {
                var compiled = RuleCompiler.Compile(overlay.Rules);
                if (compiled.RuleCount > 0)
                {
                    result = new FormRuleGraph(compiled, RuleEngineLimits.Default, clock)
                        .EvaluateInstance(RuleInstance.FromJson(bodyObj), ct);
                }
            }
            catch (RuleCompilationException ex)
            {
                // Ticket 150 / ADR 0038 (corrected): fail closed at validate — a rule that cannot
                // be interpreted REFUSES the write rather than being skipped. The old degrade to
                // schema-only silently stopped restricting. The refusal names the responsible
                // definition so the reason is visible and actionable. One condition, one code,
                // both doors: the SAME contract constant the definition-save admission uses.
                // The "(rule set)" fallback is prose only — Params carry a rule entry only when
                // the fault is attributable to a single rule.
                var ruleId = ex.RuleId is { Length: > 0 } id ? id : null;
                var errorParams = new Dictionary<string, string> { ["code"] = ex.Code };
                if (ruleId is not null)
                {
                    errorParams["rule"] = ruleId;
                }
                return GateResult.Empty with
                {
                    RuleErrors = new[]
                    {
                        new ValidationError(
                            string.Empty,
                            $"rule '{ruleId ?? "(rule set)"}' cannot be interpreted ({ex.Code}); the write is refused (fail closed).",
                            ValidationErrorKind.Schema,
                            Code: FormDefinitionCodes.RulesUncompilable,
                            Params: errorParams),
                    },
                };
            }
            // RuleEngineTimeoutException is deliberately NOT caught (ticket 150): the wall-clock
            // liveness guard is a non-authoritative infrastructure fault (D1 ratification
            // 2026-07-01) that must never become an evaluation outcome — but swallowing it here
            // degraded a restricting rule set to schema-only and ACCEPTED the write (fail open).
            // Propagating fails the request instead: the write is refused on infrastructure
            // without synthesizing a verdict — fail closed (ADR 0038) without violating D1.
        }

        // ── 2. Hidden/read-only cells from the merged visibility states. ─────────────
        var hiddenFields = new HashSet<string>(StringComparer.Ordinal);
        var readOnlyFields = new HashSet<string>(StringComparer.Ordinal);
        var requiredByRule = new HashSet<string>(StringComparer.Ordinal);
        var hiddenSections = new HashSet<string>(StringComparer.Ordinal);

        if (result is not null)
        {
            foreach (var (key, vis) in result.Visibility)
            {
                if (key.StartsWith("field:", StringComparison.Ordinal))
                {
                    var name = key["field:".Length..];
                    if (!vis.Visible)
                    {
                        hiddenFields.Add(name);
                    }
                    if (vis.ReadOnly)
                    {
                        readOnlyFields.Add(name);
                    }
                    if (vis.Required && vis.Visible && !vis.ReadOnly)
                    {
                        requiredByRule.Add(name);
                    }
                }
                else if (key.StartsWith("section:", StringComparison.Ordinal) && !vis.Visible)
                {
                    hiddenSections.Add(key["section:".Length..]);
                }
            }
        }

        // ── 3. Page guards. A guard whose EXPRESSION errors at evaluation semantics (bad
        // reference, type error, budget) returns Ok=false inside EvaluateGuard — fail-closed
        // to hidden, the pre-existing intended posture. A guard that does not COMPILE refuses
        // the write below (ticket 150 review: "page hidden" on a compile fault pruned the
        // page's fields and skipped its checks — write accepted on an infrastructure fault).
        // A wall-clock timeout (RuleEngineTimeoutException) PROPAGATES (D1): infrastructure,
        // never a verdict.
        var hiddenPages = new HashSet<string>(StringComparer.Ordinal);
        if (hasGuardedPages)
        {
            var guardEvaluator = new GuardEvaluator(clock: clock);
            var contextBag = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var (name, node) in bodyObj)
            {
                contextBag[name] = node;
            }

            foreach (var page in overlay.Pages!)
            {
                if (page.VisibleWhen is not { } guard)
                {
                    continue;
                }

                var guardId = $"page-guard:{page.Id}";
                bool visible;
                try
                {
                    var guardRule = new RuleDefinition(
                        Id: guardId,
                        Tier: RuleTier.JsonLogic,
                        Scope: RuleScope.Schema,
                        ScopeTarget: string.Empty,
                        Expression: guard,
                        Action: RuleActionKind.Validate);
                    visible = guardEvaluator.EvaluateGuard(guardRule, contextBag, ct).Ok;
                }
                catch (RuleCompilationException ex)
                {
                    // Same refusal path (and admission constant) as the rule-set compile fault:
                    // an uncompilable guard is an uninterpretable restriction, not a hidden page.
                    return GateResult.Empty with
                    {
                        RuleErrors = new[]
                        {
                            new ValidationError(
                                string.Empty,
                                $"page '{page.Id}' has a VisibleWhen guard that cannot be interpreted ({ex.Code}); "
                                + "the write is refused (fail closed).",
                                ValidationErrorKind.Schema,
                                Code: FormDefinitionCodes.RulesGuardUncompilable,
                                Params: new Dictionary<string, string>
                                {
                                    ["page"] = page.Id,
                                    ["rule"] = guardId,
                                    ["code"] = ex.Code,
                                }),
                        },
                    };
                }

                if (!visible)
                {
                    hiddenPages.Add(page.Id);
                    foreach (var sectionId in page.Sections)
                    {
                        hiddenSections.Add(sectionId);
                    }
                }
            }
        }

        // ── 4. Expand hidden sections to their fields + top-level container keys. ────
        var prunedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in overlay.Sections)
        {
            if (!hiddenSections.Contains(section.Id))
            {
                continue;
            }
            foreach (var field in section.Fields)
            {
                hiddenFields.Add(field);
            }
            foreach (var item in section.Items ?? Array.Empty<FormItem>())
            {
                if (item.Kind != FormItemKind.Field)
                {
                    prunedKeys.Add(item.Key); // group/collection/reference values nest under the item key.
                }
            }
        }
        foreach (var hidden in hiddenFields)
        {
            prunedKeys.Add(hidden);
        }

        // Ticket 150 (review): a rule-read-only field's submitted value is DISCARDED, mirroring
        // the hidden-field prune. The user could not edit the field, so a changed — possibly
        // constraint-violating — value is client-fabricated; a submit always creates a NEW
        // instance, so "revert to the stored value" is "revert to absent". Tier-1 constraint
        // errors for read-only fields are stripped downstream (the invariant), so without this
        // prune that stripped, un-validated value would PERSIST.
        foreach (var readOnly in readOnlyFields)
        {
            prunedKeys.Add(readOnly);
        }

        // ── 5. Tier-2 validity blockers + rule-driven required. ──────────────────────
        var errors = new List<ValidationError>();
        if (result is not null && hasRequiredOrValidity)
        {
            // Checks bound ONLY to hidden pages must not block (the user never reached them).
            var checksOnHiddenPages = new HashSet<string>(StringComparer.Ordinal);
            var checksOnVisiblePages = new HashSet<string>(StringComparer.Ordinal);
            foreach (var page in overlay.Pages ?? Array.Empty<FormPage>())
            {
                foreach (var checkId in page.Checks ?? Array.Empty<string>())
                {
                    if (hiddenPages.Contains(page.Id))
                    {
                        checksOnHiddenPages.Add(checkId);
                    }
                    else
                    {
                        checksOnVisiblePages.Add(checkId);
                    }
                }
            }

            foreach (var outcome in result.Validations)
            {
                if (outcome.Validity is not { Ok: false } validity)
                {
                    continue;
                }

                var target = outcome.Target.Key;
                if (target.StartsWith("field:", StringComparison.Ordinal)
                    && hiddenFields.Contains(target["field:".Length..]))
                {
                    continue; // a hidden field's validity never blocks (the invariant).
                }
                if (target.StartsWith("section:", StringComparison.Ordinal)
                    && hiddenSections.Contains(target["section:".Length..]))
                {
                    continue;
                }
                if (checksOnHiddenPages.Contains(outcome.RuleId)
                    && !checksOnVisiblePages.Contains(outcome.RuleId)
                    // Defense in depth (ticket 150 review): the engine's synthetic budget refusal
                    // must NEVER be skipped, even if an authored check id collides with its
                    // reserved RuleId — budget exhaustion is a whole-graph fault, not a page check.
                    && validity.Error?.Code != RuleEngineCodes.BudgetExceeded)
                {
                    continue; // a check bound only to a branched-away page never blocks.
                }

                // F-24 (item 5): anchor row-grain + aggregate validity to the cell/table (shared
                // pointer convention the client `validateScope` mirrors, so the tiers agree):
                //   field:x        → /x          row:s/r/f → /s/r/f (per-cell)
                //   agg:s/fn/col   → /s (the whole-table aggregate verdict, e.g. Σdebits=Σcredits)
                //   section:/schema: → "" (form-level)
                var pointer = CellTargetToPointer(target);
                var error = validity.Error ?? RuleError.Of(outcome.RuleId);
                errors.Add(new ValidationError(
                    pointer,
                    $"rule '{outcome.RuleId}' failed.",
                    ValidationErrorKind.Schema,
                    Code: error.Code,
                    Params: error.Params.Count > 0 ? error.Params : null));
            }

            // Rule-driven required (a Required-action rule's merged state): the JSON-Schema
            // `required` array only knows statically-required fields, so enforce here for
            // parity with the client projection. Hidden/read-only are already excluded.
            foreach (var name in requiredByRule)
            {
                if (IsEmptyCandidateValue(bodyObj, name))
                {
                    errors.Add(new ValidationError(
                        $"/{name}",
                        $"required: '{name}' is required.",
                        ValidationErrorKind.Schema,
                        Code: "required",
                        Params: new Dictionary<string, string> { ["field"] = name }));
                }
            }
        }

        // ── 6. The engine's block contract (ticket 150 review; RuleEvaluationResult.IsSaveBlocked,
        // SPINE-1 §1.3/§5.3): an ERRORED computed value (e.g. divide-by-zero) or a PENDING value
        // reaching this synchronous integrity tier is a fail-closed save blocker — the gate
        // previously read only result.Validations and let both pass. Consumed at cell grain from
        // the result's own Values states (IsSaveBlocked is the same signal, but scalar — it cannot
        // honor the hidden exemption), exempting only cells on genuinely branched-away pages /
        // hidden sections / hidden fields, the same grain as the check filter above.
        if (result is not null)
        {
            foreach (var (key, value) in result.Values)
            {
                if (value.State == ValueState.Resolved || IsHiddenCell(key, hiddenFields, hiddenSections))
                {
                    continue;
                }

                if (value.State == ValueState.Error)
                {
                    var error = value.Error ?? RuleError.Of(RuleEngineCodes.UpstreamError, "cell", key);
                    errors.Add(new ValidationError(
                        CellTargetToPointer(key),
                        $"computed value '{key}' errored ({error.Code}); the write is refused (fail closed).",
                        ValidationErrorKind.Schema,
                        Code: error.Code,
                        Params: error.Params.Count > 0 ? error.Params : null));
                }
                else // Pending — DE: a pending value at save is fail-closed on the .NET tier.
                {
                    errors.Add(new ValidationError(
                        CellTargetToPointer(key),
                        $"computed value '{key}' is pending at submit; the write is refused (fail closed).",
                        ValidationErrorKind.Schema,
                        Code: RuleEngineCodes.PendingAtSave,
                        Params: new Dictionary<string, string> { ["cell"] = key }));
                }
            }
        }

        return new GateResult(prunedKeys, hiddenFields, readOnlyFields, errors);
    }

    /// <summary>
    /// Prunes the named top-level keys from the candidate (F-20 hidden-value pruning —
    /// the submitted payload keeps only values visible at submit; D3 minimization).
    /// Returns a NEW document the caller owns; the input is untouched.
    /// </summary>
    public static JsonDocument Prune(JsonDocument candidate, IReadOnlySet<string> prunedKeys)
    {
        if (prunedKeys.Count == 0 || candidate.RootElement.ValueKind != JsonValueKind.Object)
        {
            return JsonDocument.Parse(candidate.RootElement.GetRawText());
        }

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in candidate.RootElement.EnumerateObject())
            {
                if (prunedKeys.Contains(property.Name))
                {
                    continue;
                }
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(buffer.WrittenMemory);
    }

    /// <summary>
    /// F-24 (item 5): maps a SPINE-1 cell-address key to the RFC-6901 JSON pointer into the
    /// candidate document, so a failing row/aggregate rule anchors to the cell / table the user
    /// sees. The client's <c>validateScope</c> uses the IDENTICAL mapping so the tiers' error
    /// pointers agree (the parity corpus pins this):
    /// <list type="bullet">
    ///   <item><c>field:x</c> → <c>/x</c></item>
    ///   <item><c>row:section/rowId/field</c> → <c>/section/rowId/field</c> (per-cell — <c>rowId</c>
    ///     is the row's <c>_id</c> or its array index)</item>
    ///   <item><c>agg:section/fn/col</c> → <c>/section</c> (the whole-table aggregate verdict)</item>
    ///   <item><c>section:</c> / <c>schema:</c> → <c>""</c> (form-level)</item>
    /// </list>
    /// </summary>
    private static string CellTargetToPointer(string target)
    {
        if (target.StartsWith("field:", StringComparison.Ordinal))
        {
            return $"/{target["field:".Length..]}";
        }
        if (target.StartsWith("row:", StringComparison.Ordinal))
        {
            // row:section/rowId/field → /section/rowId/field
            return "/" + target["row:".Length..];
        }
        if (target.StartsWith("agg:", StringComparison.Ordinal))
        {
            // agg:section/fn/col → /section (anchor the aggregate verdict to its table).
            var rest = target["agg:".Length..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            return slash > 0 ? "/" + rest[..slash] : string.Empty;
        }
        return string.Empty; // section: / schema: — a form-level error.
    }

    /// <summary>
    /// True when the cell lives on a hidden field / hidden section (including a section hidden
    /// because its page branched away) — the step-6 block-contract exemption, at the SAME grain
    /// as the step-5 validity filter. A <c>schema:</c> cell is never exempt.
    /// </summary>
    private static bool IsHiddenCell(string key, IReadOnlySet<string> hiddenFields, IReadOnlySet<string> hiddenSections)
    {
        if (key.StartsWith("field:", StringComparison.Ordinal))
        {
            return hiddenFields.Contains(key["field:".Length..]);
        }
        if (key.StartsWith("row:", StringComparison.Ordinal) || key.StartsWith("agg:", StringComparison.Ordinal))
        {
            // row:section/rowId/field | agg:section/fn/col — exempt when the owning section is hidden.
            var rest = key[(key.IndexOf(':', StringComparison.Ordinal) + 1)..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            return slash > 0 && hiddenSections.Contains(rest[..slash]);
        }
        if (key.StartsWith("section:", StringComparison.Ordinal))
        {
            return hiddenSections.Contains(key["section:".Length..]);
        }
        return false;
    }

    /// <summary>Mirror of the client's empty-value predicate (null / absent / "" / []).</summary>
    private static bool IsEmptyCandidateValue(JsonObject body, string name)
    {
        if (!body.TryGetPropertyValue(name, out var node) || node is null)
        {
            return true;
        }
        if (node is JsonValue value && value.TryGetValue<string>(out var s))
        {
            return s.Length == 0;
        }
        if (node is JsonArray arr)
        {
            return arr.Count == 0;
        }
        return false;
    }
}
