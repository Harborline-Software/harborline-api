using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Data.PackProjection;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>Signs only the pinned predecessor recipes and the named existing admission probes.</summary>
internal static class LegacyPackFixtureSigner
{
    internal static async Task<byte[]> SignPlatformSnapshotAsync(byte[] recipe, IOperationSigner signer)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(recipe));
        var (version, unqualifiedCount) = digest switch
        {
            "ef1308048dfc3553a912594ca3e9133222905581b64a315d709a743a3f24e1c4" => ("1.3.0", 16),
            "7717911ff08ed71c8b51d2f2a59a633d147eef5934ffde0c6a5e8de22c456919" => ("1.6.0", 19),
            _ => throw new ArgumentException("Only the two exact released platform recipes are supported.", nameof(recipe)),
        };
        var request = ReadRequest(recipe, signer.IssuerId.ToBase64Url());
        Assert.Equal("harborline.platform", request.Key);
        Assert.Equal(version, request.Version);
        Assert.Empty(request.Dependencies);
        return await SignAsync(request, signer, unqualifiedCount);
    }

    internal static async Task<byte[]> SignNameProbeAsync(object body, IOperationSigner signer)
    {
        var request = ReadRequest(JsonSerializer.SerializeToUtf8Bytes(body), signer.IssuerId.ToBase64Url());
        Assert.Equal("harborline.general", request.Key);
        Assert.Equal("1.0.0", request.Version);
        Assert.Equal("General forms", request.Name);
        Assert.Equal("Pack-projected form proof.", request.Description);
        Assert.Equal(PackScopeTier.Horizontal, request.ScopeTier);
        Assert.Empty(request.Dependencies);
        Assert.Empty(request.CapabilityRequirements);
        var item = Assert.Single(request.Contents);
        Assert.Contains(item.Key, new[] { "fOrMdEfInItIoN", "FormDefinitionTemplate" });
        Assert.Equal(PackContentKind.FormDefinition, item.Kind);
        Assert.Equal("1.0.0", item.Version);
        Assert.Empty(item.Content["overlay"]!["rules"]!.AsArray());
        return await SignAsync(request, signer, 1);
    }

    internal static async Task<byte[]> SignCompiledShapeProbeAsync(object body, IOperationSigner signer)
    {
        var request = ReadRequest(JsonSerializer.SerializeToUtf8Bytes(body), signer.IssuerId.ToBase64Url());
        Assert.Equal("1.0.0", request.Version);
        Assert.Equal(PackScopeTier.Vertical, request.ScopeTier);
        Assert.Empty(request.Dependencies);
        Assert.Null(request.ProviderSlot);
        Assert.Null(request.Exposes);
        Assert.Null(request.InterfaceVersion);
        Assert.Equal(2, request.Contents.Count);
        var first = request.Contents[0];
        switch (request.Key)
        {
            case "acme.bootstrap":
                Assert.Equal("Bootstrap claim pack", request.Name);
                Assert.Equal("a record type keyed as a compiled bootstrap shape", request.Description);
                Assert.Equal("fixtures.intake", first.Key);
                Assert.Equal(PackContentKind.FormDefinition, first.Kind);
                Assert.True(JsonNode.DeepEquals(
                    JsonSerializer.SerializeToNode(PackProjectionTestFixture.FormContent("Intake")), first.Content));
                Assert.Equal(new[] { "forms.dynamic" }, request.CapabilityRequirements);
                break;
            case "acme.bootstrap-standards":
                Assert.Equal("Bootstrap claim after standards", request.Name);
                Assert.Equal("a standards catalog, then a record type keyed as a compiled bootstrap shape", request.Description);
                Assert.Equal("fixtures.standards", first.Key);
                Assert.Equal(PackContentKind.StandardsCatalog, first.Kind);
                Assert.True(JsonNode.DeepEquals(new JsonObject(), first.Content));
                Assert.Empty(request.CapabilityRequirements);
                break;
            default:
                throw new ArgumentException("Only the two existing compiled-shape route recipes are supported.", nameof(body));
        }
        Assert.Equal("1.0.0", first.Version);
        AssertCompiledShapeItem(request.Contents[1], "Record-Type");
        return await SignAsync(request, signer, 1);
    }

    internal static async Task<byte[]> SignPlatformCompiledShapeProbeAsync(PackExportRequest request, IOperationSigner signer)
    {
        // T-724 ruling 121: transport header literals pin the released 2.0.0 corpus; the
        // independent producer document below is never passed through the api converter for expectations.
        Assert.Equal("harborline.platform", request.Key);
        Assert.Equal("2.0.0", request.Version);
        Assert.Equal("harborline.platform", request.Name);
        Assert.Equal("Released configuration pack 19054f26beca1b9e3a9cbc7d395c9301d2b91781d8461c216529b4fc21d616e1.", request.Description);
        Assert.Equal(PackScopeTier.Vertical, request.ScopeTier);
        Assert.Equal(1L, request.Epoch);
        Assert.Null(request.RenamedFrom);
        Assert.Null(request.DisplayName);
        Assert.Null(request.Tagline);
        Assert.Null(request.Category);
        Assert.Null(request.IconRef);
        Assert.Null(request.ExposedDefinitions);
        Assert.Empty(request.Dependencies);
        Assert.Equal(["forms.catalogue-field-source"], request.CapabilityRequirements);
        Assert.Null(request.ProviderSlot);
        Assert.Null(request.Exposes);
        Assert.Null(request.InterfaceVersion);
        Assert.Equal(49, request.Contents.Count);

        using var document = JsonDocument.Parse(PlatformSeedTestBundle.Export());
        var declarations = document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("content").GetProperty("payload"))
            .Where(payload => payload.TryGetProperty("definitionKey", out _)).ToArray();
        Assert.Equal(48, declarations.Length);
        for (var index = 0; index < declarations.Length; index++)
        {
            var declaration = declarations[index];
            var actual = request.Contents[index];
            Assert.Equal(declaration.GetProperty("definitionKey").GetString(), actual.Key);
            Assert.Equal(declaration.GetProperty("contentKind").GetString(), actual.Kind.ToString());
            Assert.Equal(declaration.GetProperty("contentKind").GetString() == "ViewDefinition" ? "1.0.0" : "2.0.0", actual.Version);
            var expectedBody = declaration.GetProperty("definitionKey").GetString() == "platform.workshop"
                ? ExpectedWorkshop() : JsonNode.Parse(declaration.GetProperty("body").GetRawText());
            Assert.True(JsonNode.DeepEquals(expectedBody, actual.Content), actual.Key);
        }
        AssertCompiledShapeItem(request.Contents[^1], "record-type");
        return await SignAsync(request, signer, 1);
    }

    private static JsonNode? ExpectedWorkshop()
    {
        // T-584/seed-released-navigation:58-59: package capability and the resolved workshop shape.
        string[] pillars = ["asset-types", "forms", "workflows", "standards", "defaults", "terminology", "documents",
            "taxonomies", "reports", "data-exchanges", "standing-rules", "schedules", "views"];
        return JsonNode.Parse($$"""
            {"seedWorkspaces":[{"id":"workshop","labelKey":"workshop.workspace","groups":[{"id":"definitions",
            "labelKey":"workshop.definitions","itemIds":{{JsonSerializer.Serialize(pillars)}},
            "items":{{JsonSerializer.Serialize(pillars.Select(id => new { id, labelKey = $"workshop.{id}" }))}}}]}],
            "panelSet":[{"id":"inspector","binding":"panels.inspector.toggle","shortcut":"mod+shift+i",
            "defaultWidth":360,"minimumHeight":180,"defaultOpen":false}]}
            """);
    }

    private static void AssertCompiledShapeItem(PackContentSource item, string key)
    {
        Assert.Equal(key, item.Key);
        Assert.Equal(PackContentKind.RecordType, item.Kind);
        Assert.Equal("1.0.0", item.Version);
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["sealed"] = true }, item.Content));
    }

    private static PackExportRequest ReadRequest(byte[] bytes, string principal)
    {
        var document = JsonSerializer.Deserialize<ExportPackRequestDto>(bytes,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(document.Dcp);
        return new PackExportRequest(document.Key, document.Version, document.Name ?? document.Key,
            document.Description ?? string.Empty, Enum.Parse<PackScopeTier>(document.ScopeTier, true),
            (document.Contents ?? []).Select(item => new PackContentSource(item.Key,
                Enum.Parse<PackContentKind>(item.Kind, true), item.Version,
                JsonNode.Parse(item.Content!.Value.GetRawText())!)).ToArray(),
            (document.Dependencies ?? []).Select(item => new PackDependencyRef(
                item.Key, item.Version, item.DeclaredDependencyKeys ?? [])).ToArray(),
            document.CapabilityRequirements ?? [], PackComposerRoutes.OwnRosterEpoch,
            ProviderSlot: document.ProviderSlot, Dcp: DomainComplianceProfile.General(principal),
            Exposes: document.Exposes, InterfaceVersion: document.InterfaceVersion);
    }

    private static async Task<byte[]> SignAsync(PackExportRequest request, IOperationSigner signer,
        int expectedUnqualifiedCount)
    {
        var canonicalizer = new PackContentCanonicalizer();
        var items = request.Contents.Select(canonicalizer.Canonicalize).ToArray();
        var dcp = new PackDcpCanonicalizer().Canonicalize(request.Dcp!);
        Assert.Empty(new DcpValidator(DcpCounselRegister.FromEmbeddedResource()).Validate(request.Dcp));
        var references = PackContentReferenceDeriver.Derive(request.Key, request.Contents,
            request.Dependencies.Select(item => item.Key), out var undeclared);
        Assert.Empty(undeclared);
        var manifest = new PackManifest(request.Key, request.Version, request.Name, request.Description,
            request.ScopeTier,
            items.Select(item => new PackContentRef(item.Key, item.Kind, item.Version, item.ContentAddress)).ToArray(),
            request.Dependencies, request.CapabilityRequirements, request.RenamedFrom, request.ProviderSlot,
            new PackDcpRef(dcp.ContentAddress, dcp.Dcp.RegulatoryClass, dcp.Dcp.DcpVersion), references,
            request.DisplayName, request.Tagline, request.Category, request.IconRef,
            request.Exposes, request.InterfaceVersion, request.ExposedDefinitions);
        var validation = new PackValidator(new PackContentPiiScanner()).Validate(manifest, items);
        Assert.False(validation.IsValid);
        Assert.Equal(expectedUnqualifiedCount, validation.Errors.Count);
        Assert.All(validation.Errors, error => Assert.Equal("pack.validation.content.key.unqualified", error.Code));

        var issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds());
        var envelope = await signer.SignAsync(new PackSignatureSubject(manifest, request.Epoch), issuedAt, Guid.NewGuid());
        var file = new PackFile(envelope, items.Select(item => new PackContentPayload(item.Key, item.Kind,
            item.Version, Convert.ToBase64String(item.CanonicalBytes.Span))).ToArray(),
            new PackDcpPayload(Convert.ToBase64String(dcp.CanonicalBytes.Span)));
        return new PackFileCodec().Encode(file);
    }
}
