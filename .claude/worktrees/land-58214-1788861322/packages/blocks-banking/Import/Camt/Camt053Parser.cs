using System.Globalization;
using System.Xml;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Integrations.Payments;

namespace Harborline.Api.Blocks.Banking.Import.Camt;

/// <summary>
/// CAMT.053 (ISO 20022 Bank-to-Customer Statement) XML parser.
/// Per ADR 0112 §3 (file import pipeline) and §Hostile-file-input invariants sec-eng C3 #2.
/// </summary>
/// <remarks>
/// <para>
/// <strong>XXE + billion-laughs hardening (CWE-611 / CWE-776 — mandatory per ADR 0112 sec-eng C3):</strong>
/// <list type="bullet">
///   <item><description><see cref="DtdProcessing.Prohibit"/> — DTD declarations are rejected.</description></item>
///   <item><description><see cref="XmlReaderSettings.XmlResolver"/> = null — no external entity resolution.</description></item>
///   <item><description><see cref="XmlReaderSettings.MaxCharactersFromEntities"/> = 1024 — entity expansion capped.</description></item>
///   <item><description><see cref="XmlReaderSettings.MaxCharactersInDocument"/> — total document size capped.</description></item>
/// </list>
/// Any violation throws <see cref="StatementParseException"/> with
/// <see cref="ParseRejectReason.XmlDtdProhibited"/> or
/// <see cref="ParseRejectReason.XmlEntityExpansionLimit"/>.
/// </para>
/// <para>
/// <strong>Streaming:</strong> uses <see cref="XmlReader"/> in forward-only mode;
/// does not load the full document into memory.
/// </para>
/// <para>
/// <strong>Description is inert data:</strong> Ntry/Cdtr/Dbtr Nm and AddtlNtryInf
/// are stored verbatim, never evaluated.
/// </para>
/// </remarks>
public sealed class Camt053Parser : IStatementFileParser
{
    /// <summary>Maximum characters from XML entities (billion-laughs guard).</summary>
    public const long MaxEntityChars = 1_024;

    /// <summary>Maximum characters in the entire document.</summary>
    public const long MaxDocumentChars = 50L * 1024 * 1024;  // 50 MB

    /// <summary>Maximum entry (transaction) count per file.</summary>
    public const int MaxEntries = 500_000;

    private static readonly string[] CamtExtensions = [".xml", ".camt", ".camt053"];

    /// <inheritdoc />
    public string Format => "CAMT053";

    /// <inheritdoc />
    public bool CanHandle(string fileName) =>
        CamtExtensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public async Task<IReadOnlyList<ParsedStatementLine>> ParseAsync(
        Stream stream, string fileName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // Read into memory with size cap before parsing
        using var ms = new MemoryStream();
        byte[] buf = new byte[4096];
        int read;
        long totalBytes = 0;
        while ((read = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
        {
            totalBytes += read;
            if (totalBytes > MaxDocumentChars)
                throw new StatementParseException(Format, ParseRejectReason.OversizedInput,
                    $"CAMT.053 file exceeds maximum size ({MaxDocumentChars / 1024 / 1024} MB).");
            ms.Write(buf, 0, read);
        }
        ms.Position = 0;

        return ParseCamt(ms);
    }

    private IReadOnlyList<ParsedStatementLine> ParseCamt(Stream stream)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing             = DtdProcessing.Prohibit,      // ADR 0112 sec-eng C3: DTD prohibited
            XmlResolver               = null,                          // No external entity resolution
            MaxCharactersFromEntities = MaxEntityChars,               // Billion-laughs guard
            MaxCharactersInDocument   = MaxDocumentChars,
            IgnoreComments            = true,
            IgnoreProcessingInstructions = true,
            CloseInput                = false,
        };

        var results = new List<ParsedStatementLine>();

        // Per-entry state
        string? valDt = null, amt = null, ccy = null, description = null, acctSvcrRef = null;
        string? cdtDbtInd = null;
        bool inNtry = false;

        try
        {
            using var reader = XmlReader.Create(stream, settings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    string local = reader.LocalName;

                    switch (local)
                    {
                        case "Ntry":
                            inNtry = true;
                            valDt = amt = ccy = description = acctSvcrRef = cdtDbtInd = null;
                            break;

                        case "ValDt" when inNtry:
                            // ValDt can contain <Dt> or <DtTm> child
                            using (var sub = reader.ReadSubtree())
                            {
                                while (sub.Read())
                                {
                                    if (sub.NodeType == XmlNodeType.Element &&
                                        (sub.LocalName == "Dt" || sub.LocalName == "DtTm"))
                                        valDt = sub.ReadElementContentAsString();
                                }
                            }
                            break;

                        case "Amt" when inNtry:
                            ccy = reader.GetAttribute("Ccy");
                            amt = reader.ReadElementContentAsString();
                            continue;  // already moved past element

                        case "CdtDbtInd" when inNtry:
                            cdtDbtInd = reader.ReadElementContentAsString();
                            continue;

                        case "AcctSvcrRef" when inNtry:
                            acctSvcrRef = reader.ReadElementContentAsString();
                            break;

                        case "AddtlNtryInf" when inNtry:
                            // Additional info — inert data; never evaluated
                            description ??= reader.ReadElementContentAsString();
                            continue;

                        case "Nm" when inNtry:
                            description ??= reader.ReadElementContentAsString();
                            continue;
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "Ntry")
                {
                    if (inNtry)
                    {
                        var line = BuildEntry(valDt, amt, ccy, cdtDbtInd, description, acctSvcrRef);
                        if (line is not null)
                        {
                            results.Add(line);
                            if (results.Count > MaxEntries)
                                throw new StatementParseException(Format, ParseRejectReason.OversizedInput,
                                    $"CAMT.053 file exceeds maximum entry count ({MaxEntries}).");
                        }
                        inNtry = false;
                    }
                }
            }
        }
        catch (XmlException ex) when (ex.Message.Contains("DTD") || ex.Message.Contains("DOCTYPE"))
        {
            throw new StatementParseException(Format, ParseRejectReason.XmlDtdProhibited,
                "CAMT.053 file contains a DTD declaration which is prohibited for security (XXE/billion-laughs prevention).", ex);
        }
        catch (XmlException ex) when (ex.Message.Contains("entity") || ex.Message.Contains("Entity"))
        {
            throw new StatementParseException(Format, ParseRejectReason.XmlEntityExpansionLimit,
                "CAMT.053 XML entity expansion limit exceeded (billion-laughs guard).", ex);
        }
        catch (XmlException ex)
        {
            throw new StatementParseException(Format, ParseRejectReason.MalformedFile,
                $"CAMT.053 XML parse error: {ex.Message}", ex);
        }

        return results;
    }

    private ParsedStatementLine? BuildEntry(
        string? valDt, string? amt, string? ccy,
        string? cdtDbtInd, string? description, string? acctSvcrRef)
    {
        if (string.IsNullOrWhiteSpace(valDt) || string.IsNullOrWhiteSpace(amt))
            return null;

        if (!TryParseCamtDate(valDt, out DateTimeOffset date))
            throw new StatementParseException(Format, ParseRejectReason.MissingRequiredField,
                $"Cannot parse CAMT.053 entry date '{valDt}'.");

        if (!decimal.TryParse(amt.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal amount))
            throw new StatementParseException(Format, ParseRejectReason.MissingRequiredField,
                $"Cannot parse CAMT.053 entry amount '{amt}'.");

        // CAMT uses DBIT (debit = outflow) → negate to match signed convention (positive = credit/inflow)
        if (cdtDbtInd is not null &&
            cdtDbtInd.Trim().Equals("DBIT", StringComparison.OrdinalIgnoreCase))
            amount = -amount;

        CurrencyCode? currency = string.IsNullOrWhiteSpace(ccy) ? null : new CurrencyCode(ccy.Trim());

        return new ParsedStatementLine(
            PostedAt:      date,
            Amount:        amount,
            Currency:      currency,
            Description:   (description ?? string.Empty).Trim(),
            Pending:       false,
            ProviderTxnId: acctSvcrRef?.Trim());
    }

    private static bool TryParseCamtDate(string value, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string v = value.Trim();

        // ISO 8601 date or datetime
        if (DateTimeOffset.TryParseExact(v, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result))
            return true;

        return DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out result);
    }
}
