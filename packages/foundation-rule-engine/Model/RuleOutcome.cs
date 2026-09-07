using System.Text.Json.Nodes;

namespace Harborline.Api.Foundation.RuleEngine.Model;

/// <summary>
/// The output-type taxonomy of a <see cref="RuleOutcome"/> (SPINE-1 design §1.3).
/// Maps onto <see cref="Harborline.Api.Foundation.Forms.Models.RuleActionKind"/>:
/// <c>Compute → Value</c>, <c>Validate → Validity</c>,
/// <c>Visibility/Required/ReadOnly → Visibility</c>, <c>Presentation → Presentation</c>.
/// </summary>
public enum OutputType
{
    /// <summary>A computed value (<c>Compute</c>) — carries <see cref="RuleOutcome.Value"/>.</summary>
    Value = 0,

    /// <summary>A validation verdict (<c>Validate</c>) — carries <see cref="RuleOutcome.Validity"/>.</summary>
    Validity = 1,

    /// <summary>Show/hide/required/readonly state — carries <see cref="RuleOutcome.Visibility"/>.</summary>
    Visibility = 2,

    /// <summary>Style/badge/severity hint — carries <see cref="RuleOutcome.Presentation"/>.</summary>
    Presentation = 3,

    /// <summary>The available options of a choice field (<c>set-options</c>) — carries
    /// <see cref="RuleOutcome.Options"/>. The additive fifth OutputType (ADR 0140 D1 amendment;
    /// built ADR 0146 D2 Wave-1).</summary>
    Options = 4,
}

/// <summary>
/// State of a <see cref="ComputedValue"/> (SPINE-1 design §1.3, Decision DE).
/// </summary>
public enum ValueState
{
    /// <summary>The computed value is available (<see cref="ComputedValue.Value"/> populated).</summary>
    Resolved = 0,

    /// <summary>The rule threw (type error, division-by-zero) or read an upstream <see cref="Error"/>;
    /// errors propagate transitively.</summary>
    Error = 1,

    /// <summary>A dependency is not yet available. <b>Client-tier only</b>: a <see cref="Pending"/>
    /// reaching the synchronous .NET integrity tier at save is a fail-closed validation error.</summary>
    Pending = 2,
}

/// <summary>Severity of a <see cref="PresentationOutcome"/>.</summary>
public enum Severity
{
    /// <summary>Informational.</summary>
    Info = 0,

    /// <summary>Warning.</summary>
    Warn = 1,

    /// <summary>Error-level presentation (distinct from a hard <see cref="OutputType.Validity"/> failure).</summary>
    Error = 2,
}

/// <summary>
/// A localizable rule error — a stable <see cref="Code"/> + interpolation
/// <see cref="Params"/>, never English prose (memory: validation errors are codes).
/// The client keys a translated template off <see cref="Code"/>.
/// </summary>
/// <param name="Code">Stable, locale-independent error code (e.g. <c>rule.div_by_zero</c>,
/// <c>rule.type_error</c>, <c>rule.upstream_error</c>, <c>rule.cycle</c>, or a rule id for a
/// <c>Validate</c> failure).</param>
/// <param name="Params">Stringified interpolation parameters (the existing forms
/// <c>ValidationError.params</c> convention — scalars only).</param>
public sealed record RuleError(string Code, IReadOnlyDictionary<string, string> Params)
{
    /// <summary>An error with a code and no parameters.</summary>
    public static RuleError Of(string code) => new(code, EmptyParams);

    /// <summary>An error with a code and one parameter.</summary>
    public static RuleError Of(string code, string key, string value)
        => new(code, new Dictionary<string, string> { [key] = value });

    internal static readonly IReadOnlyDictionary<string, string> EmptyParams
        = new Dictionary<string, string>();
}

/// <summary>
/// A computed value outcome (SPINE-1 design §1.3). Exactly one of
/// <see cref="Value"/> / <see cref="Error"/> is populated per <see cref="State"/>.
/// </summary>
public sealed record ComputedValue(ValueState State, JsonNode? Value = null, RuleError? Error = null)
{
    /// <summary>A resolved value.</summary>
    public static ComputedValue Resolved(JsonNode? value) => new(ValueState.Resolved, value);

    /// <summary>An error value (propagating).</summary>
    public static ComputedValue OfError(RuleError error) => new(ValueState.Error, Error: error);

    /// <summary>A pending value (client tier only).</summary>
    public static ComputedValue OfPending() => new(ValueState.Pending);
}

/// <summary>A validation verdict (SPINE-1 design §1.3). <paramref name="Error"/> is null iff valid.</summary>
public sealed record Validity(bool Ok, RuleError? Error = null)
{
    /// <summary>A passing verdict.</summary>
    public static Validity Valid { get; } = new(true);

    /// <summary>A failing verdict carrying a localizable code.</summary>
    public static Validity Invalid(RuleError error) => new(false, error);
}

/// <summary>
/// The merged show/hide/required/readonly state for a cell (SPINE-1 design §1.3).
/// Defaults: visible, not-required, not-readonly.
/// </summary>
public sealed record VisibilityState(bool Visible = true, bool Required = false, bool ReadOnly = false);

/// <summary>A presentation hint outcome (SPINE-1 Decision DA).</summary>
public sealed record PresentationOutcome(
    Severity? Severity = null,
    Harborline.Api.Foundation.Forms.Models.InternationalizedText? Badge = null,
    string? StyleToken = null);

/// <summary>
/// The available-options outcome of a <c>set-options</c> rule (ADR 0146 D2 Wave-1). Mirrors
/// <see cref="ComputedValue"/>'s state machine: exactly one of <see cref="Options"/> /
/// <see cref="Error"/> is populated per <see cref="State"/>. The engine evaluates the rule's
/// expression to a JSON value; a JSON array resolves to its elements, any other type fails closed
/// with <see cref="RuleEngineCodes.OptionsNotArray"/>. Option <i>labelling</i> (i18n) is a
/// renderer/i18n-cascade concern, not a rule-engine one — the outcome carries raw JSON option values.
/// </summary>
public sealed record OptionsOutcome(ValueState State, IReadOnlyList<JsonNode?>? Options = null, RuleError? Error = null)
{
    /// <summary>A resolved options list.</summary>
    public static OptionsOutcome Resolved(IReadOnlyList<JsonNode?> options) => new(ValueState.Resolved, options);

    /// <summary>An error outcome (propagating; e.g. a non-array expression or an upstream error).</summary>
    public static OptionsOutcome OfError(RuleError error) => new(ValueState.Error, Error: error);

    /// <summary>A pending outcome (client tier only — a dependency is not yet available).</summary>
    public static OptionsOutcome OfPending() => new(ValueState.Pending);
}

/// <summary>
/// The single neutral evaluation result both tiers emit (SPINE-1 design §1.3).
/// Exactly one payload is populated, keyed by <see cref="OutputType"/>.
/// </summary>
public sealed record RuleOutcome
{
    private RuleOutcome(string ruleId, CellAddress target, OutputType outputType)
    {
        RuleId = ruleId;
        Target = target;
        OutputType = outputType;
    }

    /// <summary>The rule that produced this outcome.</summary>
    public string RuleId { get; private init; }

    /// <summary>The cell this outcome applies to (Row rules resolve per-row).</summary>
    public CellAddress Target { get; private init; }

    /// <summary>Which payload is populated.</summary>
    public OutputType OutputType { get; private init; }

    /// <summary>Populated iff <see cref="OutputType"/> is <see cref="OutputType.Value"/>.</summary>
    public ComputedValue? Value { get; private init; }

    /// <summary>Populated iff <see cref="OutputType"/> is <see cref="OutputType.Validity"/>.</summary>
    public Validity? Validity { get; private init; }

    /// <summary>Populated iff <see cref="OutputType"/> is <see cref="OutputType.Visibility"/>.</summary>
    public VisibilityState? Visibility { get; private init; }

    /// <summary>Populated iff <see cref="OutputType"/> is <see cref="OutputType.Presentation"/>.</summary>
    public PresentationOutcome? Presentation { get; private init; }

    /// <summary>Populated iff <see cref="OutputType"/> is <see cref="OutputType.Options"/>.</summary>
    public OptionsOutcome? Options { get; private init; }

    /// <summary>A <see cref="OutputType.Value"/> outcome.</summary>
    public static RuleOutcome OfValue(string ruleId, CellAddress target, ComputedValue value)
        => new(ruleId, target, OutputType.Value) { Value = value };

    /// <summary>A <see cref="OutputType.Validity"/> outcome.</summary>
    public static RuleOutcome OfValidity(string ruleId, CellAddress target, Validity validity)
        => new(ruleId, target, OutputType.Validity) { Validity = validity };

    /// <summary>A <see cref="OutputType.Visibility"/> outcome.</summary>
    public static RuleOutcome OfVisibility(string ruleId, CellAddress target, VisibilityState visibility)
        => new(ruleId, target, OutputType.Visibility) { Visibility = visibility };

    /// <summary>A <see cref="OutputType.Presentation"/> outcome.</summary>
    public static RuleOutcome OfPresentation(string ruleId, CellAddress target, PresentationOutcome presentation)
        => new(ruleId, target, OutputType.Presentation) { Presentation = presentation };

    /// <summary>A <see cref="OutputType.Options"/> outcome.</summary>
    public static RuleOutcome OfOptions(string ruleId, CellAddress target, OptionsOutcome options)
        => new(ruleId, target, OutputType.Options) { Options = options };
}
