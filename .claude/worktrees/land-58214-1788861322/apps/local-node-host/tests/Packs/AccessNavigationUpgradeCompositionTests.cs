using System.Text.Json;
using System.Text.Json.Nodes;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed class AccessNavigationUpgradeCompositionTests
{
    private static readonly TenantId Tenant = new("29400000-0000-4000-8000-000000000001");
    private const string PackKey = AccessAdministrationPreloadHostedService.PackKey;

    [Fact]
    public async Task Ordinary_upgrade_serves_exact_Access_declaration_across_restart_and_retraction_removes_it()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ticket329-navigation-{Guid.NewGuid():N}");
        try
        {
            await using (var host = await UnattributedGrantCompositionTests.OpenAsync(directory))
            {
                var services = host.Services;
                await services.GetRequiredService<AccessGrantAuthorizationSeed>().InstallAsync(
                    Tenant, services.GetRequiredService<TimeProvider>().GetUtcNow(), AuthorizationSeedProfile.Production);
                await InstallPreviousVersionAsync(services);
                using (var before = await NavigationAsync(services)) Assert.False(before.RootElement.GetProperty("configured").GetBoolean());
                await services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<AccessAdministrationPreloadHostedService>().Single().PreloadAsync(Tenant, CancellationToken.None);
                await AssertNavigationAsync(services);
                var store = services.GetRequiredService<IPackInstallStore>();
                Assert.Equal("1.1.0", store.GetActive(Tenant, PackKey)!.Version);
                Assert.Equal(PackLifecycleState.Superseded, store.GetVersion(Tenant, PackKey, "1.0.0")!.Lifecycle);
            }
            await using (var restarted = await UnattributedGrantCompositionTests.OpenAsync(directory))
            {
                await AssertNavigationAsync(restarted.Services);
                var outcome = restarted.Services.GetRequiredService<IPackInstaller>().Deactivate(
                    Context(restarted.Services), PackKey, "1.1.0");
                Assert.True(outcome.Deactivated);
                using var after = await NavigationAsync(restarted.Services);
                Assert.False(after.RootElement.GetProperty("configured").GetBoolean());
                Assert.Equal(JsonValueKind.Null, after.RootElement.GetProperty("pack").ValueKind);
            }
            await using (var restarted = await UnattributedGrantCompositionTests.OpenAsync(directory))
            {
                using var after = await NavigationAsync(restarted.Services);
                Assert.False(after.RootElement.GetProperty("configured").GetBoolean());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static async Task InstallPreviousVersionAsync(IServiceProvider services)
    {
        using var stream = typeof(AccessAdministrationPreloadHostedService).Assembly.GetManifestResourceStream(
            "Harborline.Api.LocalNodeHost.Packs.access-administration-pack.export.json")!;
        using var document = JsonDocument.Parse(stream);
        var contents = document.RootElement.GetProperty("contents").EnumerateArray()
            .Where(item => item.GetProperty("kind").GetString() != "NavWorkspaceConfig")
            .Select(item => new PackContentSource(item.GetProperty("key").GetString()!,
                Enum.Parse<PackContentKind>(item.GetProperty("kind").GetString()!),
                item.GetProperty("version").GetString()!, JsonNode.Parse(item.GetProperty("content").GetRawText())!)).ToArray();
        var signer = services.GetRequiredService<NodePrincipalSigner>();
        var export = await services.GetRequiredService<IPackExporter>().ExportAsync(new PackExportRequest(
            PackKey, "1.0.0", "Access administration", "Previous Access package", PackScopeTier.Horizontal,
            contents, [], [], PackComposerRoutes.OwnRosterEpoch,
            Dcp: DomainComplianceProfile.General(signer.Signer.IssuerId.ToBase64Url())), signer.Signer);
        Assert.True(export.Succeeded);
        var installer = services.GetRequiredService<IPackInstaller>();
        var installed = installer.Install(export.FileBytes!, Context(services));
        Assert.True(installed.Installed, string.Join(",", installed.RefusalCodes));
        var activation = installer.Activate(Context(services), PackKey, "1.0.0");
        Assert.True(activation.Activated, activation.Detail);
        Assert.Empty(Assert.IsType<PackSeedProjectionSummary>(activation.ProjectionResult).Refusals);
    }

    private static PackInstallContext Context(IServiceProvider services) => new(
        Tenant, services.GetRequiredService<IPackTrustStore>(), services.GetRequiredService<IPackRevocationList>(),
        services.GetRequiredService<TimeProvider>().GetUtcNow(), PackInstallRoutes.RevocationMaxAge,
        Principal: AccessGrantAuthorizationSeed.NodeOperatorPrincipal);

    private static async Task AssertNavigationAsync(IServiceProvider services)
    {
        using var response = await NavigationAsync(services);
        Assert.True(response.RootElement.GetProperty("configured").GetBoolean());
        var pack = response.RootElement.GetProperty("pack");
        Assert.Equal("harborline.active-pack-composition", pack.GetProperty("packId").GetString());
        var workspace = Assert.Single(pack.GetProperty("seedWorkspaces").EnumerateArray());
        Assert.Equal("access", workspace.GetProperty("id").GetString());
        Assert.Equal("access.workspace", workspace.GetProperty("labelKey").GetString());
        var group = Assert.Single(workspace.GetProperty("groups").EnumerateArray());
        Assert.Equal("access-inspection", group.GetProperty("id").GetString());
        Assert.Equal("access.holders", group.GetProperty("labelKey").GetString());
        Assert.Equal("access.holders", Assert.Single(group.GetProperty("itemIds").EnumerateArray()).GetString());
        var item = Assert.Single(group.GetProperty("items").EnumerateArray());
        Assert.Equal("access.holders", item.GetProperty("id").GetString());
        Assert.Equal("access.holders", item.GetProperty("labelKey").GetString());
        Assert.Equal("Holders", item.GetProperty("label").GetString());
        var panel = Assert.Single(pack.GetProperty("panelSet").EnumerateArray());
        Assert.Equal("access-details", panel.GetProperty("id").GetString());
        Assert.Equal("access.details", panel.GetProperty("labelKey").GetString());
        Assert.Equal("panels.access-details.toggle", panel.GetProperty("binding").GetString());
        Assert.Equal("mod+shift+a", panel.GetProperty("shortcut").GetString());
        Assert.Equal(400, panel.GetProperty("defaultWidth").GetInt32());
        Assert.Equal(300, panel.GetProperty("minimumHeight").GetInt32());
        Assert.False(panel.GetProperty("defaultOpen").GetBoolean());
    }

    private static async Task<JsonDocument> NavigationAsync(IServiceProvider services)
    {
        await using var app = WebApplication.CreateBuilder().Build();
        PackNavigationRoutes.Map(app, services.GetRequiredService<IPackInstallStore>(), new ActiveTenant(services), NullLogger.Instance);
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Single(endpoint => endpoint.RoutePattern.RawText == PackNavigationRoutes.NavigationRoute);
        using var body = new MemoryStream();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Response.Body = body;
        await endpoint.RequestDelegate!(http);
        Assert.Equal(200, http.Response.StatusCode);
        return JsonDocument.Parse(body.ToArray());
    }

    private sealed class ActiveTenant(IServiceProvider services) : IActiveTeamAccessor
    {
        public TeamContext? Active => new(new TeamId(Guid.Parse(Tenant.Value)), "Access test", services, services.GetRequiredService<TimeProvider>());
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
#pragma warning disable CS0067
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
#pragma warning restore CS0067
    }
}
