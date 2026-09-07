namespace Harborline.Api.Blocks.Banking.Import.Csv;

/// <summary>
/// Describes how to map CSV columns to statement-line fields for a specific bank's
/// export format. Saved per-bank and reused to amortize the per-bank mapping toil.
/// </summary>
/// <remarks>
/// <para>
/// Column indices are 0-based. A mapping of <c>-1</c> means the field is not
/// present in the file; parsers use the default or null value for missing fields.
/// </para>
/// <para>
/// <strong>Date formats:</strong> <see cref="DateFormat"/> is a
/// <c>DateTime.ParseExact</c> / <c>DateTimeOffset.ParseExact</c>
/// format string (e.g. <c>"MM/dd/yyyy"</c>, <c>"yyyy-MM-dd"</c>). A null
/// <see cref="DateFormat"/> attempts common formats in order.
/// </para>
/// </remarks>
/// <param name="DateColumn">0-based index of the transaction date column.</param>
/// <param name="DescriptionColumn">0-based index of the payee/memo column.</param>
/// <param name="AmountColumn">
/// 0-based index of a single signed-amount column. Specify either this OR
/// <see cref="CreditColumn"/> + <see cref="DebitColumn"/>, not both.
/// </param>
/// <param name="CreditColumn">
/// 0-based index of the credit (inflow) column when the bank uses separate
/// credit/debit columns. Use with <see cref="DebitColumn"/>.
/// </param>
/// <param name="DebitColumn">
/// 0-based index of the debit (outflow) column. Values are stored as positive
/// decimals by convention; the parser negates them when combining into Amount.
/// </param>
/// <param name="CurrencyColumn">0-based index of a currency code column; -1 if absent.</param>
/// <param name="SkipRows">Number of header/preamble rows to skip before data rows begin.</param>
/// <param name="DateFormat">Optional explicit date format string; null = try common formats.</param>
/// <param name="Delimiter">Column delimiter character; defaults to comma.</param>
public sealed record CsvColumnMapping(
    int DateColumn,
    int DescriptionColumn,
    int AmountColumn = -1,
    int CreditColumn = -1,
    int DebitColumn = -1,
    int CurrencyColumn = -1,
    int SkipRows = 1,
    string? DateFormat = null,
    char Delimiter = ',')
{
    /// <summary>
    /// Returns true if this mapping uses a single signed-amount column
    /// (as opposed to separate credit/debit columns).
    /// </summary>
    public bool UsesSingleAmountColumn => AmountColumn >= 0;

    /// <summary>
    /// Validates that the mapping is internally consistent.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (DateColumn < 0)
            errors.Add("DateColumn must be >= 0.");
        if (DescriptionColumn < 0)
            errors.Add("DescriptionColumn must be >= 0.");
        if (AmountColumn < 0 && (CreditColumn < 0 || DebitColumn < 0))
            errors.Add("Either AmountColumn or both CreditColumn+DebitColumn must be specified.");
        if (AmountColumn >= 0 && (CreditColumn >= 0 || DebitColumn >= 0))
            errors.Add("Specify either AmountColumn or CreditColumn+DebitColumn, not both.");
        return errors;
    }
}
