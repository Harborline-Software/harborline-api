namespace Harborline.Api.Foundation.Forms.Engine;

/// <summary>
/// Outcome of <see cref="IFormEngine.ValidateAsync"/>. Distinct from the kernel
/// registry's <see cref="Harborline.Api.Kernel.Schema.SchemaValidationResult"/>: this
/// forms-tier result additionally surfaces resource-bound and authorization
/// failures (<see cref="ValidationErrorKind"/>) so a caller can distinguish a
/// schema-shape failure from a DoS-guard trip or an authz denial.
/// </summary>
public sealed record ValidationResult(bool IsValid, IReadOnlyList<ValidationError> Errors)
{
    /// <summary>A successful validation with no errors.</summary>
    public static ValidationResult Valid { get; } = new(true, Array.Empty<ValidationError>());

    /// <summary>Builds a failed result from one or more errors.</summary>
    public static ValidationResult Invalid(params ValidationError[] errors)
        => new(false, errors);

    /// <summary>Builds a failed result from an error list.</summary>
    public static ValidationResult Invalid(IReadOnlyList<ValidationError> errors)
        => new(false, errors);
}

/// <summary>A single validation failure, located by JSON Pointer.</summary>
/// <param name="JsonPointer">RFC 6901 pointer into the candidate document (empty string = document root).</param>
/// <param name="Message">
/// Human-readable English failure description — the FALLBACK a client renders when it has no
/// localized template for <paramref name="Code"/>. The engine no longer treats this as the only
/// surface: a localizing client (SchemaForm / Harborline App) keys a translated template off
/// <paramref name="Code"/> and interpolates <paramref name="Params"/> instead.
/// </param>
/// <param name="Kind">The category of failure.</param>
/// <param name="Code">
/// Stable, locale-independent failure code — an OPEN set, not a closed list. For a schema failure
/// it is the failing JSON-Schema keyword (<c>required</c> / <c>type</c> / <c>minimum</c> /
/// <c>maximum</c> / <c>enum</c> / <c>minLength</c> / <c>maxLength</c> / <c>pattern</c> /
/// <c>additional-properties</c> / …); for an engine-raised failure it is an engine code
/// (<c>not-object</c> / <c>candidate-too-large</c> / <c>validation-budget-exceeded</c> /
/// <c>pattern-timeout</c> / <c>form-not-found</c> / <c>invalid</c>); the submit-time rule gate
/// additionally emits the shared contract constants (<c>form.rules.uncompilable</c> /
/// <c>form.rules.guard_uncompilable</c> — the SAME codes definition-save admission uses) and the
/// rule engine's <c>rule.*</c> outcome codes. Null only for a legacy error with no resolvable code.
/// </param>
/// <param name="Params">
/// The error's structured values for client interpolation — e.g. <c>{ "min": "1" }</c>,
/// <c>{ "max": "5" }</c>, <c>{ "allowed": "[\"pass\",\"fail\"]" }</c>, <c>{ "field": "assetId" }</c>,
/// <c>{ "bytes": "4096", "limit": "2048" }</c>. Null when the code carries no parameters.
/// </param>
public sealed record ValidationError(
    string JsonPointer,
    string Message,
    ValidationErrorKind Kind,
    string? Code = null,
    IReadOnlyDictionary<string, string>? Params = null);

/// <summary>Categorizes a <see cref="ValidationError"/>.</summary>
public enum ValidationErrorKind
{
    /// <summary>The candidate violated the form's JSON Schema.</summary>
    Schema,

    /// <summary>An INV-S2 resource bound was exceeded (registry-side schema size / nesting depth at register-time, or engine-side candidate size / validation wall-clock budget). The catastrophic-regex (ReDoS) control is the registry's process-global regex match-timeout, surfaced as a <see cref="Schema"/> failure, not a resource bound.</summary>
    ResourceBound,

    /// <summary>The active capability lacks authorization for the field or operation.</summary>
    Authorization,

    /// <summary>The referenced form definition or schema could not be resolved.</summary>
    NotFound,
}
