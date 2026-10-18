using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

    private void AssertNoWrites()
    {
        var tenant = NodeTenant.Resolve(_activeTeam);
        Assert.Empty(_store.ListInstalled(tenant));
        Assert.Null(_store.GetWatermark(tenant, "acme.layout-pack"));
        Assert.Empty(_store.GetOverrides(tenant, "acme.layout-pack"));
    }

    private PackInstallContext Context() => new(NodeTenant.Resolve(_activeTeam), _trust, PackRevocationList.Empty,
        AdmittedInstant.FromRecordedAct(DateTimeOffset.UtcNow), TimeSpan.FromDays(30));

    private static JsonObject LayoutContent() => JsonNode.Parse(LayoutDefinitionJson.SerializeCanonical(new LayoutDefinition(
        new LayoutDefinitionEnvelope("acme.layout", "1.0.0", "layout-tenant", LayoutCascadeLayer.TenantConfiguration,
            JsonSerializer.SerializeToElement(new { source = "signed-route" }), "standard", false,
            [new LayoutDefinitionRequirement("platform.layout", "1.0.0")], new DefinitionContractVersion(1, 0)),
        1, LayoutMedium.Screen, LayoutIntent.Observe,
        [new LayoutBlock("title", "layout.text", new LayoutStaticBinding(JsonSerializer.SerializeToElement("Layout")), [])],
        [], [], [], null, [])))!.AsObject();

    private async Task<byte[]> ExportAsync(JsonObject content)
    {
        using var response = await _client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, new
        {
            key = "acme.layout-pack", version = "1.0.0", name = "Layout", description = "Layout admission", scopeTier = "Vertical",
            contents = new[] { new { key = "acme.layout", kind = "Layout", version = "1.0.0", content } },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync();
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
