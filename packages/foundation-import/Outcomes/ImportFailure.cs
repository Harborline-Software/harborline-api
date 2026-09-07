namespace Harborline.Api.Foundation.Import.Outcomes;

/// <summary>
/// The structured, ALLOWLISTED projection of a rejected import record — the
/// payload of the <see cref="ImportOutcome{T}.Rejected"/> arm and the only
/// thing written to the reject bin / <c>migration_audit_log</c>
/// (ADR 0100 C2 + C9; security-engineering amendment B).
/// </summary>
/// <remarks>
/// <para>
/// <b>This type captures only what is needed to FIND and RE-EXPORT the failed
/// record — never its contents.</b> It is deliberately built from safe scalar
/// identifiers only:
/// <list type="bullet">
///   <item><see cref="ExternalRef"/> — the ERPNext <c>name</c> (an opaque id, not PII).</item>
///   <item><see cref="DocType"/> — the ERPNext DocType (e.g. "Sales Invoice").</item>
///   <item><see cref="ReasonCode"/> — a bounded, machine-readable reject reason.</item>
///   <item><see cref="FieldName"/> — optional offending field NAME (never its value).</item>
///   <item><see cref="RuleViolated"/> — optional human-readable rule description (no record contents).</item>
/// </list>
/// </para>
/// <para>
/// There is intentionally NO field that can hold the raw <c>Erpnext*Source</c>
/// payload, a <c>Party</c> email/phone/name, or a per-line monetary amount —
/// the C9-forbidden content. A serializer that emits the raw source into a
/// reject row is forbidden by the contract; this shape makes the safe path the
/// only structurally-available one (the C-LOG-REJECT enforcement invariant).
/// </para>
/// <para>
/// Authors MUST supply only safe values to <see cref="FieldName"/> /
/// <see cref="RuleViolated"/> — they are descriptors, not value holders. Do
/// not interpolate a record's monetary amount or a party's PII into either.
/// </para>
/// </remarks>
/// <param name="ExternalRef">The ERPNext <c>name</c> natural key (opaque id; safe to log).</param>
/// <param name="DocType">The ERPNext DocType the rejected record belongs to.</param>
/// <param name="ReasonCode">A bounded, machine-readable reject reason code (see <see cref="ImportRejectReason"/> for canonical values).</param>
/// <param name="FieldName">Optional NAME of the offending field — never the field's value.</param>
/// <param name="RuleViolated">Optional human-readable description of the violated rule — must not contain record contents.</param>
public sealed record ImportFailure(
    string ExternalRef,
    string DocType,
    string ReasonCode,
    string? FieldName = null,
    string? RuleViolated = null)
{
    /// <summary>
    /// Builds an <see cref="ImportFailure"/> from a canonical
    /// <see cref="ImportRejectReason"/> reason code (the recommended path so
    /// reason codes stay bounded and stable for the reject-bin report).
    /// </summary>
    public static ImportFailure Of(
        string externalRef,
        string docType,
        ImportRejectReason reason,
        string? fieldName = null,
        string? ruleViolated = null)
        => new(externalRef, docType, reason.ToString(), fieldName, ruleViolated);
}

/// <summary>
/// Canonical, bounded reject reason codes for the ERPNext importer
/// (ADR 0100 C2/C5). A bounded enum keeps the reject-bin report's
/// reason taxonomy stable; the <c>string ReasonCode</c> on
/// <see cref="ImportFailure"/> is the persisted surface so future codes can be
/// added without a schema change, but new well-known codes SHOULD be added here.
/// </summary>
public enum ImportRejectReason
{
    /// <summary>A required field was missing or empty on the source record.</summary>
    MissingRequiredField,

    /// <summary>A field value failed validation (format, range, enum membership).</summary>
    InvalidFieldValue,

    /// <summary>A referenced parent/related record could not be resolved (e.g. a JE referencing an unknown account).</summary>
    UnresolvedReference,

    /// <summary>A balance / invariant constraint was violated (e.g. an opening JE where Σdebit != Σcredit).</summary>
    ConstraintViolation,

    /// <summary>The record is in a non-base currency and multi-currency is deferred to v2 (ADR 0100 §10.2).</summary>
    UnsupportedCurrency,

    /// <summary>A duplicate natural key was encountered within a single source set (data defect).</summary>
    DuplicateExternalRef,

    /// <summary>The record could not be parsed from the source into a typed <c>Erpnext*Source</c>.</summary>
    UnparseableSource,
}
