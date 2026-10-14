using System.Text;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;

using Harborline.Api.LocalNodeHost.Data.Configuration;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Configuration;

/// <summary>
/// Preserves owned exposure through conversion and signing, and refuses foreign assertions.
/// Ownership admission against the installed catalogue remains the installer’s K8 responsibility.
/// </summary>
public sealed class ReleasedPackExposureConversionTests
{
    // Literal converter-seam fixtures, not artifacts emitted by the currently pinned producer.
    // Root exposure spelling comes from frozen Platform #274 (9657407b), PlatformPackageExporter;
    // ConfigurationProposal derives that list from all exposed edits, including foreign edits.
    // Ownership/closure oracles come from existing K8/T-982 conversion rules. Omitted provenance
    // fields are not read by this seam. Neither fixture proves producer or install integration.
    private const string MixedExposure = """
        {
          "packageKey": "tenant.release", "revision": "2.0.0",
          "closure": { "dependencies": [{ "key": "acme.finance", "version": "1.0.0" }] },
          "exposes": ["records/foreign", "records/own"], "interfaceVersion": 3,
          "items": [
            { "id": "own", "content": { "classification": "present", "payload": {
              "definitionKey": "records/own", "packageKey": "tenant.release",
              "contentKind": "AssetTypeDefinition", "body": { "marker": "own-body" }
            } } },
            { "id": "foreign", "content": { "classification": "present", "payload": {
              "definitionKey": "records/foreign", "packageKey": "acme.finance",
              "contentKind": "AssetTypeDefinition", "body": { "marker": "foreign-body" }
            } } }
          ]
        }
        """;

    private const string ForeignOnlyExposure = """
        {
          "packageKey": "tenant.release", "revision": "2.0.0",
          "closure": { "dependencies": [{ "key": "acme.finance", "version": "1.0.0" }] },
          "exposes": ["records/foreign"], "interfaceVersion": 3,
          "items": [
            { "id": "foreign", "content": { "classification": "present", "payload": {
              "definitionKey": "records/foreign", "packageKey": "acme.finance",
              "contentKind": "AssetTypeDefinition", "body": { "marker": "foreign-body" }
            } } }
          ]
        }
        """;

    [Fact]
    public void Owned_exposure_in_a_mixed_release_does_not_make_foreign_edits_own_content()
    {
        var document = MixedExposure.Replace("[\"records/foreign\", \"records/own\"]", "[\"records/own\"]", StringComparison.Ordinal);
        var converted = ReleasedPackConversion.Convert(Encoding.UTF8.GetBytes(document), "fixture-principal");

        Assert.Equal("tenant.release", converted.PackageKey);
        Assert.NotNull(converted.Request);
        Assert.Equal("tenant.release", converted.Request.Key);
        Assert.Equal("2.0.0", converted.Request.Version);
        var own = Assert.Single(converted.Request.Contents);
        Assert.Equal("records/own", own.Key);
        Assert.Equal(PackContentKind.AssetTypeDefinition, own.Kind);
        Assert.Equal("2.0.0", own.Version);
        Assert.Equal("own-body", own.Content["marker"]!.GetValue<string>());
        var foreign = Assert.Single(converted.ForeignEdits);
        Assert.Equal("records/foreign", foreign.DefinitionKey);
        Assert.Equal("acme.finance", foreign.PackageKey);
        Assert.Equal(PackContentKind.AssetTypeDefinition, foreign.Kind);
        Assert.Equal("foreign-body", foreign.Body["marker"]!.GetValue<string>());
        var pin = Assert.Single(converted.Dependencies);
        Assert.Equal(("acme.finance", "1.0.0"), (pin.Key, pin.Version));
        var requestPin = Assert.Single(converted.Request.Dependencies);
        Assert.Equal(("acme.finance", "1.0.0"), (requestPin.Key, requestPin.Version));
        Assert.Equal(new[] { "records/own" }, converted.Request.Exposes);
        Assert.Equal(3, converted.Request.InterfaceVersion);
    }

    [Fact]
    public void Legacy_foreign_only_release_has_no_own_export_request()
    {
        var converted = ReleasedPackConversion.Convert(Encoding.UTF8.GetBytes(WithoutExposure(ForeignOnlyExposure)), "fixture-principal");

        Assert.Equal("tenant.release", converted.PackageKey);
        Assert.Null(converted.Request);
        var foreign = Assert.Single(converted.ForeignEdits);
        Assert.Equal(("records/foreign", "acme.finance"), (foreign.DefinitionKey, foreign.PackageKey));
        Assert.Equal(PackContentKind.AssetTypeDefinition, foreign.Kind);
        Assert.Equal("foreign-body", foreign.Body["marker"]!.GetValue<string>());
        var pin = Assert.Single(converted.Dependencies);
        Assert.Equal(("acme.finance", "1.0.0"), (pin.Key, pin.Version));
        // Existing foreign narrowing behavior is retained when no exposure assertion is made.
    }

    private static string WithoutExposure(string document)
    {
        var root = JsonNode.Parse(document)!.AsObject();
        root.Remove("exposes");
        root.Remove("interfaceVersion");
        return root.ToJsonString();
    }

    [Theory]
    [InlineData(MixedExposure)]
    [InlineData(ForeignOnlyExposure)]
    public void Foreign_exposure_refuses_before_an_export_request_is_returned(string document)
    {
        var refused = Assert.Throws<ReleasedPackConversionException>(() =>
            ReleasedPackConversion.Convert(Encoding.UTF8.GetBytes(document), "fixture-principal"));
        Assert.Equal("configuration-release-foreign-definition", refused.Code);
        Assert.Equal("records/foreign", refused.Target);
    }

    [Fact]
    public void A_conflicting_own_claim_does_not_hide_a_foreign_exposure_claim()
    {
        var document = JsonNode.Parse(MixedExposure)!.AsObject();
        document["items"]![0]!["content"]!["payload"]!["definitionKey"] = "records/foreign";
        document["exposes"] = new JsonArray("records/foreign");

        var refused = Assert.Throws<ReleasedPackConversionException>(() =>
            ReleasedPackConversion.Convert(Encoding.UTF8.GetBytes(document.ToJsonString()), "fixture-principal"));
        Assert.Equal("configuration-release-foreign-definition", refused.Code);
        Assert.Equal("records/foreign", refused.Target);
    }

    [Fact]
    public void Malformed_foreign_only_exposure_is_checked_before_the_null_request_return()
    {
        var document = ForeignOnlyExposure.Replace("\"interfaceVersion\": 3", "\"interfaceVersion\": 0", StringComparison.Ordinal);
        var refused = Assert.Throws<ReleasedPackConversionException>(() =>
            ReleasedPackConversion.Convert(Encoding.UTF8.GetBytes(document), "fixture-principal"));
        Assert.Equal("configuration-release-document-malformed", refused.Code);
        Assert.Equal("interfaceVersion", refused.Target);
    }

    [Theory]
    [InlineData(MixedExposure)]
    [InlineData(ForeignOnlyExposure)]
    public void Exposure_does_not_authorize_an_unpinned_foreign_owner(string document)
    {
        var unpinned = document.Replace("{ \"key\": \"acme.finance\", \"version\": \"1.0.0\" }", "", StringComparison.Ordinal);

        var refused = Assert.Throws<ReleasedPackConversionException>(() =>
            ReleasedPackConversion.Convert(Encoding.UTF8.GetBytes(unpinned), "fixture-principal"));

        Assert.Equal("configuration-release-closure-mismatch", refused.Code);
        Assert.Equal("acme.finance", refused.Target);
    }
    // Literal frozen wire-contract fixture: keys deliberately do not encode package ownership.
    private const string OwnedExposure = """
        {
          "packageKey": "tenant.release", "revision": "2.0.0",
          "closure": { "dependencies": [] },
          "exposes": ["alpha", "zeta"], "interfaceVersion": 3,
          "items": [
            { "content": { "classification": "present", "payload": {
              "definitionKey": "alpha", "packageKey": "tenant.release",
              "contentKind": "FormDefinition", "body": { "title": "Alpha" }
            } } },
            { "content": { "classification": "present", "payload": {
              "definitionKey": "zeta", "packageKey": "tenant.release",
              "contentKind": "AssetTypeDefinition", "body": { "title": "Zeta" }
            } } }
          ]
        }
        """;

    // Literal header/refusal oracles pin the existing Text contract independently of its guard.
    [Theory]
    [InlineData("packageKey", "missing", "The Released package states no packageKey.")]
    [InlineData("packageKey", "null", "The Released package states no packageKey.")]
    [InlineData("packageKey", "empty", "The Released package states no packageKey.")]
    [InlineData("packageKey", "whitespace", "The Released package states no packageKey.")]
    [InlineData("revision", "missing", "The Released package states no revision.")]
    [InlineData("revision", "null", "The Released package states no revision.")]
    [InlineData("revision", "empty", "The Released package states no revision.")]
    [InlineData("revision", "whitespace", "The Released package states no revision.")]
    public void Missing_or_blank_root_header_has_a_named_refusal(string property, string shape, string message)
    {
        var document = JsonNode.Parse(OwnedExposure)!.AsObject();
        if (shape == "missing") document.Remove(property);
        else document[property] = shape switch
        {
            "null" => null,
            "empty" => JsonValue.Create(string.Empty),
            "whitespace" => JsonValue.Create(" \t\r\n"),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };

        var refused = Assert.Throws<ReleasedPackConversionException>(() =>
            ReleasedPackConversion.Convert(Encoding.UTF8.GetBytes(document.ToJsonString()), "fixture-principal"));
        Assert.Equal("configuration-release-document-malformed", refused.Code);
        Assert.Equal(property, refused.Target);
        Assert.Equal(message, refused.Message);
    }

    [Fact]
    public void Missing_package_key_is_refused_before_missing_revision()
    {
        var document = JsonNode.Parse(OwnedExposure)!.AsObject();
        document.Remove("packageKey");
        document.Remove("revision");

        var refused = Assert.Throws<ReleasedPackConversionException>(() =>
            ReleasedPackConversion.Convert(Encoding.UTF8.GetBytes(document.ToJsonString()), "fixture-principal"));
        Assert.Equal("configuration-release-document-malformed", refused.Code);
        Assert.Equal("packageKey", refused.Target);
        Assert.Equal("The Released package states no packageKey.", refused.Message);
    }

    [Fact]
    public void Owned_exposure_is_preserved_exactly()
    {
        var converted = ReleasedPackConversion.Convert(Encoding.UTF8.GetBytes(OwnedExposure), "fixture-principal");
        var request = Assert.IsType<PackExportRequest>(converted.Request);
        Assert.Equal(new[] { "alpha", "zeta" }, request.Exposes);
        Assert.Equal(3, request.InterfaceVersion);
        Assert.Empty(converted.ForeignEdits);
        Assert.Empty(request.Dependencies);
        Assert.Collection(request.Contents,
            alpha =>
            {
                Assert.Equal(("alpha", PackContentKind.FormDefinition, "2.0.0"), (alpha.Key, alpha.Kind, alpha.Version));
                Assert.Equal("Alpha", alpha.Content["title"]!.GetValue<string>());
            },
            zeta =>
            {
                Assert.Equal(("zeta", PackContentKind.AssetTypeDefinition, "2.0.0"), (zeta.Key, zeta.Kind, zeta.Version));
                Assert.Equal("Zeta", zeta.Content["title"]!.GetValue<string>());
            });
    }

    [Fact]
    public void Provided_order_is_preserved_without_renaming_keys()
    {
        var document = OwnedExposure.Replace("[\"alpha\", \"zeta\"]", "[\"zeta\", \"alpha\"]", StringComparison.Ordinal);
        var request = ReleasedPackConversion.Convert(Encoding.UTF8.GetBytes(document), "fixture-principal").Request!;
        Assert.Equal(new[] { "zeta", "alpha" }, request.Exposes);
        Assert.Equal(3, request.InterfaceVersion);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2147483647)]
    public void Positive_int32_interface_boundaries_are_preserved(int version)
    {
        var document = JsonNode.Parse(OwnedExposure)!.AsObject();
        document["interfaceVersion"] = version;
        var request = ReleasedPackConversion.Convert(Encoding.UTF8.GetBytes(document.ToJsonString()), "fixture-principal").Request!;
        Assert.Equal(version, request.InterfaceVersion);
        Assert.Equal(new[] { "alpha", "zeta" }, request.Exposes);
    }

    [Fact]
    public void Absent_root_exposure_is_not_inferred_from_names_or_body_hints()
    {
        var document = WithoutExposure(OwnedExposure).Replace("\"title\":\"Alpha\"",
            "\"title\":\"Alpha\",\"definitionEnvelope\":{\"exposes\":{\"interface_version\":7}}", StringComparison.Ordinal);
        var request = ReleasedPackConversion.Convert(Encoding.UTF8.GetBytes(document), "fixture-principal").Request!;
        Assert.Null(request.Exposes);
        Assert.Null(request.InterfaceVersion);
        Assert.Equal(7, request.Contents[0].Content["definitionEnvelope"]!["exposes"]!["interface_version"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("\"exposes\":[\"alpha\"]", "interfaceVersion")]
    [InlineData("\"interfaceVersion\":3", "exposes")]
    [InlineData("\"exposes\":null,\"interfaceVersion\":3", "exposes")]
    [InlineData("\"exposes\":{},\"interfaceVersion\":3", "exposes")]
    [InlineData("\"exposes\":\"alpha\",\"interfaceVersion\":3", "exposes")]
    [InlineData("\"exposes\":[],\"interfaceVersion\":3", "exposes")]
    [InlineData("\"exposes\":[null],\"interfaceVersion\":3", "exposes")]
    [InlineData("\"exposes\":[1],\"interfaceVersion\":3", "exposes")]
    [InlineData("\"exposes\":[\"\"],\"interfaceVersion\":3", "exposes")]
    [InlineData("\"exposes\":[\" \"],\"interfaceVersion\":3", "exposes")]
    [InlineData("\"exposes\":[\"alpha\",\"alpha\"],\"interfaceVersion\":3", "exposes")]
    [InlineData("\"exposes\":[\"missing\"],\"interfaceVersion\":3", "exposes")]
    [InlineData("\"exposes\":[\"ALPHA\"],\"interfaceVersion\":3", "exposes")]
    [InlineData("\"exposes\":[\" alpha \"],\"interfaceVersion\":3", "exposes")]
    [InlineData("\"exposes\":null,\"interfaceVersion\":null", "exposes")]
    [InlineData("\"exposes\":[\"alpha\"],\"interfaceVersion\":null", "interfaceVersion")]
    [InlineData("\"exposes\":[\"alpha\"],\"interfaceVersion\":\"3\"", "interfaceVersion")]
    [InlineData("\"exposes\":[\"alpha\"],\"interfaceVersion\":true", "interfaceVersion")]
    [InlineData("\"exposes\":[\"alpha\"],\"interfaceVersion\":{}", "interfaceVersion")]
    [InlineData("\"exposes\":[\"alpha\"],\"interfaceVersion\":[]", "interfaceVersion")]
    [InlineData("\"exposes\":[\"alpha\"],\"interfaceVersion\":1.5", "interfaceVersion")]
    [InlineData("\"exposes\":[\"alpha\"],\"interfaceVersion\":0", "interfaceVersion")]
    [InlineData("\"exposes\":[\"alpha\"],\"interfaceVersion\":-1", "interfaceVersion")]
    [InlineData("\"exposes\":[\"alpha\"],\"interfaceVersion\":2147483648", "interfaceVersion")]
    public void Malformed_exposure_refuses_before_export(string assertion, string target)
    {
        var document = OwnedExposure.Replace("\"exposes\": [\"alpha\", \"zeta\"], \"interfaceVersion\": 3", assertion, StringComparison.Ordinal);
        var refused = Assert.Throws<ReleasedPackConversionException>(() =>
            ReleasedPackConversion.Convert(Encoding.UTF8.GetBytes(document), "fixture-principal"));
        Assert.Equal("configuration-release-document-malformed", refused.Code);
        Assert.Equal(target, refused.Target);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Exposure_and_legacy_absence_survive_signed_export_and_verification(bool exposes)
    {
        using var key = KeyPair.Generate();
        var signer = new Ed25519Signer(key);
        var codec = new PackFileCodec();
        var exporter = new PackExporter(new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()), new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            codec, TimeProvider.System);
        var request = ReleasedPackConversion.Convert(Encoding.UTF8.GetBytes(exposes ? OwnedExposure : WithoutExposure(OwnedExposure)),
            signer.IssuerId.ToBase64Url()).Request!;
        var exported = await exporter.ExportAsync(request, signer);
        Assert.True(exported.Succeeded, string.Join(",", exported.Validation.Errors.Select(error => error.Code)));
        Assert.NotNull(exported.FileBytes);
        var trust = new InMemoryPackTrustStore(
            [new PackTrustRoot(TrustScope.OwnRoster, key.PrincipalId, 1, TrustRootStatus.Current)]);
        var verifier = new PackVerifier(new Ed25519Verifier(), codec);
        var verified = verifier.Verify(exported.FileBytes, trust);
        Assert.Equal(PackVerdict.Verified, verified.Verdict);
        Assert.NotNull(verified.Manifest);
        if (exposes)
        {
            Assert.Equal(new[] { "alpha", "zeta" }, verified.Manifest.Exposes);
            Assert.Equal(3, verified.Manifest.InterfaceVersion);
        }
        else
        {
            Assert.Null(verified.Manifest.Exposes);
            Assert.Null(verified.Manifest.InterfaceVersion);
        }

        // Decode/re-encode with the real codec, retaining the original signature for both tamper controls.
        var file = codec.TryDecode(exported.FileBytes)!;
        var envelope = file.Envelope!;
        var subject = envelope.Payload;
        var changedKeys = file with { Envelope = envelope with { Payload = subject with
            { Manifest = subject.Manifest with { Exposes = ["forged"] } } } };
        var changedVersion = file with { Envelope = envelope with { Payload = subject with
            { Manifest = subject.Manifest with { InterfaceVersion = 4 } } } };
        foreach (var tampered in new[] { changedKeys, changedVersion })
        {
            var refused = verifier.Verify(codec.Encode(tampered), trust);
            Assert.Equal(PackVerdict.VerificationFailed, refused.Verdict);
            Assert.Equal(new[] { "pack.verify.signature_invalid" }, refused.Details);
            Assert.Null(refused.Manifest);
            Assert.Null(refused.Contents);
        }
    }
}
