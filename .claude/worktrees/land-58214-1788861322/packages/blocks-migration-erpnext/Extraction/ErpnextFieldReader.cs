using System.Globalization;
using Harborline.Api.Foundation.Import.Extraction;

namespace Harborline.Api.Blocks.Migration.Erpnext.Extraction;

/// <summary>
/// Internal column-coercion helper: reads a typed scalar off a
/// <see cref="SourceRow"/> for the row→DTO mapping in
/// <see cref="MariaDbDumpExtractor"/>, failing loud (ADR 0100 C5) via
/// <see cref="ErpnextExtractionException"/> on a missing-required or
/// unparseable value — never coercing, defaulting, or guessing.
/// </summary>
/// <remarks>
/// All numeric/date parses use <see cref="CultureInfo.InvariantCulture"/> — a
/// mysqldump emits invariant-formatted <c>decimal</c>/<c>date</c>/<c>datetime</c>
/// literals regardless of the ERPNext UI locale. Booleans are MySQL <c>0</c>/<c>1</c>
/// (<c>tinyint(1)</c>). A null/absent column models SQL NULL; whether that is a
/// failure depends on whether the field is required.
/// </remarks>
internal static class ErpnextFieldReader
{
    /// <summary>The ERPNext <c>name</c> column — the opaque natural key, present on every DocType.</summary>
    private const string NameColumn = "name";

    /// <summary>Reads the ERPNext <c>name</c> (externalRef) — required on every row.</summary>
    internal static string ExternalRef(SourceRow row)
    {
        var name = row.GetString(NameColumn);
        if (string.IsNullOrEmpty(name))
        {
            // No externalRef to even identify the row by — use a sentinel for the message.
            throw new ErpnextExtractionException(
                row.DocType, "(unknown)", ErpnextExtractionReason.MissingRequiredField, NameColumn);
        }

        return name;
    }

    /// <summary>Reads an optional string column (SQL NULL / absent -> null). No coercion.</summary>
    internal static string? OptionalString(SourceRow row, string column) => row.GetString(column);

    /// <summary>Reads a required string column; fails loud if missing/null/empty.</summary>
    internal static string RequiredString(SourceRow row, string externalRef, string column)
    {
        var value = row.GetString(column);
        if (string.IsNullOrEmpty(value))
        {
            throw new ErpnextExtractionException(
                row.DocType, externalRef, ErpnextExtractionReason.MissingRequiredField, column);
        }

        return value;
    }

    /// <summary>
    /// Reads a required string column but tolerates an absent/null source value by
    /// returning <paramref name="fallback"/> — used where ERPNext leaves a label
    /// blank and the DTO contract says "fall back to X" (NOT a fail-loud field).
    /// </summary>
    internal static string StringOrFallback(SourceRow row, string column, string fallback)
    {
        var value = row.GetString(column);
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    /// <summary>
    /// Reads a MySQL <c>tinyint(1)</c> boolean (<c>0</c>/<c>1</c>). Absent/NULL -> false
    /// (ERPNext omits a false flag as often as it writes 0). A non-0/1 value fails loud.
    /// </summary>
    internal static bool Bool(SourceRow row, string externalRef, string column)
    {
        var raw = row.GetString(column);
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        return raw.Trim() switch
        {
            "0" => false,
            "1" => true,
            _ => throw new ErpnextExtractionException(
                row.DocType, externalRef, ErpnextExtractionReason.UnparseableFieldValue, column),
        };
    }

    /// <summary>Reads a required <c>int</c> column (e.g. <c>docstatus</c>); fails loud if missing/unparseable.</summary>
    internal static int RequiredInt(SourceRow row, string externalRef, string column)
    {
        var raw = row.GetString(column);
        if (string.IsNullOrEmpty(raw))
        {
            throw new ErpnextExtractionException(
                row.DocType, externalRef, ErpnextExtractionReason.MissingRequiredField, column);
        }

        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new ErpnextExtractionException(
                row.DocType, externalRef, ErpnextExtractionReason.UnparseableFieldValue, column);
        }

        return value;
    }

    /// <summary>Reads an <c>int</c> column with a default for absent/NULL; a present-but-unparseable value fails loud.</summary>
    internal static int IntOrDefault(SourceRow row, string externalRef, string column, int @default)
    {
        var raw = row.GetString(column);
        if (string.IsNullOrEmpty(raw))
        {
            return @default;
        }

        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new ErpnextExtractionException(
                row.DocType, externalRef, ErpnextExtractionReason.UnparseableFieldValue, column);
        }

        return value;
    }

    /// <summary>Reads a required <c>decimal</c> column (monetary/quantity); fails loud if missing/unparseable.</summary>
    internal static decimal RequiredDecimal(SourceRow row, string externalRef, string column)
    {
        var raw = row.GetString(column);
        if (string.IsNullOrEmpty(raw))
        {
            throw new ErpnextExtractionException(
                row.DocType, externalRef, ErpnextExtractionReason.MissingRequiredField, column);
        }

        if (!decimal.TryParse(raw.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            throw new ErpnextExtractionException(
                row.DocType, externalRef, ErpnextExtractionReason.UnparseableFieldValue, column);
        }

        return value;
    }

    /// <summary>Reads a <c>decimal</c> column with a default for absent/NULL; present-but-unparseable fails loud.</summary>
    internal static decimal DecimalOrDefault(SourceRow row, string externalRef, string column, decimal @default)
    {
        var raw = row.GetString(column);
        if (string.IsNullOrEmpty(raw))
        {
            return @default;
        }

        if (!decimal.TryParse(raw.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            throw new ErpnextExtractionException(
                row.DocType, externalRef, ErpnextExtractionReason.UnparseableFieldValue, column);
        }

        return value;
    }

    /// <summary>
    /// Reads a required <c>date</c> column as <see cref="DateOnly"/>. ERPNext emits
    /// <c>YYYY-MM-DD</c>; a <c>datetime</c> column passed here is truncated to its date
    /// part. Fails loud if missing/unparseable.
    /// </summary>
    internal static DateOnly RequiredDate(SourceRow row, string externalRef, string column)
    {
        var raw = row.GetString(column);
        if (string.IsNullOrEmpty(raw))
        {
            throw new ErpnextExtractionException(
                row.DocType, externalRef, ErpnextExtractionReason.MissingRequiredField, column);
        }

        return ParseDate(row.DocType, externalRef, column, raw);
    }

    /// <summary>Reads an optional <c>date</c> column (absent/NULL -> null); present-but-unparseable fails loud.</summary>
    internal static DateOnly? OptionalDate(SourceRow row, string externalRef, string column)
    {
        var raw = row.GetString(column);
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        return ParseDate(row.DocType, externalRef, column, raw);
    }

    private static DateOnly ParseDate(string docType, string externalRef, string column, string raw)
    {
        var trimmed = raw.Trim();
        // ERPNext date columns are YYYY-MM-DD; datetime columns are YYYY-MM-DD HH:MM:SS[.ffffff].
        // Take the leading date token either way.
        var dateToken = trimmed.Length >= 10 ? trimmed[..10] : trimmed;
        if (DateOnly.TryParseExact(dateToken, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        throw new ErpnextExtractionException(
            docType, externalRef, ErpnextExtractionReason.UnparseableFieldValue, column);
    }

    /// <summary>The single v1 base currency (ADR 0100 OQ-2 — USD-only across all four LLCs).</summary>
    internal const string BaseCurrency = "USD";

    /// <summary>
    /// Reads the optional <c>currency</c> column and ENFORCES the USD-only guard
    /// (ADR 0100 OQ-2). A null/blank currency is treated as the USD base (the DTOs'
    /// documented default). A present non-USD currency FAILS LOUD — out-of-v1-scope;
    /// never coerced. Returns the source currency string (always USD on success) for
    /// the DTO's <c>Currency</c> field.
    /// </summary>
    internal static string? RequireUsdCurrency(SourceRow row, string externalRef, string column = "currency")
    {
        var raw = row.GetString(column);
        if (string.IsNullOrWhiteSpace(raw))
        {
            // Null/blank -> the DTO's documented USD default; pass null through so the
            // DTO's own "defaults to USD when null/blank" contract applies.
            return raw;
        }

        if (!string.Equals(raw.Trim(), BaseCurrency, StringComparison.OrdinalIgnoreCase))
        {
            throw new ErpnextExtractionException(
                row.DocType, externalRef, ErpnextExtractionReason.NonUsdCurrency, column);
        }

        return raw;
    }
}
