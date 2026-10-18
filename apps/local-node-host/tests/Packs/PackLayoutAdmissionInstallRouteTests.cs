using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Blocks.BuilderDefinitions;
using Harborline.Foundation.Definitions;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed class PackLayoutAdmissionInstallRouteTests : IAsyncLifetime
{
    private static readonly TeamId Team = new(Guid.Parse("aaaa0000-0000-0000-0000-000000001034"));
    private const string BodyMarker = "private-layout-body-marker";
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private KeyPair _key = null!;
    private InMemoryPackTrustStore _trust = null!;
    private InMemoryPackInstallStore _store = null!;
    private PackInstaller _installer = null!;
    private MutableActiveTeamAccessor _activeTeam = null!;

    public Task InitializeAsync() => Task.CompletedTask;

    private async Task StartAsync(string? capabilityVersion = "1.0.0")
    {
        var builder = WebApplication.CreateBuilder();
        TestDesktopOperator.AddTestDesktopOperator(builder.Services);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _key = KeyPair.Generate();
        var codec = new PackFileCodec();
        var exporter = new PackExporter(
            new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()), codec, timeProvider: TimeProvider.System);
        var verifier = new PackVerifier(new Ed25519Verifier(), codec);
        _trust = new InMemoryPackTrustStore([
            new PackTrustRoot(TrustScope.OwnRoster, _key.PrincipalId, PackComposerRoutes.OwnRosterEpoch, TrustRootStatus.Current),
        ]);
        _activeTeam = new MutableActiveTeamAccessor(new TeamContext(
            Team, "Layout", new ServiceCollection().BuildServiceProvider(), TimeProvider.System));
        _store = new InMemoryPackInstallStore();
        _installer = new PackInstaller(verifier, _store,
            new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator()),
            new InMemoryPackInstallAudit(), TestAuthorization.AllowGate(),
            layoutCapabilities: capabilityVersion is null ? null
                : new Dictionary<string, string> { ["platform.layout"] = capabilityVersion },
            layoutRegisters: new LayoutHostRegisters(LayoutBlockKindRegistry.Platform,
                FieldControls: LayoutFieldControlRegistry.Released, ValidationRules: LayoutValidationRuleRegistry.Released));
        var registryServices = new ServiceCollection().AddLogging().AddInMemoryAssetTypeSystem().BuildServiceProvider();
        var projector = PackProjectionTestFixture.Create(_store, registryServices.GetRequiredService<IEntityTypeRegistry>());
        var authz = TestPackGate.AllowAll();
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        PackComposerRoutes.Map(_app, exporter, verifier, _trust, new Ed25519Signer(_key), _activeTeam,
            authz, TimeProvider.System, NullLogger.Instance);
        PackInstallRoutes.Map(_app, _installer, _store, _trust, PackRevocationList.Empty, _activeTeam,
            authz, TimeProvider.System, NullLogger.Instance, authorizingPrincipal: _key.PrincipalId.ToBase64Url(),
            projector: projector);
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _key?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Signed_canonical_layout_installs_with_explicit_host_inputs()
    {
        await StartAsync();
        var content = LayoutContent();
        var bytes = await ExportAsync(content);
        Assert.Equal(PackInstallVerdict.WouldInstall, _installer.Preview(bytes, Context()).Verdict);
        Assert.Equal(PackInstallVerdict.WouldInstall, _installer.Check(bytes, Context()).Verdict);
        AssertNoWrites();

        using var response = await PostBytesAsync(PackInstallRoutes.InstallRoute, bytes);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("installed").GetBoolean());
        var installed = _store.GetVersion(NodeTenant.Resolve(_activeTeam), "acme.layout-pack", "1.0.0");
        Assert.NotNull(installed);
        var seed = Assert.Single(installed.SeedItems);
        Assert.Equal("acme.layout", seed.Key);
        Assert.Equal(PackContentKind.Layout, seed.Kind);
        Assert.Equal("1.0.0", seed.Version);
        Assert.True(JsonNode.DeepEquals(content, seed.ParseContent()));
    }

    [Theory]
    [InlineData("1.0.0", "acme.unregistered", "layout.block.kind_unknown", "/blocks/0/kind")]
    [InlineData(null, "layout.text", "layout.pack.capability_unsupported", "/envelope/requires/0/minimum_platform_version")]
    [InlineData("0.9.0", "layout.text", "layout.pack.capability_unsupported", "/envelope/requires/0/minimum_platform_version")]
    public async Task Platform_refusals_survive_preview_check_and_signed_install(
        string? capabilityVersion, string kind, string code, string pointer)
    {
        await StartAsync(capabilityVersion);
        var content = LayoutContent();
        content["blocks"]![0]!["kind"] = kind;
        var bytes = await ExportAsync(content);
        var expected = new PackInstallRefusal(code, pointer, "acme.layout@1.0.0");

        var preview = _installer.Preview(bytes, Context());
        Assert.Equal(PackInstallVerdict.Refused, preview.Verdict);
        Assert.Equal(expected, Assert.Single(preview.Refusals));
        var check = _installer.Check(bytes, Context());
        Assert.Equal(PackInstallVerdict.Refused, check.Verdict);
        Assert.Equal(expected, Assert.Single(check.Refusals));
        AssertNoWrites();

        using var response = await PostBytesAsync(PackInstallRoutes.InstallRoute, bytes);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("installed").GetBoolean());
        Assert.Equal("install", body.GetProperty("stage").GetString());
        Assert.Equal(new[] { code }, body.GetProperty("refusalCodes").EnumerateArray().Select(value => value.GetString()));
        var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
        Assert.Equal(code, refusal.GetProperty("code").GetString());
        Assert.Equal(pointer, refusal.GetProperty("pointer").GetString());
        Assert.Equal("acme.layout@1.0.0", refusal.GetProperty("target").GetString());
        AssertNoWrites();
    }

    [Fact]
    public async Task Check_collects_distinct_layout_failures_without_repeating_the_shared_window_refusal()
    {
        await StartAsync("0.9.0");
        var content = LayoutContent();
        content["envelope"]!["contract"]!["major"] = 2;
        content["blocks"]![0]!["kind"] = "acme.unregistered";
        var bytes = await ExportAsync(content);

        var check = _installer.Check(bytes, Context());
        Assert.Equal(PackInstallVerdict.Refused, check.Verdict);
        Assert.Equal(new[]
        {
            new PackInstallRefusal("definition.contract.out_of_window", "/envelope/contract", "acme.layout@1.0.0"),
            new PackInstallRefusal("layout.block.kind_unknown", "/blocks/0/kind", "acme.layout@1.0.0"),
            new PackInstallRefusal("layout.pack.capability_unsupported", "/envelope/requires/0/minimum_platform_version", "acme.layout@1.0.0"),
        }, check.Refusals);
        AssertNoWrites();
    }

    [Theory]
    [InlineData("syntax", "1.0.0")]
    [InlineData("syntax", null)]
    [InlineData("syntax", "0.9.0")]
    [InlineData("strict", "1.0.0")]
    [InlineData("strict", null)]
    [InlineData("strict", "0.9.0")]
    [InlineData("null", "1.0.0")]
    [InlineData("null", null)]
    [InlineData("null", "0.9.0")]
    public async Task Undecodable_layout_refuses_without_capability_noise_or_body_details(
        string failure, string? capabilityVersion)
    {
        await StartAsync(capabilityVersion);
        var content = LayoutContent();
        content[BodyMarker] = true;
        var rawBody = failure switch
        {
            "syntax" => "{\"private-layout-body-marker\":",
            "strict" => content.ToJsonString(),
            _ => "null",
        };
        var bytes = await ReplaceBodiesAsync(await ExportAsync(LayoutContent()), ("acme.layout", rawBody));

        await AssertRefusedAsync(bytes,
            new PackInstallRefusal("layout.definition.body_invalid", "", "acme.layout@1.0.0"));
        AssertNoWrites();
    }

    [Fact]
    public async Task Undecodable_entries_do_not_hide_later_layout_semantics_or_allow_partial_install()
    {
        await StartAsync();
        var strict = LayoutContent("acme.strict");
        strict[BodyMarker] = true;
        var semantic = LayoutContent("acme.semantic");
        semantic["blocks"]![0]!["kind"] = "acme.unregistered";
        var bytes = await ExportAsync([
            ("acme.syntax", LayoutContent("acme.syntax")),
            ("acme.strict", LayoutContent("acme.strict")),
            ("acme.valid", LayoutContent("acme.valid")),
            ("acme.semantic", semantic),
        ]);
        bytes = await ReplaceBodiesAsync(bytes,
            ("acme.syntax", "{\"private-layout-body-marker\":"),
            ("acme.strict", strict.ToJsonString()));

        await AssertRefusedAsync(bytes,
            new PackInstallRefusal("layout.definition.body_invalid", "", "acme.syntax@1.0.0"),
            new PackInstallRefusal("layout.definition.body_invalid", "", "acme.strict@1.0.0"),
            new PackInstallRefusal("layout.block.kind_unknown", "/blocks/0/kind", "acme.semantic@1.0.0"));
        AssertNoWrites();
    }

    [Fact]
    public async Task Check_keeps_shared_contract_diagnostics_when_a_body_is_invalid()
    {
        await StartAsync();
        var strict = LayoutContent("acme.strict");
        strict["envelope"]!["contract"]!["major"] = 2;
        strict[BodyMarker] = true;
        var semantic = LayoutContent("acme.semantic");
        semantic["envelope"]!["contract"]!["major"] = 2;
        semantic["blocks"]![0]!["kind"] = "acme.unregistered";
        var bytes = await ExportAsync([
            ("acme.strict", LayoutContent("acme.strict")),
            ("acme.semantic", semantic),
        ]);
        bytes = await ReplaceBodiesAsync(bytes, ("acme.strict", strict.ToJsonString()));

        AssertRefusals(_installer.Check(bytes, Context()),
            new PackInstallRefusal("definition.contract.out_of_window", "/envelope/contract", "acme.strict@1.0.0"),
            new PackInstallRefusal("definition.contract.out_of_window", "/envelope/contract", "acme.semantic@1.0.0"),
            new PackInstallRefusal("layout.definition.body_invalid", "", "acme.strict@1.0.0"),
            new PackInstallRefusal("layout.block.kind_unknown", "/blocks/0/kind", "acme.semantic@1.0.0"));
        AssertNoWrites();
    }

    [Theory]
    [InlineData("{\"private-layout-body-marker\":")]
    [InlineData("null")]
    public async Task Undecodable_upgrade_with_prior_override_refuses_before_reattachment(string rawBody)
    {
        await StartAsync();
        using var installedResponse = await PostBytesAsync(PackInstallRoutes.InstallRoute, await ExportAsync(LayoutContent()));
        Assert.Equal(HttpStatusCode.OK, installedResponse.StatusCode);
        var tenant = NodeTenant.Resolve(_activeTeam);
        _store.SaveOverride(tenant, "acme.layout-pack",
            new PackTenantOverride("acme.layout", new JsonObject { ["intent"] = "Observe" }));
        Assert.Single(_store.GetOverrides(tenant, "acme.layout-pack"));
        var installedBefore = JsonSerializer.Serialize(_store.ListInstalled(tenant));
        var overridesBefore = JsonSerializer.Serialize(_store.GetOverrides(tenant, "acme.layout-pack"));
        var watermarkBefore = JsonSerializer.Serialize(_store.GetWatermark(tenant, "acme.layout-pack"));
        var bytes = await ExportAsync([("acme.layout", LayoutContent("acme.layout", "2.0.0"))], "2.0.0");
        bytes = await ReplaceBodiesAsync(bytes, ("acme.layout", rawBody));

        await AssertRefusedAsync(bytes,
            new PackInstallRefusal("layout.definition.body_invalid", "", "acme.layout@2.0.0"));
        Assert.Equal(installedBefore, JsonSerializer.Serialize(_store.ListInstalled(tenant)));
        Assert.Equal(overridesBefore, JsonSerializer.Serialize(_store.GetOverrides(tenant, "acme.layout-pack")));
        Assert.Equal(watermarkBefore, JsonSerializer.Serialize(_store.GetWatermark(tenant, "acme.layout-pack")));
        Assert.Null(_store.GetVersion(tenant, "acme.layout-pack", "2.0.0"));
    }

    private async Task AssertRefusedAsync(byte[] bytes, params PackInstallRefusal[] expected)
    {
        AssertRefusals(_installer.Preview(bytes, Context()), expected);
        AssertRefusals(_installer.Check(bytes, Context()), expected);
        foreach (var route in new[] { PackInstallRoutes.PreviewRoute, PackInstallRoutes.CheckRoute, PackInstallRoutes.InstallRoute })
        {
            using var response = await PostBytesAsync(route, bytes);
            Assert.Equal(route == PackInstallRoutes.InstallRoute
                ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadAsStringAsync();
            AssertNoBodyDetails(json);
            using var document = JsonDocument.Parse(json);
            var body = document.RootElement;
            if (route == PackInstallRoutes.InstallRoute)
            {
                Assert.False(body.GetProperty("installed").GetBoolean());
                Assert.Equal("install", body.GetProperty("stage").GetString());
                AssertDtoRefusals(body.GetProperty("preview"), expected);
            }

            AssertDtoRefusals(body, expected);
        }
    }

    private static void AssertRefusals(PackInstallPreview preview, params PackInstallRefusal[] expected)
    {
        Assert.Equal(PackInstallVerdict.Refused, preview.Verdict);
        Assert.Equal(expected.Length, preview.Refusals.Count);
        foreach (var refusal in expected)
            Assert.Contains(refusal, preview.Refusals);
        Assert.Equal(expected.Select(refusal => refusal.Code).Distinct().Count(), preview.RefusalCodes.Count);
        foreach (var code in expected.Select(refusal => refusal.Code))
            Assert.Contains(code, preview.RefusalCodes);
        AssertNoBodyDetails(JsonSerializer.Serialize(preview));
    }

    private static void AssertDtoRefusals(JsonElement body, PackInstallRefusal[] expected)
    {
        var actual = body.GetProperty("refusals").EnumerateArray().Select(refusal => new PackInstallRefusal(
            refusal.GetProperty("code").GetString()!, refusal.GetProperty("pointer").GetString()!,
            refusal.GetProperty("target").GetString())).ToArray();
        Assert.Equal(expected.Length, actual.Length);
        foreach (var refusal in expected)
            Assert.Contains(refusal, actual);
        var codes = body.GetProperty("refusalCodes").EnumerateArray().Select(code => code.GetString()).ToArray();
        Assert.Equal(expected.Select(refusal => refusal.Code).Distinct().Count(), codes.Length);
        foreach (var code in expected.Select(refusal => refusal.Code))
            Assert.Contains(code, codes);
    }

    private static void AssertNoBodyDetails(string json)
    {
        Assert.DoesNotContain(BodyMarker, json, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonException", json, StringComparison.Ordinal);
        Assert.DoesNotContain("BytePositionInLine", json, StringComparison.Ordinal);
        Assert.DoesNotContain("LineNumber", json, StringComparison.Ordinal);
    }

    private void AssertNoWrites()
    {
        var tenant = NodeTenant.Resolve(_activeTeam);
        Assert.Empty(_store.ListInstalled(tenant));
        Assert.Null(_store.GetWatermark(tenant, "acme.layout-pack"));
        Assert.Empty(_store.GetOverrides(tenant, "acme.layout-pack"));
    }

    private PackInstallContext Context() => new(NodeTenant.Resolve(_activeTeam), _trust, PackRevocationList.Empty,
        AdmittedInstant.FromRecordedAct(DateTimeOffset.UtcNow), TimeSpan.FromDays(30));

    private static JsonObject LayoutContent(string key = "acme.layout", string version = "1.0.0")
        => JsonNode.Parse(LayoutDefinitionJson.SerializeCanonical(new LayoutDefinition(
        new LayoutDefinitionEnvelope(key, version, "layout-tenant", LayoutCascadeLayer.TenantConfiguration,
            JsonSerializer.SerializeToElement(new { source = "signed-route" }), "standard", false,
            [new LayoutDefinitionRequirement("platform.layout", "1.0.0")], new DefinitionContractVersion(1, 0)),
        1, LayoutMedium.Screen, LayoutIntent.Observe,
        [new LayoutBlock("title", "layout.text", new LayoutStaticBinding(JsonSerializer.SerializeToElement("Layout")), [])],
        [], [], [], null, [])))!.AsObject();

    private Task<byte[]> ExportAsync(JsonObject content) => ExportAsync([("acme.layout", content)]);

    private async Task<byte[]> ExportAsync(IReadOnlyList<(string Key, JsonObject Content)> entries, string version = "1.0.0")
    {
        using var response = await _client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, new
        {
            key = "acme.layout-pack", version, name = "Layout", description = "Layout admission", scopeTier = "Vertical",
            contents = entries.Select(entry => new { key = entry.Key, kind = "Layout", version, content = entry.Content }).ToArray(),
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private async Task<byte[]> ReplaceBodiesAsync(byte[] bytes, params (string Key, string Json)[] replacements)
    {
        var codec = new PackFileCodec();
        var file = Assert.IsType<PackFile>(codec.TryDecode(bytes));
        var envelope = Assert.IsType<SignedOperation<PackSignatureSubject>>(file.Envelope);
        var replacementBytes = replacements.ToDictionary(entry => entry.Key,
            entry => Encoding.UTF8.GetBytes(entry.Json), StringComparer.Ordinal);
        var contents = file.Contents.Select(payload => replacementBytes.TryGetValue(payload.Key, out var raw)
            ? payload with { ContentBase64 = Convert.ToBase64String(raw) } : payload).ToList();
        var manifest = envelope.Payload.Manifest with
        {
            Contents = envelope.Payload.Manifest.Contents.Select(reference => replacementBytes.TryGetValue(reference.Key, out var raw)
                ? reference with { ContentAddress = Cid.FromBytes(raw) } : reference).ToList(),
        };
        var signature = await new Ed25519Signer(_key).SignAsync(
            envelope.Payload with { Manifest = manifest }, envelope.IssuedAt, Guid.NewGuid());
        var result = codec.Encode(file with { Envelope = signature, Contents = contents });
        Assert.Equal(PackVerdict.Verified, new PackVerifier(new Ed25519Verifier(), codec).Verify(result, _trust).Verdict);
        return result;
    }

    private Task<HttpResponseMessage> PostBytesAsync(string route, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return _client.PostAsync(route, content);
    }

    private sealed class MutableActiveTeamAccessor(TeamContext? active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; set; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void Keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
