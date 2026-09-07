namespace Harborline.Api.Blocks.Migration.Erpnext.Extraction;

/// <summary>
/// Thrown by the extractor when a source row cannot be faithfully mapped to its
/// typed <c>Erpnext*Source</c> DTO, or when it violates a v1 hard invariant
/// (e.g. the USD-only single-currency guard). Fail-loud per ADR 0100 C5 — the
/// extractor never silently drops, coerces, or guesses.
/// </summary>
/// <remarks>
/// <para>
/// <b>C9-safe message surface.</b> The exception message carries ONLY the DocType,
/// the opaque <see cref="ExternalRef"/> (the ERPNext <c>name</c> — an id, not PII),
/// the offending field NAME, and a bounded <see cref="ReasonCode"/>. It NEVER
/// carries a field VALUE, a monetary amount, party PII, or the raw row — a
/// log/handler that serializes this exception cannot leak C9-forbidden content.
/// </para>
/// <para>
/// The orchestrator (A7) MAY catch this at its boundary and convert it to an
/// <c>ImportOutcome.Rejected(ImportFailure)</c> with the same allowlisted scalar
/// identifiers; the extractor's job is only to refuse to fabricate a DTO from a
/// row it cannot honestly read.
/// </para>
/// </remarks>
public sealed class ErpnextExtractionException : Exception
{
    /// <summary>The DocType of the offending source row (e.g. "Sales Invoice").</summary>
    public string DocType { get; }

    /// <summary>The ERPNext <c>name</c> natural key of the offending row (opaque id; safe to log).</summary>
    public string ExternalRef { get; }

    /// <summary>A bounded, machine-readable reason code (see <see cref="ErpnextExtractionReason"/>).</summary>
    public string ReasonCode { get; }

    /// <summary>The NAME of the offending field — never its value. Null when the whole row is at fault.</summary>
    public string? FieldName { get; }

    /// <summary>Initializes the exception from C9-safe scalar identifiers only.</summary>
    public ErpnextExtractionException(
        string docType,
        string externalRef,
        ErpnextExtractionReason reason,
        string? fieldName = null)
        : base(BuildSafeMessage(docType, externalRef, reason, fieldName))
    {
        DocType = docType;
        ExternalRef = externalRef;
        ReasonCode = reason.ToString();
        FieldName = fieldName;
    }

    private static string BuildSafeMessage(
        string docType, string externalRef, ErpnextExtractionReason reason, string? fieldName)
    {
        var field = fieldName is null ? string.Empty : $" field='{fieldName}'";
        // No field VALUE interpolated — C9. externalRef is an opaque id, safe to log.
        return $"ERPNext extraction failed: docType='{docType}' externalRef='{externalRef}' " +
               $"reason={reason}{field}.";
    }
}

/// <summary>
/// Bounded, machine-readable reason codes for an extraction failure
/// (ADR 0100 C5). Distinct from <c>ImportRejectReason</c> (the pass-level reject
/// taxonomy): these describe why the EXTRACTOR could not produce a faithful DTO.
/// </summary>
public enum ErpnextExtractionReason
{
    /// <summary>A required column was missing or null on the source row.</summary>
    MissingRequiredField,

    /// <summary>A column value could not be parsed into the DTO's typed shape (decimal/date/bool/int).</summary>
    UnparseableFieldValue,

    /// <summary>
    /// The row is in a non-USD currency. v1 is USD-only across all four LLCs
    /// (ADR 0100 OQ-2; CIC-resolved 2026-05-29) — a non-USD row is out-of-v1-scope;
    /// the extractor fails loud rather than coercing it to USD.
    /// </summary>
    NonUsdCurrency,
}
