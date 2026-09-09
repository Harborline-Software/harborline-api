using System.Text;
using System.Globalization;

using Harborline.Api.Foundation.RuleEngine.Compilation;
using Harborline.Api.Foundation.RuleEngine.Model;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.RuleEngine.Explain;

// ─────────────────────────────────────────────────────────────────────────────
//  ADR 0146 D10 — interactive explainability traces (the Pilot "why is this field
//  hidden? / why did this route to a director?" substrate; the design constitution's
//  cognitive-accessibility respect).
//
//  A trace is a list of localizable CODE + PARAM entries (the ADR 0055
//  validation-codes doctrine — never embedded English). Every interactive / CP-gated
//  evaluation can carry one; per-row batch/streaming paths run traceless (they simply
//  do not build a trace) with a sampling hook (the sampler builds a trace on a sample,
//  identical shape).
//
//  Board F2 (BINDING) — the value-leak seam is closed BY CONSTRUCTION:
//    A trace is a pure projection over (a) the compiled rules' STATIC field references
//    (names) + (b) the built RuleOutcome (a code). It has NO access to a resolved
//    RefValue — the now-public IValueResolver/RefValue seam (slice 2) is never touched
//    here — so a field VALUE can never enter a trace param. The AUTHORITY FILTER then
//    gates the field REFERENCES: a shredded subject's refs darken to a reference+hash
//    form, and a viewer-unreadable field's ref redacts. Explainability is never a PBAC
//    or crypto-shred bypass.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Stable, locale-independent trace codes (ADR 0146 D10). Byte-identical to the TS
/// <c>RuleTraceCodes</c>. The client keys a localized template off the code; params interpolate.</summary>
public static class RuleTraceCodes
{
    public const string ValueComputed = "rule.trace.value_computed";
    public const string ValueError = "rule.trace.value_error";
    public const string Pending = "rule.trace.pending";
    public const string ValidationPassed = "rule.trace.validation_passed";
    public const string ValidationFailed = "rule.trace.validation_failed";
    public const string Shown = "rule.trace.shown";
    public const string Hidden = "rule.trace.hidden";
    public const string Required = "rule.trace.required";
    public const string NotRequired = "rule.trace.not_required";
    public const string ReadOnly = "rule.trace.readonly";
    public const string Editable = "rule.trace.editable";
    public const string OptionsSet = "rule.trace.options_set";
    public const string OptionsError = "rule.trace.options_error";
    public const string Presented = "rule.trace.presented";
    public const string NotPresented = "rule.trace.not_presented";
    public const string GuardPassed = "rule.trace.guard_passed";
    public const string GuardFailed = "rule.trace.guard_failed";
}

/// <summary>How a field reference appears in a trace (board F2). The filter decides; the builder renders.</summary>
public enum TraceFieldDisclosure
{
    /// <summary>The field reference is shown (the viewer may read it — the default).</summary>
    Show = 0,

    /// <summary>The field reference is redacted (the viewer is not authorized to read the field).</summary>
    Redact = 1,

    /// <summary>The field reference darkens to a reference+hash form (the data subject is crypto-shredded).</summary>
    Hash = 2,
}

/// <summary>
/// The authority filter that gates field references in a trace (board F2). Sees only field NAMES, never
/// values — it decides disclosure per field. A pillar supplies it from the viewing-principal / subject-key
/// context (the ADR 0146 D3 context-adapter is where that context is threaded). Default:
/// <see cref="PassThroughTraceFilter"/> (all fields shown — no authority context bound).
/// </summary>
public interface ITraceAuthorityFilter
{
    /// <summary>Decides how <paramref name="fieldName"/> appears in a trace param. NEVER receives a value.</summary>
    TraceFieldDisclosure Disclose(string fieldName);
}

/// <summary>The no-op filter — every field reference is shown. The default when no authority context is bound.</summary>
public sealed class PassThroughTraceFilter : ITraceAuthorityFilter
{
    public static PassThroughTraceFilter Instance { get; } = new();
    public TraceFieldDisclosure Disclose(string fieldName) => TraceFieldDisclosure.Show;
}

/// <summary>
/// One trace entry — a localizable code + params for one rule's outcome (ADR 0146 D10). Params are scalar
/// strings (the ADR 0055 convention): <c>rule</c> (the rule id), optional <c>reads</c> (the filtered field
/// references the rule read), and optional <c>cause</c> (an underlying error code for error/invalid outcomes).
/// A field VALUE never appears in <see cref="Params"/> — the builder cannot access one.
/// </summary>
public sealed record RuleTraceEntry(string RuleId, string Target, string Code, IReadOnlyDictionary<string, string> Params);

/// <summary>
/// Builds explainability traces as a pure projection over compiled rules + built outcomes (ADR 0146 D10 /
/// board F2). No evaluation, no resolver, no value access — structurally leak-free.
/// </summary>
public static class RuleTraceBuilder
{
    /// <summary>
    /// Builds the trace for a whole form evaluation — one entry per rule outcome. Call this on the
    /// interactive / CP-gated path; the batch path simply does not call it (traceless). The
    /// <paramref name="filter"/> gates each field reference; pass <see cref="PassThroughTraceFilter.Instance"/>
    /// when no authority context is bound.
    /// </summary>
    public static IReadOnlyList<RuleTraceEntry> BuildForm(
        CompiledGraph compiled, RuleEvaluationResult result, ITraceAuthorityFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(result);
        var f = filter ?? PassThroughTraceFilter.Instance;

        var rulesById = new Dictionary<string, CompiledRule>(StringComparer.Ordinal);
        foreach (var r in compiled.Rules) rulesById[r.Source.Id] = r;

        var entries = new List<RuleTraceEntry>();
        // Deterministic order (the ByRule dictionary order is not guaranteed): sort by rule key.
        foreach (var key in result.ByRule.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var outcome = result.ByRule[key];
            rulesById.TryGetValue(outcome.RuleId, out var rule);
            entries.Add(DescribeOutcome(key, outcome, rule, f));
        }
        return entries;
    }

    /// <summary>
    /// Builds a single trace entry for a workflow guard outcome (the <c>GuardEvaluator</c> arm). Re-derives
    /// the guard rule's field references from its definition (deterministic; the rule already compiled during
    /// evaluation). A compile failure degrades to an entry with no reads (never throws from the trace path).
    /// </summary>
    public static RuleTraceEntry BuildGuard(
        RuleDefinition rule, Validity outcome, ITraceAuthorityFilter? filter = null, RuleEngineLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(outcome);
        var f = filter ?? PassThroughTraceFilter.Instance;

        IReadOnlyList<string> reads = Array.Empty<string>();
        try
        {
            var compiled = RuleCompiler.Compile(new[] { rule }, limits ?? RuleEngineLimits.Default);
            if (compiled.Rules.Count > 0) reads = FieldRefsOf(compiled.Rules[0]);
        }
        catch (RuleCompilationException) { /* trace is best-effort — never throws */ }

        string code = outcome.Ok ? RuleTraceCodes.GuardPassed : RuleTraceCodes.GuardFailed;
        var p = BaseParams(rule.Id, reads, f);
        if (!outcome.Ok && outcome.Error is not null) p["cause"] = outcome.Error.Code;
        return new RuleTraceEntry(rule.Id, "guard:" + rule.Id, code, p);
    }

    private static RuleTraceEntry DescribeOutcome(string key, RuleOutcome outcome, CompiledRule? rule, ITraceAuthorityFilter f)
    {
        IReadOnlyList<string> reads = rule is not null ? FieldRefsOf(rule) : Array.Empty<string>();
        var p = BaseParams(outcome.RuleId, reads, f);

        string code;
        switch (outcome.OutputType)
        {
            case OutputType.Value:
                code = outcome.Value!.State switch
                {
                    ValueState.Resolved => RuleTraceCodes.ValueComputed,
                    ValueState.Pending => RuleTraceCodes.Pending,
                    _ => Cause(p, outcome.Value.Error, RuleTraceCodes.ValueError),
                };
                break;
            case OutputType.Validity:
                code = outcome.Validity!.Ok
                    ? RuleTraceCodes.ValidationPassed
                    : Cause(p, outcome.Validity.Error, RuleTraceCodes.ValidationFailed);
                break;
            case OutputType.Visibility:
                code = VisibilityCode(rule?.Source.Action ?? RuleActionKind.Visibility, outcome.Visibility!);
                break;
            case OutputType.Options:
                code = outcome.Options!.State switch
                {
                    ValueState.Resolved => RuleTraceCodes.OptionsSet,
                    ValueState.Pending => RuleTraceCodes.Pending,
                    _ => Cause(p, outcome.Options.Error, RuleTraceCodes.OptionsError),
                };
                break;
            default: // Presentation
                code = outcome.Presentation!.Severity is not null ? RuleTraceCodes.Presented : RuleTraceCodes.NotPresented;
                break;
        }
        return new RuleTraceEntry(outcome.RuleId, outcome.Target.Key, code, p);
    }

    private static string VisibilityCode(RuleActionKind action, VisibilityState v) => action switch
    {
        RuleActionKind.Required => v.Required ? RuleTraceCodes.Required : RuleTraceCodes.NotRequired,
        RuleActionKind.ReadOnly => v.ReadOnly ? RuleTraceCodes.ReadOnly : RuleTraceCodes.Editable,
        _ => v.Visible ? RuleTraceCodes.Shown : RuleTraceCodes.Hidden,
    };

    private static string Cause(Dictionary<string, string> p, RuleError? error, string code)
    {
        if (error is not null) p["cause"] = error.Code;
        return code;
    }

    private static Dictionary<string, string> BaseParams(string ruleId, IReadOnlyList<string> reads, ITraceAuthorityFilter f)
    {
        var p = new Dictionary<string, string>(StringComparer.Ordinal) { ["rule"] = ruleId };
        if (reads.Count > 0)
        {
            // Field references are disclosed per the authority filter — the value-leak seam is already
            // closed (there is no value here); this darkens the REFERENCE for unreadable / shredded subjects.
            var disclosed = reads.Select(name => Render(name, f.Disclose(name)));
            p["reads"] = string.Join(",", disclosed);
        }
        return p;
    }

    private static string Render(string fieldName, TraceFieldDisclosure disclosure) => disclosure switch
    {
        TraceFieldDisclosure.Redact => "[redacted]",
        TraceFieldDisclosure.Hash => "#" + Fnv1a.Hash8(fieldName),
        _ => fieldName,
    };

    // The static field references a rule reads, in first-seen order, de-duplicated. Internal RuleRef
    // subtypes (same assembly) — extracts the readable field/column name.
    private static IReadOnlyList<string> FieldRefsOf(CompiledRule rule)
    {
        var seen = new List<string>();
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rf in rule.References)
        {
            string? name = rf switch
            {
                FieldRef fr => fr.Name,
                RowFieldRef rr => rr.Field,
                AggRef ar => ar.Col,
                _ => null,
            };
            if (name is not null && set.Add(name)) seen.Add(name);
        }
        return seen;
    }
}

/// <summary>A tiny FNV-1a 32-bit hash — byte-identical to the TS tier — used only to darken a field
/// reference to an opaque, stable token for a crypto-shredded subject (board F2). Not a security primitive:
/// the subject's data is already destroyed; this only avoids echoing a bare field name in a shredded trace.</summary>
internal static class Fnv1a
{
    public static string Hash8(string s)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        uint h = offset;
        foreach (char c in s)
        {
            h ^= (byte)(c & 0xFF);
            h *= prime;
            h ^= (byte)((c >> 8) & 0xFF);
            h *= prime;
        }
        return h.ToString("x8", CultureInfo.InvariantCulture);
    }
}
