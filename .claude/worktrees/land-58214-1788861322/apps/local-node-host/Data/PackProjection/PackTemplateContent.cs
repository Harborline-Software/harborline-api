using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Documents.Model;

namespace Harborline.Api.LocalNodeHost.Data.PackProjection;

/// <summary>
/// Parses + validates the PINNED canonical JSON contract for a pack content item of kind
/// <see cref="Harborline.Api.Foundation.Packs.Model.PackContentKind.TemplateDefinition"/> (#111) into a
/// <see cref="TemplateDefinition"/> (design note <c>_shared/engineering/pack-content-schemas-2026-07-06.md</c>).
/// The node's <see cref="PackSeedProjector"/> calls <see cref="TryParse"/> at install-activate time and, on
/// a HIT, publishes the template into the <c>IDocumentTemplateRegistry</c> the render pipeline reads.
/// </summary>
/// <remarks>
/// Mirrors <see cref="PackAssetTypeContent"/>: a malformed item is a <see cref="TryParse"/> miss (the
/// projector logs + skips it, never bricking the install), NOT an exception — one bad template in a pack
/// cannot deny-of-service every other item it ships. Validation is deliberately structural (the binding
/// grammar itself is validated lazily by the rule engine at render time, exactly as an authored template).
/// </remarks>
internal static class PackTemplateContent
{
    /// <summary>Attempts to parse a <c>TemplateDefinition</c> content body. Returns false with an error code on malformed input.</summary>
    public static bool TryParse(JsonNode? content, out TemplateDefinition template, out string error)
        => TryParse(content, TenantId.System, out template, out error);

    /// <summary>Parses a pack template and stamps the tenant that installed the authoritative content.</summary>
    public static bool TryParse(
        JsonNode? content,
        TenantId tenant,
        out TemplateDefinition template,
        out string error)
    {
        template = null!;
        error = string.Empty;

        if (content is not JsonObject obj)
        {
            error = "content is not a JSON object";
            return false;
        }

        foreach (var authorityField in new[] { "owner", "tenant", "packKey", "provenance" })
        {
            if (obj.ContainsKey(authorityField))
            {
                error = $"authority field '{authorityField}' is server-derived and must not appear in content";
                return false;
            }
        }

        var key = ReadString(obj, "key");
        if (string.IsNullOrWhiteSpace(key))
        {
            error = "missing/blank 'key'";
            return false;
        }

        var version = ReadString(obj, "version");
        if (string.IsNullOrWhiteSpace(version))
        {
            error = "missing/blank 'version'";
            return false;
        }

        var documentType = ReadString(obj, "documentType");
        if (string.IsNullOrWhiteSpace(documentType))
        {
            error = "missing/blank 'documentType'";
            return false;
        }

        if (obj["recordType"] is not JsonObject recordTypeObj)
        {
            error = "missing 'recordType' object";
            return false;
        }

        var recordType = ReadString(recordTypeObj, "type");
        var recordVersion = ReadString(recordTypeObj, "version");
        if (string.IsNullOrWhiteSpace(recordType) || string.IsNullOrWhiteSpace(recordVersion))
        {
            error = "'recordType' requires non-empty 'type' + 'version'";
            return false;
        }

        if (!TryParseLocale(obj["locale"], out var locale, out error))
        {
            return false;
        }

        var style = obj["style"] is JsonObject styleObj
            ? new DocumentStyleRef(ReadString(styleObj, "brandName"), ReadString(styleObj, "logoAssetRef"))
            : null;

        if (obj["structure"] is not JsonArray structureArr || structureArr.Count == 0)
        {
            error = "'structure' must be a non-empty array of blocks";
            return false;
        }

        var blocks = new List<DocumentBlock>(structureArr.Count);
        foreach (var blockNode in structureArr)
        {
            if (!TryParseBlock(blockNode, out var block, out error))
            {
                return false;
            }

            blocks.Add(block);
        }

        template = new TemplateDefinition(
            Envelope: new DefinitionEnvelope<string, string, TenantId, string?>(
                key!.Trim(),
                version!.Trim(),
                tenant,
                CascadeLayer.Pack,
                Provenance: null,
                Array.Empty<DefinitionRequirement>()),
            DocumentType: documentType!.Trim(),
            RecordType: new RecordTypeBinding(recordType!.Trim(), recordVersion!.Trim()),
            Locale: locale,
            Style: style,
            Structure: blocks);
        return true;
    }

    private static bool TryParseLocale(JsonNode? node, out DocumentLocalePolicy locale, out string error)
    {
        error = string.Empty;
        locale = DocumentLocalePolicy.FromRecord;

        if (node is not JsonObject obj)
        {
            error = "missing 'locale' object";
            return false;
        }

        var kind = ReadString(obj, "kind")?.ToLowerInvariant();
        switch (kind)
        {
            case "fixed":
                var tag = ReadString(obj, "tag");
                if (string.IsNullOrWhiteSpace(tag))
                {
                    error = "'locale.kind' fixed requires a 'tag'";
                    return false;
                }

                locale = DocumentLocalePolicy.Fixed(tag!.Trim());
                return true;
            case "fromrecord":
                locale = DocumentLocalePolicy.FromRecord;
                return true;
            case "frominstance":
                locale = DocumentLocalePolicy.FromInstance;
                return true;
            default:
                error = "'locale.kind' must be one of fixed|fromRecord|fromInstance";
                return false;
        }
    }

    private static bool TryParseBlock(JsonNode? node, out DocumentBlock block, out string error)
    {
        block = null!;
        error = string.Empty;

        if (node is not JsonObject obj)
        {
            error = "a structure entry is not a JSON object";
            return false;
        }

        if (!TryParseBlockKind(ReadString(obj, "kind"), out var kind, out error))
        {
            return false;
        }

        var label = ReadString(obj, "label");
        var showWhen = TryParseGuard(obj["showWhen"]);

        if (kind == DocumentBlockKind.RepeatingRegion)
        {
            var repeatSection = ReadString(obj, "repeatSection");
            if (string.IsNullOrWhiteSpace(repeatSection))
            {
                error = "a repeatingRegion block requires 'repeatSection'";
                return false;
            }

            if (obj["columns"] is not JsonArray columnsArr || columnsArr.Count == 0)
            {
                error = "a repeatingRegion block requires a non-empty 'columns' array";
                return false;
            }

            var columns = new List<DocumentColumn>(columnsArr.Count);
            foreach (var colNode in columnsArr)
            {
                if (colNode is not JsonObject colObj)
                {
                    error = "a column entry is not a JSON object";
                    return false;
                }

                var header = ReadString(colObj, "header") ?? string.Empty;
                var binding = ReadString(colObj, "binding");
                if (string.IsNullOrWhiteSpace(binding))
                {
                    error = "a column requires a 'binding'";
                    return false;
                }

                columns.Add(new DocumentColumn(header, binding!.Trim(), ParseFormat(ReadString(colObj, "format")), ParseAlign(ReadString(colObj, "align"))));
            }

            block = new DocumentBlock
            {
                Kind = kind,
                Label = label,
                RepeatSection = repeatSection!.Trim(),
                Columns = columns,
                ShowWhen = showWhen,
            };
            return true;
        }

        // Text-bearing blocks (header/footer/section/fieldGrid): lines of literal + merge runs.
        var lines = new List<DocumentLine>();
        if (obj["lines"] is JsonArray linesArr)
        {
            foreach (var lineNode in linesArr)
            {
                if (lineNode is not JsonObject lineObj || lineObj["runs"] is not JsonArray runsArr)
                {
                    error = "a line entry requires a 'runs' array";
                    return false;
                }

                var runs = new List<DocumentInline>(runsArr.Count);
                foreach (var runNode in runsArr)
                {
                    if (runNode is not JsonObject runObj)
                    {
                        error = "a run entry is not a JSON object";
                        return false;
                    }

                    var literal = ReadString(runObj, "literal");
                    var binding = ReadString(runObj, "binding");
                    if (literal is not null)
                    {
                        runs.Add(DocumentInline.OfLiteral(literal));
                    }
                    else if (!string.IsNullOrWhiteSpace(binding))
                    {
                        runs.Add(DocumentInline.OfMerge(binding!.Trim(), ParseFormat(ReadString(runObj, "format")), ReadString(runObj, "fallback")));
                    }
                    else
                    {
                        error = "a run requires either 'literal' or 'binding'";
                        return false;
                    }
                }

                lines.Add(new DocumentLine(runs));
            }
        }

        block = new DocumentBlock
        {
            Kind = kind,
            Label = label,
            Lines = lines,
            ShowWhen = showWhen,
        };
        return true;
    }

    private static DocumentBlockGuard? TryParseGuard(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        var expression = ReadString(obj, "expression");
        if (!string.IsNullOrWhiteSpace(expression))
        {
            return DocumentBlockGuard.Inline(expression!);
        }

        var namedRule = ReadString(obj, "namedRule");
        return string.IsNullOrWhiteSpace(namedRule) ? null : DocumentBlockGuard.Named(namedRule!, ReadString(obj, "namedRuleVersion"));
    }

    private static bool TryParseBlockKind(string? raw, out DocumentBlockKind kind, out string error)
    {
        error = string.Empty;
        switch (raw?.ToLowerInvariant())
        {
            case "header": kind = DocumentBlockKind.Header; return true;
            case "footer": kind = DocumentBlockKind.Footer; return true;
            case "section": kind = DocumentBlockKind.Section; return true;
            case "fieldgrid": kind = DocumentBlockKind.FieldGrid; return true;
            case "repeatingregion": kind = DocumentBlockKind.RepeatingRegion; return true;
            default:
                kind = DocumentBlockKind.Section;
                error = "block 'kind' must be one of header|footer|section|fieldGrid|repeatingRegion";
                return false;
        }
    }

    private static MergeFormat ParseFormat(string? raw) => raw?.ToLowerInvariant() switch
    {
        "currency" => MergeFormat.Currency,
        "date" => MergeFormat.Date,
        "integer" => MergeFormat.Integer,
        "decimal" => MergeFormat.Decimal,
        _ => MergeFormat.Text,
    };

    private static ColumnAlign ParseAlign(string? raw) => raw?.ToLowerInvariant() switch
    {
        "center" => ColumnAlign.Center,
        "end" => ColumnAlign.End,
        _ => ColumnAlign.Start,
    };

    private static string? ReadString(JsonObject obj, string key)
        => obj.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s)
            ? s
            : null;
}
