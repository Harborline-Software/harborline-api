using System.Text.Json;
using System.Text.Json.Nodes;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Compatibility;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;

using NSubstitute;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Forms;

public sealed class CatalogueFieldSourceContractTests
{
    internal const string Declaration = """
        {"capabilityId":"forms.catalogue-field-source","coordinateSchemaVersion":1,"sourceMappingSchemaVersion":1,
         "sourceKind":"FormDefinition","fields":[
          {"fieldId":"formId","source":"catalogue.entry.formId"},
          {"fieldId":"title","source":"catalogue.entry.title"},
          {"fieldId":"version","source":"catalogue.entry.version"},
          {"fieldId":"cascadeLayer","source":"catalogue.entry.cascadeLayer"}]}
        """;

    internal static JsonObject Content(bool typed = true)
    {
        var root = JsonNode.Parse("""
            {"overlay":{"title":{"defaultLocale":"en","values":{"en":"Form details"}},
              "fields":{},"sections":[{"id":"details","title":{"defaultLocale":"en","values":{"en":"Details"}},
              "fields":["formId","title","version","cascadeLayer"]}],"rules":[]},"fieldsMeta":{}}
            """)!.AsObject();
        foreach (var field in CatalogueFieldSourceContract.Fields)
        {
            root["overlay"]!["fields"]![field.FieldId] = JsonNode.Parse($$$"""
                {"label":{"defaultLocale":"en","values":{"en":"{{{field.FieldId}}}"}},"controlHint":"text","piiSensitivity":"None"}
                """);
            root["fieldsMeta"]![field.FieldId] = JsonNode.Parse("""{"type":"text","required":false}""");
        }
        if (typed) root["catalogueFieldSource"] = JsonNode.Parse(Declaration);
        return root;
    }

    internal static PackComposedItem Item(string json, IReadOnlyList<string>? requirements = null, string? seed = null)
        => new("tests.catalogue-details", "platform.detail.form", PackContentKind.FormDefinition, "1.0.0", json,
            requirements ?? [CatalogueFieldSourceContract.CapabilityId], seed);

    [Fact]
    public async Task Producer_model_authoring_DTO_and_pack_content_round_trip_the_declaration()
    {
        var content = Content();
        Assert.True(PackFormDefinitionContent.TryParse(content, out var request, out var error), error);
        var schema = await new InMemorySchemaRegistry(TimeProvider.System)
            .RegisterAsync(BuilderSchemaSynthesizer.Synthesize(request, new FormDefinitionId("platform.detail.form")));
        var definition = FormDefinitionRoutes.BuildDefinition(new FormDefinitionId("platform.detail.form"),
            new SemanticVersion(1, 0, 0), new TenantId("test"), IdentityRef.System, schema.Id,
            request.Overlay, DateTimeOffset.UnixEpoch, request.CatalogueFieldSource);
        var frozen = FormDefinitionFreezer.Freeze(definition);
        Assert.NotNull(frozen.CatalogueFieldSource);
        Assert.Equal(CatalogueFieldSourceContract.Fields, frozen.CatalogueFieldSource.Fields);
        var dto = JsonSerializer.SerializeToNode(FormDefinitionDto.From(frozen))!;
        Assert.True(JsonNode.DeepEquals(content["catalogueFieldSource"], dto["catalogueFieldSource"]));
        var exported = PackFormDefinitionContent.ToContent(frozen, schema);
        Assert.True(JsonNode.DeepEquals(content["catalogueFieldSource"], exported["catalogueFieldSource"]));
        Assert.True(PackFormDefinitionContent.TryParse(exported, out var imported, out error), error);
        Assert.Equal(request.CatalogueFieldSource!.Fields, imported.CatalogueFieldSource!.Fields);
    }

    [Fact]
    public void Legacy_content_does_not_synthesize_or_serialize_a_mapping()
    {
        Assert.True(PackFormDefinitionContent.TryParse(Content(false), out var request, out var error), error);
        Assert.Null(request.CatalogueFieldSource);
        Assert.Null(JsonSerializer.SerializeToNode(request)!["catalogueFieldSource"]);
        Assert.Empty(new CatalogueFieldSourceAdmission().Validate([Item(Content(false).ToJsonString())]));
    }

    [Theory]
    [InlineData("\"coordinateSchemaVersion\":1,", "", CatalogueFieldSourceCodes.MissingSupportDeclaration)]
    [InlineData("\"sourceMappingSchemaVersion\":1,", "", CatalogueFieldSourceCodes.MissingSupportDeclaration)]
    [InlineData("\"capabilityId\":\"forms.catalogue-field-source\",", "", CatalogueFieldSourceCodes.MissingSupportDeclaration)]
    [InlineData("\"coordinateSchemaVersion\":1", "\"coordinateSchemaVersion\":\"1\"", CatalogueFieldSourceCodes.MalformedSourceMapping)]
    [InlineData("\"coordinateSchemaVersion\":1", "\"coordinateSchemaVersion\":1.0", CatalogueFieldSourceCodes.MalformedSourceMapping)]
    [InlineData("\"coordinateSchemaVersion\":1", "\"coordinateSchemaVersion\":1e0", CatalogueFieldSourceCodes.MalformedSourceMapping)]
    [InlineData("\"coordinateSchemaVersion\":1", "\"coordinateSchemaVersion\":null", CatalogueFieldSourceCodes.MalformedSourceMapping)]
    [InlineData("\"coordinateSchemaVersion\":1", "\"coordinateSchemaVersion\":2", CatalogueFieldSourceCodes.UnsupportedCapabilityOrSchemaVersion)]
    [InlineData("\"sourceMappingSchemaVersion\":1", "\"sourceMappingSchemaVersion\":2", CatalogueFieldSourceCodes.UnsupportedCapabilityOrSchemaVersion)]
    [InlineData("forms.catalogue-field-source", "forms.catalogue-field-source.future", CatalogueFieldSourceCodes.UnsupportedCapabilityOrSchemaVersion)]
    [InlineData("FormDefinition", "formdefinition", CatalogueFieldSourceCodes.UnknownSourceMapping)]
    [InlineData("FormDefinition", "ViewDefinition", CatalogueFieldSourceCodes.UnsupportedSourceMapping)]
    [InlineData("catalogue.entry.title", "catalogue.entry.body.title", CatalogueFieldSourceCodes.UnknownSourceMapping)]
    [InlineData("\"fieldId\":\"title\"", "\"fieldId\":\"unknown\"", CatalogueFieldSourceCodes.UnknownSourceMapping)]
    [InlineData("\"fieldId\":\"title\"", "\"fieldId\":\"version\"", CatalogueFieldSourceCodes.MalformedSourceMapping)]
    [InlineData("\"sourceKind\":", "\"unknown\":true,\"sourceKind\":", CatalogueFieldSourceCodes.MalformedSourceMapping)]
    [InlineData("\"fieldId\":\"title\"", "\"fieldId\":\"title\",\"field\\u0049d\":\"title\"", CatalogueFieldSourceCodes.MalformedSourceMapping)]
    [InlineData("\"sourceKind\":", "\"sourceKind\":\"FormDefinition\",\"sourceKind\":", CatalogueFieldSourceCodes.MalformedSourceMapping)]
    public void Strict_declaration_refusals(string find, string replacement, string code)
    {
        var json = Declaration.Replace(find, replacement, StringComparison.Ordinal);
        Assert.Equal(code, Assert.Throws<CatalogueFieldSourceException>(() =>
            JsonSerializer.Deserialize<CatalogueFieldSource>(json)).Code);
        var content = Content(false).ToJsonString()[..^1] + ",\"catalogueFieldSource\":" + json + "}";
        Assert.False(PackFormDefinitionContent.TryParse(content, out _, out _, out var error));
        Assert.Equal(code, error);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    public void Explicit_wrong_shape_is_not_legacy(string declaration)
    {
        var content = Content(false);
        content["catalogueFieldSource"] = JsonNode.Parse(declaration);
        Assert.False(PackFormDefinitionContent.TryParse(content, out _, out var error));
        Assert.Equal(CatalogueFieldSourceCodes.MalformedSourceMapping, error);
    }

    [Theory]
    [InlineData("overlay-field")]
    [InlineData("metadata-field")]
    [InlineData("section-order")]
    [InlineData("mapping-order")]
    [InlineData("extra-mapping")]
    [InlineData("compute")]
    [InlineData("action")]
    public void Invalid_render_correspondence_refuses_before_runtime_support_check(string mutation)
    {
        var content = Content();
        switch (mutation)
        {
            case "overlay-field": content["overlay"]!["fields"]!.AsObject().Remove("title"); break;
            case "metadata-field": content["fieldsMeta"]!.AsObject().Remove("title"); break;
            case "section-order": content["overlay"]!["sections"]![0]!["fields"]![0] = "title"; break;
            case "mapping-order":
                var fields = content["catalogueFieldSource"]!["fields"]!.AsArray();
                var first = fields[0]!.DeepClone();
                fields[0] = fields[1]!.DeepClone(); fields[1] = first; break;
            case "extra-mapping": content["catalogueFieldSource"]!["fields"]!.AsArray().Add(content["catalogueFieldSource"]!["fields"]![0]!.DeepClone()); break;
            case "compute": content["overlay"]!["rules"] = JsonNode.Parse("""[{"action":"Compute","scopeTarget":"title","expression":"123"}]"""); break;
            case "action": content["overlay"]!["sections"]![0]!["items"] = JsonNode.Parse("""[{"kind":"Action","key":"submit"}]"""); break;
        }
        var calls = 0;
        var admission = new CatalogueFieldSourceAdmission(_ => { calls++; return true; });
        Assert.Equal(CatalogueFieldSourceCodes.MalformedSourceMapping, Assert.Single(admission.Validate([Item(content.ToJsonString())])).Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Host_requires_both_signed_declaration_and_registered_runtime_support()
    {
        var item = Item(Content().ToJsonString());
        Assert.Equal(CatalogueFieldSourceCodes.UnsupportedCapabilityOrSchemaVersion,
            Assert.Single(new CatalogueFieldSourceAdmission().Validate([item])).Code);
        var admission = new CatalogueFieldSourceAdmission(_ => true);
        Assert.Empty(admission.Validate([item]));
        Assert.Equal(CatalogueFieldSourceCodes.MissingSupportDeclaration,
            Assert.Single(admission.Validate([item with { CapabilityRequirements = [] }])).Code);
        Assert.Equal(CatalogueFieldSourceCodes.MalformedSourceMapping,
            Assert.Single(admission.Validate([item with { CapabilityRequirements = [CatalogueFieldSourceContract.CapabilityId, CatalogueFieldSourceContract.CapabilityId] }])).Code);
        Assert.DoesNotContain(CatalogueFieldSourceContract.CapabilityId,
            new PackPlatformCompatibility("1.0.0", PackSeedProjector.RegisteredCases).Provides);
    }

    [Fact]
    public void Composed_admission_does_not_allow_an_override_to_erase_the_signed_mapping()
    {
        var adapter = new PackWorkflowAdmissionAdapter(Substitute.For<IWorkflowAdmissionValidator>(),
            catalogueFields: new CatalogueFieldSourceAdmission(_ => true));
        var result = adapter.Admit([Item(Content(false).ToJsonString(), seed: Content().ToJsonString())], new TenantId("test"));
        Assert.Equal(CatalogueFieldSourceCodes.MalformedSourceMapping, Assert.Single(result.Refusals).Code);
    }

    [Theory]
    [InlineData("\"version\":\"2.3.4\",", "", CatalogueFieldSourceCodes.MalformedCoordinate)]
    [InlineData("2.3.4", "latest", CatalogueFieldSourceCodes.MalformedCoordinate)]
    [InlineData("2.3.4", "02.3.4", CatalogueFieldSourceCodes.MalformedCoordinate)]
    [InlineData("2.3.4", "2.3.4-beta", CatalogueFieldSourceCodes.MalformedCoordinate)]
    [InlineData("2.3.4", "2147483648.3.4", CatalogueFieldSourceCodes.MalformedCoordinate)]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":1.0", CatalogueFieldSourceCodes.MalformedCoordinate)]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":2", CatalogueFieldSourceCodes.UnsupportedCoordinate)]
    [InlineData("\"field\":\"title\"", "\"field\":\"secret\"", CatalogueFieldSourceCodes.UnknownCoordinate)]
    [InlineData("FormDefinition", "ViewDefinition", CatalogueFieldSourceCodes.UnsupportedCoordinate)]
    [InlineData("FormDefinition", "UnknownDefinition", CatalogueFieldSourceCodes.UnknownCoordinate)]
    [InlineData("\"id\":\"source\"", "\"id\":\"..\"", CatalogueFieldSourceCodes.MalformedCoordinate)]
    [InlineData("\"id\":\"source\"", "\"id\":\"\\uD800\"", CatalogueFieldSourceCodes.MalformedCoordinate)]
    [InlineData("\"id\":\"source\"", "\"id\":\"a\\n\"", CatalogueFieldSourceCodes.MalformedCoordinate)]
    [InlineData("\"id\":\"source\"", "\"id\":\"source\",\"\\u0069d\":\"other\"", CatalogueFieldSourceCodes.MalformedCoordinate)]
    [InlineData("\"kind\":\"tenant\"", "\"kind\":\"tenant\",\"packKey\":\"p\"", CatalogueFieldSourceCodes.MalformedSourceBinding)]
    [InlineData("\"kind\":\"tenant\"", "\"kind\":\"pack\"", CatalogueFieldSourceCodes.MalformedSourceBinding)]
    [InlineData("sha256:", "SHA256:", CatalogueFieldSourceCodes.MalformedSourceBinding)]
    public void Typed_request_refuses_invalid_coordinate_or_binding(string find, string replacement, string code)
    {
        var json = RequestJson().Replace(find, replacement, StringComparison.Ordinal);
        Assert.Equal(code, Assert.Throws<CatalogueFieldSourceException>(() => JsonSerializer.Deserialize<CatalogueFieldReadRequest>(json)).Code);
    }

    [Fact]
    public void Typed_request_preserves_exact_unicode_version_and_separate_binding()
    {
        var request = JsonSerializer.Deserialize<CatalogueFieldReadRequest>(RequestJson().Replace("\"source\"", "\"tenant:acme/été%\"", StringComparison.Ordinal))!;
        Assert.Equal("tenant:acme/été%", request.Coordinate.Id);
        Assert.Equal("2.3.4", request.Coordinate.Version);
        Assert.Equal(request, JsonSerializer.Deserialize<CatalogueFieldReadRequest>(JsonSerializer.Serialize(request)));
    }

    private static string RequestJson() => $$$$"""
        {"coordinate":{"schemaVersion":1,"kind":"FormDefinition","id":"source","version":"2.3.4","field":"title"},
         "sourceBinding":{"definitionHash":"sha256:{{{{new string('a', 64)}}}}","provenance":{"kind":"tenant"}}}
        """;
}
