using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Documents.Merge;
using Harborline.Api.Foundation.Documents.Model;
using Harborline.Api.Foundation.Documents.Rendering;
using Harborline.Api.LocalNodeHost.Data.PackProjection;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// The pack-content schema-validation gate for the Documents pillar (#111 D2): the node parser
/// <see cref="PackTemplateContent"/> validates a pack-authored <c>TemplateDefinition</c> against its pinned
/// JSON contract (<c>_shared/engineering/pack-content-schemas-2026-07-06.md</c>) identically to an in-code
/// template — a HIT renders through the same walker; a malformed body is a miss (the projector logs + skips,
/// never bricking the install). The body under test mirrors the shipped <c>general.invoice</c>.
/// </summary>
public sealed class PackTemplateContentTests
{
    private const string InvoiceContentJson = """
    {
      "key": "general.invoice",
      "version": "1.0.0",
      "documentType": "invoice",
      "recordType": { "type": "invoice", "version": "1" },
      "locale": { "kind": "fixed", "tag": "en-US" },
      "style": { "brandName": "Harborline Software" },
      "structure": [
        { "kind": "header", "lines": [
          { "runs": [ { "literal": "Invoice " }, { "binding": "field.invoiceNumber" } ] } ] },
        { "kind": "repeatingRegion", "label": "Line items", "repeatSection": "lineItems", "columns": [
          { "header": "Description", "binding": "row.description" },
          { "header": "Amount", "binding": "row.amount", "format": "currency", "align": "end" } ] },
        { "kind": "fieldGrid", "label": "Totals", "lines": [
          { "runs": [ { "literal": "Total: " }, { "binding": "field.total", "format": "currency" } ] } ] },
        { "kind": "footer", "label": "Notes", "showWhen": { "expression": "{\"!!\":[{\"var\":\"field.notes\"}]}" },
          "lines": [ { "runs": [ { "binding": "field.notes" } ] } ] }
      ]
    }
    """;

    [Fact]
    public void A_pack_authored_invoice_template_parses_and_renders_through_the_real_pipeline()
    {
        var ok = PackTemplateContent.TryParse(JsonNode.Parse(InvoiceContentJson), out var template, out var error);

        Assert.True(ok, error);
        Assert.Equal("general.invoice", template.Key);
        Assert.Equal("1.0.0", template.Version);
        Assert.Equal("invoice", template.DocumentType);
        Assert.Equal("invoice", template.RecordType.RecordType);
        Assert.Equal(LocalePolicyKind.Fixed, template.Locale.Kind);
        Assert.Equal("en-US", template.Locale.Tag);
        Assert.Equal("Harborline Software", template.Style?.BrandName);
        Assert.Equal(4, template.Structure.Count);

        var repeat = template.Structure.Single(b => b.Kind == DocumentBlockKind.RepeatingRegion);
        Assert.Equal("lineItems", repeat.RepeatSection);
        Assert.Equal(MergeFormat.Currency, repeat.Columns![1].Format);
        Assert.Equal(ColumnAlign.End, repeat.Columns![1].Align);

        // The pack-authored template renders IDENTICALLY to an in-code one — same walker, same output.
        var model = DocumentMergeModel.Build()
            .Field("invoiceNumber", "INV-2026-07-06-AA-0001")
            .Field("total", JsonValue.Create(1699.00m))
            .Field("notes", "Thank you.")
            .Section("lineItems", new List<DocumentMergeRow>
            {
                new("l1", new Dictionary<string, JsonNode?> { ["description"] = JsonValue.Create("Consulting"), ["amount"] = JsonValue.Create("1699.00") }),
            })
            .ToModel();

        var rendered = new DocumentRenderWalker(TimeProvider.System).Render(template, model, new DocumentFormatContext("en-US", "USD"));
        var text = rendered.ExtractText();
        Assert.Contains("INV-2026-07-06-AA-0001", text);
        Assert.Contains("Consulting", text);
        Assert.Contains("Total: $1,699.00", text);
        Assert.Contains("Thank you.", text); // the conditional footer renders (notes present)
    }

    [Theory]
    [InlineData("{}", "missing/blank 'key'")]
    [InlineData("{\"key\":\"k\"}", "missing/blank 'version'")]
    [InlineData("{\"key\":\"k\",\"version\":\"1\",\"documentType\":\"invoice\",\"recordType\":{\"type\":\"invoice\",\"version\":\"1\"},\"locale\":{\"kind\":\"fixed\",\"tag\":\"en-US\"},\"structure\":[]}", "'structure' must be a non-empty array of blocks")]
    public void A_malformed_template_body_is_a_miss_not_an_exception(string json, string expectedFragment)
    {
        var ok = PackTemplateContent.TryParse(JsonNode.Parse(json), out _, out var error);
        Assert.False(ok);
        Assert.Contains(expectedFragment, error);
    }

    [Fact]
    public void A_repeating_region_without_columns_is_a_miss()
    {
        const string json = """
        { "key": "k", "version": "1", "documentType": "invoice",
          "recordType": { "type": "invoice", "version": "1" },
          "locale": { "kind": "fromRecord" },
          "structure": [ { "kind": "repeatingRegion", "repeatSection": "lineItems" } ] }
        """;
        var ok = PackTemplateContent.TryParse(JsonNode.Parse(json), out _, out var error);
        Assert.False(ok);
        Assert.Contains("columns", error);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("tenant")]
    [InlineData("packKey")]
    [InlineData("provenance")]
    public void Authority_metadata_is_server_derived_not_pack_authored(string field)
    {
        var body = Assert.IsType<JsonObject>(JsonNode.Parse(InvoiceContentJson));
        body[field] = "self-declared";

        var ok = PackTemplateContent.TryParse(body, out _, out var error);

        Assert.False(ok);
        Assert.Contains("server-derived", error);
    }
}
