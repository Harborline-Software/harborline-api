namespace Harborline.Api.Blocks.Banking.Import;

/// <summary>
/// Thrown when a parser rejects a statement file due to malformed content,
/// oversized input, security-triggering content (DTD/XXE in CAMT), or
/// other hard-rejection conditions.
/// </summary>
/// <remarks>
/// Parsers MUST fail-closed — i.e. throw this exception rather than
/// silently returning partial results when the file is structurally invalid.
/// (ADR 0112 §Hostile-file-input invariants, sec-eng C3.)
/// </remarks>
public sealed class StatementParseException : Exception
{
    /// <summary>The parser format that rejected the file.</summary>
    public string Format { get; }

    /// <summary>Categorized rejection reason.</summary>
    public ParseRejectReason Reason { get; }

    /// <summary>
    /// Initializes a new <see cref="StatementParseException"/>.
    /// </summary>
    /// <param name="format">The parser format that rejected the file.</param>
    /// <param name="reason">Categorized rejection reason.</param>
    /// <param name="message">Human-readable message.</param>
    public StatementParseException(string format, ParseRejectReason reason, string message)
        : base(message)
    {
        Format = format;
        Reason = reason;
    }

    /// <summary>
    /// Initializes a new <see cref="StatementParseException"/> with an inner exception.
    /// </summary>
    /// <param name="format">The parser format that rejected the file.</param>
    /// <param name="reason">Categorized rejection reason.</param>
    /// <param name="message">Human-readable message.</param>
    /// <param name="inner">The underlying exception.</param>
    public StatementParseException(string format, ParseRejectReason reason, string message, Exception inner)
        : base(message, inner)
    {
        Format = format;
        Reason = reason;
    }
}

/// <summary>Categorized reasons a parser may reject a file.</summary>
public enum ParseRejectReason
{
    /// <summary>File exceeds the configured size or line-count ceiling.</summary>
    OversizedInput,

    /// <summary>File is structurally malformed (invalid syntax, truncated).</summary>
    MalformedFile,

    /// <summary>
    /// CAMT.053 file contains a DTD declaration or external entity reference —
    /// rejected to prevent XXE / billion-laughs attacks (CWE-611 / CWE-776).
    /// </summary>
    XmlDtdProhibited,

    /// <summary>
    /// XML entity expansion exceeded the permitted depth or size limit
    /// (billion-laughs guard).
    /// </summary>
    XmlEntityExpansionLimit,

    /// <summary>
    /// Required field or column mapping is missing and the file cannot
    /// be parsed without it.
    /// </summary>
    MissingRequiredField,

    /// <summary>File uses an encoding that cannot be decoded.</summary>
    EncodingError,
}
