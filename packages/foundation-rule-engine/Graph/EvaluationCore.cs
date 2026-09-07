using System.Text.Json.Nodes;

using Harborline.Api.Foundation.RuleEngine.Compilation;
using Harborline.Api.Foundation.RuleEngine.Evaluation;
using Harborline.Api.Foundation.RuleEngine.Model;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.RuleEngine.Graph;

/// <summary>The sentinel a corpus / async tier uses to mark an unresolved (Pending) value.</summary>
internal static class PendingSentinel
{
    public static bool Is(JsonNode? n) =>
        n is JsonObject o && o.Count == 1 && o.TryGetPropertyValue("@pending", out var v)
        && v is JsonValue jv && jv.TryGetValue<bool>(out var b) && b;
}

/// <summary>
/// Resolves a rule's <c>var</c>/<c>agg</c> references against already-evaluated
/// computed cells + the raw instance (SPINE-1 design §2.2). Error/Pending of an
/// upstream computed cell propagate to the dependent.
/// </summary>
internal sealed class CellResolver : IValueResolver
{
    private readonly IReadOnlyDictionary<string, ComputedValue> _computed;
    private readonly RuleInstance _instance;
    private readonly string? _rowSection;
    private readonly string? _rowId;

    public CellResolver(IReadOnlyDictionary<string, ComputedValue> computed, RuleInstance instance,
        string? rowSection = null, string? rowId = null)
    {
        _computed = computed;
        _instance = instance;
        _rowSection = rowSection;
        _rowId = rowId;
    }

    public RefValue ResolveVar(string path)
    {
        if (path.StartsWith("row.", StringComparison.Ordinal))
        {
            if (_rowSection is null || _rowId is null) return RefValue.OfError(RuleError.Of(RuleEngineCodes.BadReference, "path", path));
            string field = path["row.".Length..];
            string rkey = CellAddress.Row(_rowSection, _rowId, field).Key;
            if (_computed.TryGetValue(rkey, out var rcv)) return FromComputed(rcv, rkey);
            return RawRowField(_rowSection, _rowId, field);
        }
        // "field.x" and a bare "x" (e.g. a missing/missing_some key) both address a top-level field.
        string name = path.StartsWith("field.", StringComparison.Ordinal) ? path["field.".Length..] : path;
        string key = CellAddress.Field(name).Key;
        if (_computed.TryGetValue(key, out var cv)) return FromComputed(cv, key);
        return RawField(name);
    }

    public RefValue ResolveAgg(string fn, string section, string col)
    {
        string key = CellAddress.TableAggregate(section, fn, col).Key;
        // An aggregate the graph never computed is unavailable data, not a value (ticket 162):
        // resolving it to null fabricated a result the preview could not compute. Refuse with the
        // one shared bad-reference shape. Since the compiler now refuses agg nodes it cannot
        // statically register, every compiled agg has a fold cell - this branch is defense-in-depth.
        return _computed.TryGetValue(key, out var cv)
            ? FromComputed(cv, key)
            : RefValue.UnavailableAggregate(fn, section, col);
    }

    private RefValue RawField(string name)
    {
        if (_instance.Fields.TryGetValue(name, out var v))
        {
            return PendingSentinel.Is(v) ? RefValue.Pending : RefValue.Resolved(v);
        }
        return RefValue.Resolved(null);
    }

    private RefValue RawRowField(string section, string rowId, string field)
    {
        if (_instance.Tables.TryGetValue(section, out var rows))
        {
            var row = rows.FirstOrDefault(r => r.Id == rowId);
            if (row is not null && row.Fields.TryGetValue(field, out var v))
            {
                return PendingSentinel.Is(v) ? RefValue.Pending : RefValue.Resolved(v);
            }
        }
        return RefValue.Resolved(null);
    }

    private static RefValue FromComputed(ComputedValue cv, string key) => cv.State switch
    {
        ValueState.Resolved => RefValue.Resolved(cv.Value),
        ValueState.Pending => RefValue.Pending,
        _ => RefValue.OfError(RuleError.Of(RuleEngineCodes.UpstreamError, "cell", key)),
    };
}

/// <summary>Builds a single rule's <see cref="RuleOutcome"/> from its AST evaluation (SPINE-1 design §1.3).</summary>
internal static class OutcomeBuilder
{
    /// <summary>The result of building one outcome (carries whether it landed Pending, for the save gate).</summary>
    internal readonly record struct Built(RuleOutcome Outcome, bool Pending);

    public static Built Build(CompiledRule rule, CellAddress target, IValueResolver resolver, EvalContext ctx)
    {
        string ruleId = rule.Source.Id;
        try
        {
            var v = HarborlineJsonLogic.Evaluate(rule.Ast, ctx);
            return rule.OutputType switch
            {
                OutputType.Value => new Built(RuleOutcome.OfValue(ruleId, target, ComputedValue.Resolved(v)), false),
                OutputType.Validity => new Built(RuleOutcome.OfValidity(ruleId, target,
                    HarborlineJsonLogic.IsTruthy(v) ? Validity.Valid : Validity.Invalid(RuleError.Of(ruleId))), false),
                OutputType.Visibility => new Built(RuleOutcome.OfVisibility(ruleId, target,
                    VisibilityFor(rule.Source.Action, HarborlineJsonLogic.IsTruthy(v))), false),
                OutputType.Presentation => new Built(RuleOutcome.OfPresentation(ruleId, target,
                    HarborlineJsonLogic.IsTruthy(v) ? PresentationFor(rule.Source) : new PresentationOutcome()), false),
                OutputType.Options => new Built(RuleOutcome.OfOptions(ruleId, target, OptionsFor(v)), false),
                _ => throw new InvalidOperationException("unreachable"),
            };
        }
        catch (RulePendingException)
        {
            return rule.OutputType switch
            {
                OutputType.Value => new Built(RuleOutcome.OfValue(ruleId, target, ComputedValue.OfPending()), true),
                // On the synchronous .NET integrity tier a pending dependency at save is fail-closed.
                OutputType.Validity => new Built(RuleOutcome.OfValidity(ruleId, target,
                    Validity.Invalid(RuleError.Of(RuleEngineCodes.PendingAtSave))), true),
                // Ticket 150 / ADR 0038: Required is a RESTRICT — a Required rule that cannot
                // render a verdict must REFUSE (a failing validity naming the rule), never relax
                // to not-required. Permits (Visibility/ReadOnly) still fail closed by hiding.
                OutputType.Visibility when rule.Source.Action == RuleActionKind.Required =>
                    new Built(RuleOutcome.OfValidity(ruleId, target,
                        Validity.Invalid(RuleError.Of(RuleEngineCodes.PendingAtSave, "rule", ruleId))), true),
                OutputType.Visibility => new Built(RuleOutcome.OfVisibility(ruleId, target, FailClosedVisibility(rule.Source.Action)), true),
                OutputType.Options => new Built(RuleOutcome.OfOptions(ruleId, target, OptionsOutcome.OfPending()), true),
                _ => new Built(RuleOutcome.OfPresentation(ruleId, target, new PresentationOutcome()), true),
            };
        }
        catch (RuleEvalException ex)
        {
            return rule.OutputType switch
            {
                OutputType.Value => new Built(RuleOutcome.OfValue(ruleId, target, ComputedValue.OfError(ex.Error)), false),
                OutputType.Validity => new Built(RuleOutcome.OfValidity(ruleId, target, Validity.Invalid(ex.Error)), false),
                // Ticket 150 / ADR 0038: a broken Required rule refuses rather than relaxes (above).
                // The refusal names the rule in its params, matching the pending arm.
                OutputType.Visibility when rule.Source.Action == RuleActionKind.Required =>
                    new Built(RuleOutcome.OfValidity(ruleId, target, Validity.Invalid(WithRule(ex.Error, ruleId))), false),
                OutputType.Visibility => new Built(RuleOutcome.OfVisibility(ruleId, target, FailClosedVisibility(rule.Source.Action)), false),
                OutputType.Options => new Built(RuleOutcome.OfOptions(ruleId, target, OptionsOutcome.OfError(ex.Error)), false),
                _ => new Built(RuleOutcome.OfPresentation(ruleId, target, new PresentationOutcome(Severity.Error)), false),
            };
        }
    }

    /// <summary>The error with <c>Params["rule"]</c> attached (no-op when already present).</summary>
    private static RuleError WithRule(RuleError error, string ruleId)
    {
        if (error.Params.ContainsKey("rule")) return error;
        var withRule = new Dictionary<string, string>(error.Params.Count + 1, StringComparer.Ordinal);
        foreach (var (key, value) in error.Params) withRule[key] = value;
        withRule["rule"] = ruleId;
        return new RuleError(error.Code, withRule);
    }

    // A set-options rule's expression must evaluate to a JSON array; any other type fails closed
    // (ADR 0146 D2). Elements are detached (DeepClone) so the resolved list owns them.
    private static OptionsOutcome OptionsFor(JsonNode? v)
        => v is JsonArray arr
            ? OptionsOutcome.Resolved(arr.Select(e => e?.DeepClone()).ToList())
            : OptionsOutcome.OfError(RuleError.Of(RuleEngineCodes.OptionsNotArray));

    private static VisibilityState VisibilityFor(RuleActionKind action, bool result) => action switch
    {
        RuleActionKind.Required => new VisibilityState(Required: result),
        RuleActionKind.ReadOnly => new VisibilityState(ReadOnly: result),
        _ => new VisibilityState(Visible: result), // Visibility
    };

    // Fail-closed-safe state when a visibility-family PERMIT errors: hide / readonly. A broken
    // Required rule never reaches here — Required is a restrict, so it refuses via a failing
    // Validity outcome instead of relaxing (ticket 150; ADR 0038 as corrected: permits may fail
    // open, restricts must refuse).
    private static VisibilityState FailClosedVisibility(RuleActionKind action) => action switch
    {
        RuleActionKind.ReadOnly => new VisibilityState(ReadOnly: true),
        _ => new VisibilityState(Visible: false),
    };

    private static PresentationOutcome PresentationFor(RuleDefinition rule)
    {
        var hint = rule.Presentation;
        if (hint is null) return new PresentationOutcome();
        return new PresentationOutcome(
            hint.Severity switch
            {
                "info" => Severity.Info,
                "warn" => Severity.Warn,
                "error" => Severity.Error,
                _ => null,
            },
            hint.Badge,
            hint.StyleToken);
    }
}
