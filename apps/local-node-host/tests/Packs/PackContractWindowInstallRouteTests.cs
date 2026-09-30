using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

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
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Blocks.BuilderDefinitions;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// T-572 slice 4 (DES-0029 kernel-core-ck-8, DES-0014 C10): <c>POST /packs/install</c> refuses a Layout,
/// Resource or Bookable item whose declared envelope contract is outside the platform seed's window with
/// 422, in the C3 shape at stage <c>install</c>, and reports the window in DES-0006 §1's <c>contract</c>
/// shape. The seed window is app contract 1.0 over majors [1, 1] (T-572 owner ruling Q5), so the cases
/// below are both window edges: major 0 is below the oldest major, 1.0 is the newest admitted
/// declaration, 1.1 is a newer minor (T-724 ruling 86) and 2.0 a newer major.
/// </summary>
public sealed class PackContractWindowInstallRouteTests : IAsyncLifetime
{
    private const string OutOfWindow = "definition.contract.out_of_window";
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-0000000005c4"));

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private KeyPair _key = null!;
    private InMemoryPackInstallStore _store = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        Harborline.Api.LocalNodeHost.Tests.Authorization.TestDesktopOperator.AddTestDesktopOperator(builder.Services);
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
        var trustStore = new InMemoryPackTrustStore(new[]
        {
            new PackTrustRoot(TrustScope.OwnRoster, _key.PrincipalId, PackComposerRoutes.OwnRosterEpoch, TrustRootStatus.Current),
        });
        var activeTeam = new MutableActiveTeamAccessor(new TeamContext(
            TeamA, "Team A", new ServiceCollection().BuildServiceProvider(), TimeProvider.System));

        _store = new InMemoryPackInstallStore();
        var installer = new PackInstaller(
            verifier,
            _store,
            new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator()),
            new InMemoryPackInstallAudit(),
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate());
        var registryServices = new ServiceCollection().AddLogging().AddInMemoryAssetTypeSystem().BuildServiceProvider();
        var projector = PackProjectionTestFixture.Create(_store, registryServices.GetRequiredService<IEntityTypeRegistry>());
        var authz = TestPackGate.AllowAll();
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        PackComposerRoutes.Map(_app, exporter, verifier, trustStore, new Ed25519Signer(_key), activeTeam, authz,
            TimeProvider.System, NullLogger.Instance);
        PackInstallRoutes.Map(_app, installer, _store, trustStore, PackRevocationList.Empty, activeTeam,
            authz, TimeProvider.System, NullLogger.Instance, authorizingPrincipal: _key.PrincipalId.ToBase64Url(),
            projector: projector);

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _key?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Theory(DisplayName = "T-572 S4: install refuses a platform-kind item whose contract is outside the window with 422 at stage install")]
    [Trait("Holds", "kernel-core-ck-8")]
    [InlineData("Layout", 2, 0)]
    [InlineData("Layout", 1, 1)]
    [InlineData("Layout", 0, 0)]
    [InlineData("Layout", 0, 9)]
    [InlineData("Resource", 2, 0)]
    [InlineData("Bookable", 1, 1)]
    public async Task Install_refuses_an_item_whose_contract_is_outside_the_window(string kind, int major, int minor)
    {
        using var response = await PostBytesAsync(PackInstallRoutes.InstallRoute,
            await ExportAsync(PackBody(kind, new { major, minor })));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("installed").GetBoolean());
        Assert.Equal("install", body.GetProperty("stage").GetString());
        Assert.Equal([OutOfWindow], body.GetProperty("refusalCodes").EnumerateArray().Select(code => code.GetString()));
        var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
        Assert.Equal(OutOfWindow, refusal.GetProperty("code").GetString());
        Assert.Equal("/envelope/contract", refusal.GetProperty("pointer").GetString());
        Assert.Equal("acme.surface@1.0.0", refusal.GetProperty("target").GetString());
        AssertReportsTheSeedWindow(body);
        Assert.Empty(_store.ListInstalled(NodeTenantFor()));
    }

    [Theory(DisplayName = "T-572 S4: install admits a platform-kind item declaring the current app contract, the window's newest edge")]
    [Trait("Holds", "kernel-core-ck-8")]
    [InlineData("Layout")]
    [InlineData("Resource")]
    [InlineData("Bookable")]
    public async Task Install_admits_an_item_declaring_the_current_app_contract(string kind)
    {
        var window = PlatformPackageSeed.ContractWindow;

        using var response = await PostBytesAsync(PackInstallRoutes.InstallRoute,
            await ExportAsync(PackBody(kind, new { major = window.Major, minor = window.Minor })));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_store.ListInstalled(NodeTenantFor()), pack => pack.PackKey == "acme.contract");
    }

    [Fact(DisplayName = "T-572 S4: check collects the contract refusal with the pack's other refusals and installs nothing")]
    [Trait("Holds", "kernel-core-ck-8")]
    public async Task Check_collects_the_contract_refusal()
    {
        // Check is exposed once something is installed.
        var window = PlatformPackageSeed.ContractWindow;
        using (var seed = await PostBytesAsync(PackInstallRoutes.InstallRoute, await ExportAsync(
            PackBody("Layout", new { major = window.Major, minor = window.Minor }, key: "acme.seed"))))
        {
            Assert.Equal(HttpStatusCode.OK, seed.StatusCode);
        }

        using var response = await PostBytesAsync(PackInstallRoutes.CheckRoute,
            await ExportAsync(PackBody("Layout", new { major = window.Major + 1, minor = 0 })));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Refused", body.GetProperty("verdict").GetString());
        var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
        Assert.Equal(OutOfWindow, refusal.GetProperty("code").GetString());
        Assert.Equal("acme.surface@1.0.0", refusal.GetProperty("target").GetString());
        Assert.DoesNotContain(_store.ListInstalled(NodeTenantFor()), pack => pack.PackKey == "acme.contract");
    }

    private static void AssertReportsTheSeedWindow(JsonElement body)
    {
        var window = PlatformPackageSeed.ContractWindow;
        var contract = body.GetProperty("contract");
        Assert.Equal(window.Major, contract.GetProperty("app_major").GetInt32());
        Assert.Equal(window.Minor, contract.GetProperty("app_minor").GetInt32());
        Assert.Equal([window.OldestMajor, window.Major],
            contract.GetProperty("window").EnumerateArray().Select(edge => edge.GetInt32()));
    }

    private async Task<byte[]> ExportAsync(object body)
    {
        using var response = await _client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private Task<HttpResponseMessage> PostBytesAsync(string route, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return _client.PostAsync(route, content);
    }

    private static object PackBody(string kind, object contract, string key = "acme.contract") => new
    {
        key,
        version = "1.0.0",
        name = "Acme Contract",
        description = "contract window pack",
        scopeTier = "Vertical",
        contents = new[]
        {
            new
            {
                key = "acme.surface",
                kind,
                version = "1.0.0",
                content = (object)new { envelope = new { contract } },
            },
        },
    };

    private static Harborline.Foundation.Assets.Common.TenantId NodeTenantFor()
        => Harborline.Api.LocalNodeHost.Data.Financial.NodeTenant.Resolve(new MutableActiveTeamAccessor(new TeamContext(
            TeamA, "Team A", new ServiceCollection().BuildServiceProvider(), TimeProvider.System)));

    private sealed class MutableActiveTeamAccessor(TeamContext? active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; set; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void Keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
