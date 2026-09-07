using System.Globalization;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Integrations.Payments;

namespace Harborline.Api.Blocks.Banking.Import.Ofx;

/// <summary>
/// Streaming OFX 1.x/2.x (and QFX) statement file parser.
/// Per ADR 0112 §3 (file import pipeline) and §Hostile-file-input invariants sec-eng C3 #4.
/// </summary>
/// <remarks>
/// <para>
/// <strong>OFX 1.x is SGML, not well-formed XML.</strong>
/// OFX 1.x uses unclosed SGML tags (<c>&lt;DTAMT&gt;value</c> with no closing tag).
/// This parser uses line-oriented text scanning (not an XML parser) for OFX 1.x,
/// and for OFX 2.x (well-formed XML headers) delegates to a hardened XML reader with
/// DTD disabled. This approach avoids lenient-parser quirks that are a security surface.
/// (ADR 0112 sec-eng C3 #4.)
/// </para>
/// <para>
/// <strong>Oversized-input guard:</strong>
/// total bytes read is tracked; files exceeding <see cref="MaxFileSizeBytes"/> are rejected.
/// </para>
/// <para>
/// <strong>Description is inert data:</strong>
/// NAME/MEMO fields are stored verbatim, never evaluated.
/// </para>
/// </remarks>
public sealed class OfxStatementParser : IStatementFileParser
{
    /// <summary>Maximum file size: 50 MB is far more than any bank export would produce.</summary>
    public const int MaxFileSizeBytes = 50 * 1024 * 1024;

    /// <summary>Maximum transaction count per file.</summary>
    public const int MaxTransactions = 500_000;

    private static readonly string[] OfxExtensions = [".ofx", ".qfx"];

    /// <inheritdoc />
    public string Format => "OFX";

    /// <inheritdoc />
    public bool CanHandle(string fileName) =>
        OfxExtensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public async Task<IReadOnlyList<ParsedStatementLine>> ParseAsync(
        Stream stream, string fileName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // Read with size cap
        byte[] buffer = new byte[MaxFileSizeBytes + 1];
        int totalRead = 0;
        int read;
        using var ms = new MemoryStream();
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(4096, buffer.Length)), ct)) > 0)
        {
            ms.Write(buffer, 0, read);
            totalRead += read;
            if (totalRead > MaxFileSizeBytes)
                throw new StatementParseException(Format, ParseRejectReason.OversizedInput,
                    $"OFX file exceeds maximum size of {MaxFileSizeBytes / 1024 / 1024} MB.");
        }

        string content = System.Text.Encoding.UTF8.GetString(ms.ToArray());

        // Detect OFX 2.x (XML) vs OFX 1.x (SGML) by presence of an XML processing instruction.
        // OFX 2.x files begin with <?xml ... ?> or <?OFX OFXHEADER=... ?>.
        // OFX 1.x SGML files contain <OFX>...</OFX> but have NO processing instruction —
        // the third condition (contains <OFX> AND </OFX>) was deliberately removed because it
        // fired on both formats; the processing-instruction test is the correct discriminator.
        string trimmed = content.TrimStart();
        bool isXml = trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
                  || trimmed.StartsWith("<?OFX", StringComparison.OrdinalIgnoreCase);

        return isXml
            ? ParseOfxXml(content)
            : ParseOfxSgml(content);
    }

    /// <summary>
    /// Parses OFX 2.x (well-formed XML). Uses <see cref="System.Xml.XmlReader"/>
    /// with DTD prohibited and no external resolver.
    /// </summary>
    private IReadOnlyList<ParsedStatementLine> ParseOfxXml(string content)
    {
        var settings = new System.Xml.XmlReaderSettings
        {
            DtdProcessing  = System.Xml.DtdProcessing.Prohibit,
            XmlResolver    = null,
            MaxCharactersInDocument = MaxFileSizeBytes,
            MaxCharactersFromEntities = 1024,  // entity expansion cap
        };

        var results = new List<ParsedStatementLine>();
        string? dtPostedXml = null, trnAmtXml = null, nameXml = null, memoXml = null, fitIdXml = null, trnTypeXml = null;
        bool inStmtTrn = false;

        try
        {
            using var sr = new System.IO.StringReader(content);
            using var reader = System.Xml.XmlReader.Create(sr, settings);

            while (reader.Read())
            {
                if (reader.NodeType == System.Xml.XmlNodeType.Element)
                {
                    string elem = reader.LocalName.ToUpperInvariant();
                    switch (elem)
                    {
                        case "STMTTRN":
                            inStmtTrn = true;
                            dtPostedXml = trnAmtXml = nameXml = memoXml = fitIdXml = trnTypeXml = null;
                            break;
                        case "DTPOSTED": dtPostedXml = reader.ReadElementContentAsString(); break;
                        case "FITID":    fitIdXml    = reader.ReadElementContentAsString(); break;
                        case "TRNTYPE":  trnTypeXml  = reader.ReadElementContentAsString(); break;
                        case "TRNAMT":   trnAmtXml   = reader.ReadElementContentAsString(); break;
                        case "NAME":     nameXml     = reader.ReadElementContentAsString(); break;
                        case "MEMO":     memoXml     = reader.ReadElementContentAsString(); break;
                    }
                }
                else if (reader.NodeType == System.Xml.XmlNodeType.EndElement
                         && reader.LocalName.Equals("STMTTRN", StringComparison.OrdinalIgnoreCase))
                {
                    if (inStmtTrn)
                    {
                        string? description = nameXml ?? memoXml;
                        var line = BuildFromFields(dtPostedXml, trnAmtXml, description, fitIdXml, trnTypeXml, results.Count);
                        if (line is not null)
                        {
                            results.Add(line);
                            if (results.Count > MaxTransactions)
                                throw new StatementParseException(Format, ParseRejectReason.OversizedInput,
                                    $"OFX file exceeds maximum transaction count ({MaxTransactions}).");
                        }
                        inStmtTrn = false;
                    }
                }
            }
        }
        catch (System.Xml.XmlException ex) when (ex.Message.Contains("DTD"))
        {
            throw new StatementParseException(Format, ParseRejectReason.XmlDtdProhibited,
                "OFX/XML file contains a DTD declaration which is prohibited for security (XXE prevention).", ex);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new StatementParseException(Format, ParseRejectReason.MalformedFile,
                $"OFX/XML parse error: {ex.Message}", ex);
        }

        return results;
    }

    /// <summary>
    /// Parses OFX 1.x SGML using line-oriented text scanning.
    /// Extracts transactions from STMTTRN blocks.
    /// </summary>
    private IReadOnlyList<ParsedStatementLine> ParseOfxSgml(string content)
    {
        var results = new List<ParsedStatementLine>();
        var lines = content.Split('\n');

        string? dtPosted = null, trnAmt = null, name = null, memo = null, fitId = null, trnType = null;
        bool inStmtTrn = false;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();

            if (line.Equals("<STMTTRN>", StringComparison.OrdinalIgnoreCase))
            {
                inStmtTrn = true;
                dtPosted = trnAmt = name = memo = fitId = trnType = null;
                continue;
            }
            if (line.Equals("</STMTTRN>", StringComparison.OrdinalIgnoreCase))
            {
                if (inStmtTrn)
                {
                    var parsed = BuildFromFields(dtPosted, trnAmt, name ?? memo, fitId, trnType, results.Count);
                    if (parsed is not null)
                    {
                        results.Add(parsed);
                        if (results.Count > MaxTransactions)
                            throw new StatementParseException(Format, ParseRejectReason.OversizedInput,
                                $"OFX file exceeds maximum transaction count ({MaxTransactions}).");
                    }
                    inStmtTrn = false;
                }
                continue;
            }

            if (!inStmtTrn) continue;

            // Extract tag and value: <TAG>value
            int gt = line.IndexOf('>');
            if (gt < 0) continue;
            string tag   = line[1..gt].ToUpperInvariant();
            string value = line[(gt + 1)..].Trim();

            switch (tag)
            {
                case "DTPOSTED": dtPosted = value; break;
                case "TRNAMT":   trnAmt   = value; break;
                case "FITID":    fitId    = value; break;
                case "TRNTYPE":  trnType  = value; break;
                case "NAME":     name     = value; break;
                case "MEMO":     memo     = value; break;
            }
        }

        return results;
    }

    private ParsedStatementLine? BuildFromFields(
        string? dtPosted, string? trnAmt, string? description,
        string? fitId, string? trnType, int index)
    {
        if (string.IsNullOrWhiteSpace(dtPosted) || string.IsNullOrWhiteSpace(trnAmt))
            return null;

        if (!TryParseOfxDate(dtPosted, out DateTimeOffset date))
            throw new StatementParseException(Format, ParseRejectReason.MissingRequiredField,
                $"Cannot parse DTPOSTED value '{dtPosted}'.");

        if (!decimal.TryParse(trnAmt, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal amount))
            throw new StatementParseException(Format, ParseRejectReason.MissingRequiredField,
                $"Cannot parse TRNAMT value '{trnAmt}'.");

        // OFX DEBIT records sometimes have positive amounts; TRNTYPE disambiguates
        if (trnType is not null &&
            trnType.Equals("DEBIT", StringComparison.OrdinalIgnoreCase) && amount > 0)
            amount = -amount;

        return new ParsedStatementLine(
            PostedAt:      date,
            Amount:        amount,
            Currency:      null,  // OFX carries CURDEF at the account level, not per-transaction; omit for v1
            Description:   (description ?? string.Empty).Trim(),
            Pending:       false,
            ProviderTxnId: fitId?.Trim());
    }

    private static bool TryParseOfxDate(string value, out DateTimeOffset result)
    {
        result = default;
        // OFX date format: YYYYMMDDHHMMSS[.nnn][+/-HH:MM[timezone name]]
        // Minimum: YYYYMMDD (8 chars)
        if (string.IsNullOrWhiteSpace(value)) return false;
        string v = value.Trim();

        // Strip timezone suffix (everything after [ or +/-)
        int bracketIdx = v.IndexOf('[');
        if (bracketIdx >= 0) v = v[..bracketIdx].Trim();

        // Try longest then shortest form
        if (v.Length >= 14 && DateTimeOffset.TryParseExact(v[..14], "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result))
            return true;
        if (v.Length >= 8 && DateTimeOffset.TryParseExact(v[..8], "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result))
            return true;

        return false;
    }
}
