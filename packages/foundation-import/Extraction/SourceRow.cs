using System.Collections.Generic;
using System.Linq;

namespace Harborline.Api.Foundation.Import.Extraction;

/// <summary>
/// One access-mode-agnostic source row — a DocType-tagged, read-only map of
/// column name → string value, as produced by an <see cref="ISourceReader"/>
/// (ADR 0100 C6). This is the SEAM's currency: the upsert passes (A1+) map a
/// <see cref="SourceRow"/> to a typed <c>Erpnext*Source</c> DTO, and depend ONLY
/// on this row shape (or their own DTOs) — never on a mode-specific type such as
/// the MariaDB-dump adapter's internals (the C-MODE enforcement invariant).
/// </summary>
/// <remarks>
/// <para>
/// All values are surfaced as raw strings (the lowest-common-denominator across
/// dump / CSV / REST); the typed-DTO mapping (parse to <c>decimal</c> /
/// <c>DateOnly</c> / <c>bool</c>, with reject-on-unparseable) is the upsert
/// pass's responsibility, so the seam carries no mode-specific coercion.
/// A null value models SQL <c>NULL</c> / an absent column.
/// </para>
/// <para>The row is immutable; lookups are case-insensitive on the column name.</para>
/// </remarks>
public sealed class SourceRow
{
    private readonly IReadOnlyDictionary<string, string?> _columns;

    /// <summary>The ERPNext DocType this row belongs to (e.g. "Account", "Sales Invoice").</summary>
    public string DocType { get; }

    /// <summary>Initializes a row for <paramref name="docType"/> from a column map.</summary>
    /// <param name="docType">The ERPNext DocType name.</param>
    /// <param name="columns">Column name → value (null models SQL NULL). Column names are matched case-insensitively.</param>
    public SourceRow(string docType, IReadOnlyDictionary<string, string?> columns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docType);
        ArgumentNullException.ThrowIfNull(columns);

        DocType = docType;
        _columns = new Dictionary<string, string?>(columns, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The set of column names present on this row.</summary>
    public IEnumerable<string> ColumnNames => _columns.Keys;

    /// <summary>True if the named column is present on this row (regardless of null/value).</summary>
    public bool HasColumn(string column) => _columns.ContainsKey(column);

    /// <summary>
    /// Reads the value of <paramref name="column"/>, or <see langword="null"/> if
    /// the column is absent or SQL-NULL.
    /// </summary>
    public string? GetString(string column) =>
        _columns.TryGetValue(column, out var value) ? value : null;

    /// <summary>
    /// Reads a required string column. Returns <see langword="false"/> (and a null
    /// out value) if the column is absent or null — the upsert pass turns that into
    /// a structured <c>Rejected(MissingRequiredField)</c> rather than throwing.
    /// </summary>
    public bool TryGetRequired(string column, out string value)
    {
        var raw = GetString(column);
        if (string.IsNullOrEmpty(raw))
        {
            value = string.Empty;
            return false;
        }

        value = raw;
        return true;
    }
}
