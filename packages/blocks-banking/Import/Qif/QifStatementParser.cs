using System.Globalization;

namespace Harborline.Api.Blocks.Banking.Import.Qif;

/// <summary>
/// Streaming QIF (Quicken Interchange Format) statement file parser.
/// Per ADR 0112 §3 (file import pipeline) — legacy fallback format.
/// </summary>
/// <remarks>
/// <para>
/// QIF is a line-oriented text format. Each record is delimited by <c>^</c>.
/// Field codes: D=date, T=amount, P=payee, M=memo, N=check number, L=category.
/// </para>
/// <para>
/// <strong>QIF is a lossy format:</strong> no provider transaction id (FITID),
/// minimal required fields. Treat as best-effort; lines with unparseable dates
/// or amounts are skipped with a warning, not rejected (unless the whole file
/// structure is corrupt).
/// </para>
/// <para>
/// <strong>Oversized-input guard:</strong>
/// total transactions capped at <see cref="MaxTransactions"/>;
/// line length capped at <see cref="MaxLineLengthChars"/>.
/// </para>
/// <para>
/// <strong>Description is inert data</strong> — Payee/Memo stored verbatim.
/// </para>
/// </remarks>
public sealed class QifStatementParser : IStatementFileParser
{
    /// <summary>Maximum line length in characters.</summary>
    public const int MaxLineLengthChars = 8_192;

    /// <summary>Maximum transactions per file.</summary>
    public const int MaxTransactions = 500_000;

    private static readonly string[] QifExtensions = [".qif"];

    /// <inheritdoc />
    public string Format => "QIF";

    /// <inheritdoc />
    public bool CanHandle(string fileName) =>
        QifExtensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public async Task<IReadOnlyList<ParsedStatementLine>> ParseAsync(
        Stream stream, string fileName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var results = new List<ParsedStatementLine>();
        using var reader = new StreamReader(stream, leaveOpen: true);

        // Current record fields
        string? date = null, amount = null, payee = null, memo = null;
        int lineNumber = 0;

        string? rawLine;
        while ((rawLine = await reader.ReadLineAsync(ct)) is not null)
        {
            lineNumber++;
            if (rawLine.Length > MaxLineLengthChars)
                throw new StatementParseException(Format, ParseRejectReason.OversizedInput,
                    $"QIF line {lineNumber} exceeds maximum length ({MaxLineLengthChars} chars).");

            string line = rawLine.TrimEnd();

            if (line.Length == 0) continue;

            // Record separator
            if (line[0] == '^')
            {
                var record = BuildRecord(date, amount, payee, memo, results.Count);
                if (record is not null)
                {
                    results.Add(record);
                    if (results.Count > MaxTransactions)
                        throw new StatementParseException(Format, ParseRejectReason.OversizedInput,
                            $"QIF file exceeds maximum transaction count ({MaxTransactions}).");
                }
                // Reset for next record
                date = amount = payee = memo = null;
                continue;
            }

            // QIF header lines (e.g. !Type:Bank) — skip
            if (line[0] == '!')
                continue;

            if (line.Length < 2) continue;
            char code = line[0];
            string value = line[1..].Trim();

            switch (code)
            {
                case 'D': date   = value; break;
                case 'T': amount = value; break;
                case 'U': if (amount is null) amount = value; break;  // alternate amount field
                case 'P': payee  = value; break;
                case 'M': memo   = value; break;
                // N (check number), L (category), C (cleared) — ignored
            }
        }

        // Handle trailing record with no ^ terminator
        {
            var record = BuildRecord(date, amount, payee, memo, results.Count);
            if (record is not null)
                results.Add(record);
        }

        return results;
    }

    private ParsedStatementLine? BuildRecord(
        string? date, string? amount, string? payee, string? memo, int index)
    {
        if (string.IsNullOrWhiteSpace(date) || string.IsNullOrWhiteSpace(amount))
            return null;

        if (!TryParseQifDate(date, out DateTimeOffset parsedDate))
            return null;  // lossy: skip unparseable dates

        string cleanAmount = amount.Replace(",", "", StringComparison.Ordinal).Trim();
        if (!decimal.TryParse(cleanAmount, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal parsedAmount))
            return null;  // lossy: skip unparseable amounts

        string description = (payee ?? memo ?? string.Empty).Trim();

        return new ParsedStatementLine(
            PostedAt:      parsedDate,
            Amount:        parsedAmount,
            Currency:      null,
            Description:   description,
            Pending:       false,
            ProviderTxnId: null);
    }

    private static bool TryParseQifDate(string value, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        // QIF date formats: M/D/Y, M/D'Y, D/M/Y, various separators
        string v = value.Replace("'", "/", StringComparison.Ordinal).Replace("-", "/", StringComparison.Ordinal).Replace(".", "/", StringComparison.Ordinal).Trim();

        string[] formats =
        [
            "M/d/yy", "M/d/yyyy", "d/M/yyyy", "d/M/yy",
            "MM/dd/yyyy", "dd/MM/yyyy", "MM/dd/yy", "dd/MM/yy",
            "yyyy/MM/dd",
        ];

        if (DateTimeOffset.TryParseExact(v, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out result))
            return true;

        return DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }
}
