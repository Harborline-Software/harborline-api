using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

using Harborline.Api.LocalNodeHost.Data.PackProjection;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// T-1026 step 2: the two shipped invoice templates declare app contract 1.0 at the template content's flat
/// root, where <c>PackTemplateContent.TryParse</c> reads it. The test reads the committed pack files byte-for-byte from the test output (tests.csproj links
/// both), so deleting or changing either declaration turns it red.
/// </summary>
/// <remarks>
/// Oracle (T-724 ruling 121): the expected major 1 and minor 0 are literals from T-572 owner ruling Q5 (the
/// first app contract is 1.0); they are deliberately not read from the platform's supported contract window.
/// </remarks>
public sealed class ShippedInvoiceTemplateContractTests
{
    [Theory]
    [InlineData("Fixtures/general-pack.export.json", "general.invoice")]
    [InlineData("property-management-pack.export.json", "property-management.rent-invoice")]
    public void Shipped_invoice_template_declares_app_contract_1_0(string packFile, string templateKey)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Packs", packFile);
        var request = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(path)));
        var item = Assert.Single(
            Assert.IsType<JsonArray>(request["contents"]).OfType<JsonObject>(),
            candidate => (string?)candidate["key"] == templateKey);
        Assert.Equal("TemplateDefinition", (string?)item["kind"]);

        var parsed = PackTemplateContent.TryParse(item["content"], out var template, out var error);

        Assert.True(parsed, error);
        var contract = template.Envelope.Contract;
        Assert.NotNull(contract);
        Assert.Equal(1, contract.Major);
        Assert.Equal(0, contract.Minor);
    }
}
