namespace Harborline.Api.Blocks.Banking.Import;

/// <summary>
/// Format-agnostic contract for parsing a bank statement file into a sequence of
/// <see cref="ParsedStatementLine"/> rows.
/// </summary>
/// <remarks>
/// <para>
/// Implementations MUST be streaming (not slurp-to-memory) and MUST enforce:
/// <list type="bullet">
///   <item><description>Maximum bytes-per-line (where line-oriented) or per-element size cap.</description></item>
///   <item><description>Maximum total line/element count per file.</description></item>
///   <item><description>Rejection of oversized input with a clean <see cref="StatementParseException"/>.</description></item>
/// </list>
/// (ADR 0112 §Hostile-file-input invariants, sec-eng C3.)
/// </para>
/// <para>
/// Description fields in the returned rows are <strong>inert data</strong> — they are
/// never evaluated, templated, or executed. Formula neutralization for any CSV export
/// path is the caller's/exporter's responsibility (ADR 0112 sec-eng C3 #1, CWE-1236).
/// </para>
/// </remarks>
public interface IStatementFileParser
{
    /// <summary>
    /// The file format this parser handles (e.g. "CSV", "OFX", "QIF", "CAMT053").
    /// </summary>
    string Format { get; }

    /// <summary>
    /// Returns <c>true</c> if this parser can attempt to parse a file with the
    /// given <paramref name="fileName"/> (based on extension / mime hint).
    /// </summary>
    bool CanHandle(string fileName);

    /// <summary>
    /// Parses the statement file from <paramref name="stream"/> and returns the
    /// normalized rows. The stream is read once (streaming); callers are
    /// responsible for position reset if re-reads are needed.
    /// </summary>
    /// <param name="stream">The file content. Must be seekable for CAMT/OFX; sequential for CSV/QIF.</param>
    /// <param name="fileName">Used for format detection hints.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An ordered list of parsed rows (may be empty for files with headers only).</returns>
    /// <exception cref="StatementParseException">
    /// Thrown on malformed input, oversized input, DTD/XXE in CAMT, or any
    /// hard-rejection condition. Parsers MUST fail-closed rather than returning
    /// partial results when the file is structurally invalid.
    /// </exception>
    Task<IReadOnlyList<ParsedStatementLine>> ParseAsync(
        Stream stream, string fileName, CancellationToken ct = default);
}
