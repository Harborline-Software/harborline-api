using System.Globalization;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Integrations.Payments;

namespace Harborline.Api.Blocks.Banking.Import.Csv;

/// <summary>
/// Streaming CSV statement-file parser.
/// Per ADR 0112 §Hostile-file-input invariants sec-eng C3.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Security invariants enforced:</strong>
/// <list type="bullet">
///   <item>
///     <description>
///     <strong>Formula injection (CWE-1236) — neutralized on EXPORT, not on import.</strong>
///     The parser stores Description values as inert data. Any CSV <em>export</em> path
///     MUST prefix-quote cells whose value starts with <c>= + - @ TAB CR</c>.
///     This parser validates that descriptions are read verbatim and not evaluated.
///     </description>
///   </item>
///   <item>
///     <description>
///     <strong>Oversized input (CWE-400):</strong>
///     line length is capped at <see cref="MaxLineLengthBytes"/>; total line count
///     is capped at <see cref="MaxLines"/>. Oversized files are rejected with
///     <see cref="ParseRejectReason.OversizedInput"/>.
///     </description>
///   </item>
///   <item>
///     <description>
///     <strong>Streaming:</strong> reads line-by-line via <see cref="StreamReader"/>
///     without loading the full file into memory.
///     </description>
///   </item>
/// </list>
/// </para>
/// </remarks>
public sealed class CsvStatementParser : IStatementFileParser
{
    /// <summary>Maximum bytes per line before rejection (32 KB is generous for statement data).</summary>
    public const int MaxLineLengthBytes = 32_768;

    /// <summary>Maximum lines per file before rejection (500 k covers a decade of daily transactions).</summary>
    public const int MaxLines = 500_000;

    private static readonly string[] CsvExtensions = [".csv", ".txt"];

    private readonly CsvColumnMapping _mapping;

    /// <summary>
    /// Initializes a new <see cref="CsvStatementParser"/> with the given column mapping.
    /// </summary>
    /// <param name="mapping">Per-bank CSV column mapping.</param>
    /// <exception cref="ArgumentException">Thrown if the mapping is invalid.</exception>
    public CsvStatementParser(CsvColumnMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var errors = mapping.Validate();
        if (errors.Count > 0)
            throw new ArgumentException($"Invalid CsvColumnMapping: {string.Join("; ", errors)}", nameof(mapping));
        _mapping = mapping;
    }

    /// <inheritdoc />
    public string Format => "CSV";

    /// <inheritdoc />
    public bool CanHandle(string fileName) =>
        CsvExtensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public async Task<IReadOnlyList<ParsedStatementLine>> ParseAsync(
        Stream stream, string fileName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var results = new List<ParsedStatementLine>();
        using var reader = new StreamReader(stream, leaveOpen: true);

        int lineNumber = 0;
        string? line;

        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            lineNumber++;

            // Oversized-line guard
            if (line.Length > MaxLineLengthBytes)
                throw new StatementParseException(Format, ParseRejectReason.OversizedInput,
                    $"Line {lineNumber} exceeds maximum length ({MaxLineLengthBytes} chars).");

            // Total-line guard
            if (lineNumber > MaxLines + _mapping.SkipRows)
                throw new StatementParseException(Format, ParseRejectReason.OversizedInput,
                    $"File exceeds maximum line count ({MaxLines} data rows).");

            // Skip header rows
            if (lineNumber <= _mapping.SkipRows)
                continue;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            var columns = SplitCsvLine(line, _mapping.Delimiter);

            ParsedStatementLine? parsed = TryParseLine(columns, lineNumber);
            if (parsed is not null)
                results.Add(parsed);
        }

        return results;
    }

    private ParsedStatementLine? TryParseLine(string[] columns, int lineNumber)
    {
        if (columns.Length == 0)
            return null;

        // Date
        if (!TryParseDate(GetColumn(columns, _mapping.DateColumn), out DateTimeOffset date))
            throw new StatementParseException(Format, ParseRejectReason.MissingRequiredField,
                $"Line {lineNumber}: cannot parse date from column {_mapping.DateColumn}.");

        // Amount
        decimal amount;
        if (_mapping.UsesSingleAmountColumn)
        {
            if (!TryParseDecimal(GetColumn(columns, _mapping.AmountColumn), out amount))
                throw new StatementParseException(Format, ParseRejectReason.MissingRequiredField,
                    $"Line {lineNumber}: cannot parse amount from column {_mapping.AmountColumn}.");
        }
        else
        {
            var creditStr = GetColumn(columns, _mapping.CreditColumn);
            var debitStr  = GetColumn(columns, _mapping.DebitColumn);
            TryParseDecimal(creditStr, out decimal credit);
            TryParseDecimal(debitStr,  out decimal debit);
            // credit = positive inflow, debit = positive value representing outflow → negate
            amount = credit - debit;
        }

        // Description — inert data; never evaluated or templated
        string description = GetColumn(columns, _mapping.DescriptionColumn).Trim();

        // Optional currency
        CurrencyCode? currency = null;
        if (_mapping.CurrencyColumn >= 0)
        {
            string curr = GetColumn(columns, _mapping.CurrencyColumn).Trim();
            if (!string.IsNullOrEmpty(curr))
                currency = new CurrencyCode(curr);
        }

        return new ParsedStatementLine(
            PostedAt:       date,
            Amount:         amount,
            Currency:       currency,
            Description:    description,
            Pending:        false,    // CSV files don't distinguish pending; treat all as posted
            ProviderTxnId:  null);    // CSV has no provider transaction id
    }

    private bool TryParseDate(string value, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        // Try explicit format first
        if (_mapping.DateFormat is not null)
        {
            if (DateTimeOffset.TryParseExact(value.Trim(), _mapping.DateFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result))
                return true;
            if (DateTime.TryParseExact(value.Trim(), _mapping.DateFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            {
                result = new DateTimeOffset(dt, TimeSpan.Zero);
                return true;
            }
            return false;
        }

        // Try common bank statement date formats.
        // AssumeUniversal treats date-only strings (no TZ offset) as UTC; bank statement
        // dates are calendar dates — storing them at UTC midnight avoids local-TZ drift.
        string[] commonFormats =
        [
            "MM/dd/yyyy", "MM/dd/yy", "dd/MM/yyyy", "dd-MM-yyyy",
            "yyyy-MM-dd", "yyyyMMdd", "dd MMM yyyy", "d MMM yyyy",
            "MMM dd, yyyy", "M/d/yyyy", "d/M/yyyy",
        ];
        if (DateTimeOffset.TryParseExact(value.Trim(), commonFormats,
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result))
            return true;

        return DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out result);
    }

    private static bool TryParseDecimal(string value, out decimal result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        // Remove currency symbols, whitespace, and thousand-separators
        string cleaned = value.Trim().TrimStart('$', '£', '€', '¥', '+')
                                     .Replace(",", "")
                                     .Replace(" ", "");
        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out result);
    }

    private static string GetColumn(string[] columns, int index)
    {
        if (index < 0 || index >= columns.Length)
            return string.Empty;
        // Remove surrounding quotes if present
        string val = columns[index];
        if (val.Length >= 2 && val[0] == '"' && val[^1] == '"')
            val = val[1..^1].Replace("\"\"", "\"");
        return val;
    }

    /// <summary>
    /// Splits a CSV line respecting quoted fields.
    /// Simple implementation that handles RFC 4180 quoting.
    /// </summary>
    internal static string[] SplitCsvLine(string line, char delimiter)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // Check for escaped quote ""
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++; // skip next quote
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == delimiter)
                {
                    fields.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
        }
        fields.Add(current.ToString());
        return [.. fields];
    }
}
