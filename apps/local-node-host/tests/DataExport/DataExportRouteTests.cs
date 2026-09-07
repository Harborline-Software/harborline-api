using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Harborline.Api.Foundation.LocalFirst;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Health;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.DataExport;

public sealed class DataExportRouteTests
{
    [Fact]
    public async Task Host_route_exports_and_downloads_the_active_tenants_data()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddTestKernelClock();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddHarborlineLocalFirst();
        await using var app = builder.Build();

        var teamId = new TeamId(new Guid("11111111-1111-1111-1111-111111111111"));
        var teamServices = new ServiceCollection().BuildServiceProvider();
        await using var team = new TeamContext(teamId, "Export tenant", teamServices, TimeProvider.System);
        var activeTeam = new FixedActiveTeamAccessor(team);

        var store = app.Services.GetRequiredService<IOfflineStore>();
        await store.WriteAsync(
            "tenants/11111111-1111-1111-1111-111111111111/forms/contact-1",
            Encoding.UTF8.GetBytes("{\"name\":\"Ada\"}"));
        await store.WriteAsync(
            "tenants/22222222-2222-2222-2222-222222222222/forms/contact-2",
            Encoding.UTF8.GetBytes("{\"name\":\"Grace\"}"));

        app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        DataExportRoutes.Map(
            app.MapSelectedSessionProductGroup(),
            app.Services.GetRequiredService<IDataExportService>(),
            new ActiveTeamTenantContext(activeTeam));
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        using var client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };

        var start = await client.PostAsJsonAsync(
            DataExportRoutes.RouteBase,
            new { includeScopes = new[] { "forms" } });
        start.EnsureSuccessStatusCode();
        using var startBody = JsonDocument.Parse(await start.Content.ReadAsStringAsync());
        var exportId = startBody.RootElement.GetProperty("exportId").GetGuid();

        var download = await client.GetAsync($"{DataExportRoutes.RouteBase}/{exportId}/download");
        download.EnsureSuccessStatusCode();
        Assert.Equal("application/json", download.Content.Headers.ContentType?.MediaType);

        using var package = JsonDocument.Parse(await download.Content.ReadAsStringAsync());
        var contributor = Assert.Single(package.RootElement.GetProperty("contributors").EnumerateArray());
        var entry = Assert.Single(contributor.GetProperty("entries").EnumerateArray());
        Assert.Equal(
            "tenants/11111111-1111-1111-1111-111111111111/forms/contact-1",
            entry.GetProperty("key").GetString());
        Assert.Equal(
            "{\"name\":\"Ada\"}",
            Encoding.UTF8.GetString(entry.GetProperty("payloadBase64").GetBytesFromBase64()));

        await app.StopAsync();
    }

    private sealed class FixedActiveTeamAccessor(TeamContext active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void Keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
