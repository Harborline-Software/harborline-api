using System.Text;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.LocalNodeHost.Data.Configuration;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Configuration;

/// <summary>
/// Goal 3 P3 CONV-1..4: the Released converter reads the optional root <c>exposedDefinitions</c> a bound Platform release
/// writes (builder-definitions revision 19) into the export request exactly as signed, and refuses every other shape
/// with <c>configuration-release-document-malformed</c> at <c>exposedDefinitions</c>.
/// </summary>
/// <remarks>
/// Oracles: the member spelling and its placement after <c>interfaceVersion</c>, one
/// <c>{definitionKey, version, digest}</c> per bound key in ordinal key order, are the Platform Goal 3 P1 exporter's
/// (Platform main c3df8c33, PlatformPackageExporter); the asset-class version 3.1.4, its store digest and the package
/// revision 7.0.0 are the P1 test literals, and the meter digest is the P0 fixture's. The refusal code and target are the
/// converter's existing malformed-document literals. Bodies are opaque markers: this seam reads no body.
/// </remarks>
public sealed class ReleasedPackExposedDefinitionsConversionTests
{
    private const string AssetClassDigest = "sha256:ca09327e39bb416e6a5e7025adf6e6c2ecf9967bcc2b48122ed14d1eb99e6caf";
    private const string MeterDigest = "sha256:a0eda87a3a13a042813b0a8f3b09ede94811258877e5629a944766ea6160a919";

    private const string Bound = """
        {
          "schemaVersion": 1, "packageKey": "eam-core", "revision": "7.0.0",
          "closure": { "dependencies": [{ "key": "acme.finance", "version": "1.0.0" }] },
          "exposes": ["records/eam.asset-class", "records/eam.meter"], "interfaceVersion": 1,
          "exposedDefinitions": [
            { "definitionKey": "records/eam.asset-class", "version": "3.1.4", "digest": "sha256:ca09327e39bb416e6a5e7025adf6e6c2ecf9967bcc2b48122ed14d1eb99e6caf" },
            { "definitionKey": "records/eam.meter", "version": "1.0.0", "digest": "sha256:a0eda87a3a13a042813b0a8f3b09ede94811258877e5629a944766ea6160a919" }
          ],
          "items": [
            { "id": "asset-class", "content": { "classification": "present", "payload": {
              "definitionKey": "records/eam.asset-class", "packageKey": "eam-core",
              "contentKind": "AssetTypeDefinition", "body": { "marker": "asset-class" }
            } } },
            { "id": "meter", "content": { "classification": "present", "payload": {
              "definitionKey": "records/eam.meter", "packageKey": "eam-core",
              "contentKind": "AssetTypeDefinition", "body": { "marker": "meter" }
            } } },
            { "id": "foreign", "content": { "classification": "present", "payload": {
              "definitionKey": "records/foreign", "packageKey": "acme.finance",
              "contentKind": "AssetTypeDefinition", "body": { "marker": "foreign" }
            } } }
          ]
        }
        """;

    [Fact(DisplayName = "goal 3 P3 CONV-1: a release without exposedDefinitions converts with none")]
    public void A_release_without_exposed_definitions_converts_with_none()
    {
        var document = JsonNode.Parse(Bound)!.AsObject();
        Assert.True(document.Remove("exposedDefinitions"));

        var converted = Convert(document);

        Assert.Equal(["records/eam.asset-class", "records/eam.meter"], converted.Request!.Exposes);
        Assert.Equal(1, converted.Request.InterfaceVersion);
        Assert.Null(converted.Request.ExposedDefinitions);
    }

    [Fact(DisplayName = "goal 3 P3 CONV-1: bound entries are carried exactly as signed, never derived from the package revision")]
    public void Bound_entries_are_carried_exactly_as_signed()
    {
        var converted = Convert(JsonNode.Parse(Bound)!.AsObject());

        Assert.Equal(
            [new PackExposedDefinition("records/eam.asset-class", "3.1.4", AssetClassDigest),
             new PackExposedDefinition("records/eam.meter", "1.0.0", MeterDigest)],
            converted.Request!.ExposedDefinitions);
        // Every item still carries the package revision; the entries do not.
        Assert.All(converted.Request.Contents, content => Assert.Equal("7.0.0", content.Version));
        Assert.Single(converted.ForeignEdits);
    }

    public static TheoryData<string> MalformedShapes() => new()
    {
        "without an exposure",
        "not an array",
        "an empty array",
        "an entry that is not an object",
        "an entry without a digest",
        "a version that is not a string",
        "a blank version",
        "an uppercase digest",
        "a repeated key",
    };

    [Theory(DisplayName = "goal 3 P3 CONV-2: a malformed exposedDefinitions refuses configuration-release-document-malformed")]
    [MemberData(nameof(MalformedShapes))]
    public void A_malformed_exposed_definitions_member_refuses(string shape)
    {
        var document = JsonNode.Parse(Bound)!.AsObject();
        var first = document["exposedDefinitions"]![0]!.AsObject();
        switch (shape)
        {
            case "without an exposure":
                document.Remove("exposes");
                document.Remove("interfaceVersion");
                break;
            case "not an array":
                document["exposedDefinitions"] = first.DeepClone();
                break;
            case "an empty array":
                document["exposedDefinitions"] = new JsonArray();
                break;
            case "an entry that is not an object":
                document["exposedDefinitions"] = new JsonArray("records/eam.asset-class");
                break;
            case "an entry without a digest":
                first.Remove("digest");
                break;
            case "a version that is not a string":
                first["version"] = 3;
                break;
            case "a blank version":
                first["version"] = " ";
                break;
            case "an uppercase digest":
                first["digest"] = "sha256:" + AssetClassDigest["sha256:".Length..].ToUpperInvariant();
                break;
            case "a repeated key":
                document["exposedDefinitions"]![1]!["definitionKey"] = "records/eam.asset-class";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }

        AssertMalformed(document);
    }

    [Fact(DisplayName = "goal 3 P3 CONV-3: an entry for an own definition the release does not expose refuses")]
    public void An_entry_for_an_unexposed_key_refuses()
    {
        var document = JsonNode.Parse(Bound)!.AsObject();
        document["exposes"] = new JsonArray("records/eam.asset-class");

        AssertMalformed(document);
    }

    [Fact(DisplayName = "goal 3 P3 CONV-4: an entry for a foreign edit refuses; a foreign definition's facts are never carried")]
    public void An_entry_for_a_foreign_definition_refuses()
    {
        var document = JsonNode.Parse(Bound)!.AsObject();
        document["exposedDefinitions"]![1]!["definitionKey"] = "records/foreign";

        AssertMalformed(document);
    }

    private static ReleasedPackConversionResult Convert(JsonObject document) =>
        ReleasedPackConversion.Convert(Encoding.UTF8.GetBytes(document.ToJsonString()), "fixture-principal");

    private static void AssertMalformed(JsonObject document)
    {
        var refused = Assert.Throws<ReleasedPackConversionException>(() => Convert(document));
        Assert.Equal(("configuration-release-document-malformed", "exposedDefinitions"), (refused.Code, refused.Target));
    }
}
