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

using NodeOperatorCommand = global::Harborline.Api.NodeOperatorCli.OperatorCli;

namespace Harborline.Api.LocalNodeHost.Tests.OperatorCli;

public sealed class OperatorCliHeadlessEndToEndTests
{
    [Fact]
    public async Task Cli_drives_authenticated_export_over_real_listener_without_gui()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddTestKernelClock();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddHarborlineLocalFirst();
        await using var app = builder.Build();

        var teamId = new TeamId(new Guid("11111111-1111-1111-1111-111111111111"));
        await using var team = new TeamContext(
            teamId,
            "Headless tenant",
            new ServiceCollection().BuildServiceProvider(), TimeProvider.System);
        var activeTeam = new FixedActiveTeamAccessor(team);
        var store = app.Services.GetRequiredService<IOfflineStore>();
        await store.WriteAsync(
            "tenants/11111111-1111-1111-1111-111111111111/forms/contact-1",
            Encoding.UTF8.GetBytes("{\"name\":\"Ada\"}"));
        await store.WriteAsync(
            "tenants/11111111-1111-1111-1111-111111111111/documents/private-1",
            Encoding.UTF8.GetBytes("{\"title\":\"Excluded\"}"));

        var exports = app.Services.GetRequiredService<IDataExportService>();
        app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        DataExportRoutes.Map(
            app.MapSelectedSessionProductGroup(),
            exports,
            new ActiveTeamTenantContext(activeTeam),
            new NodeCallerSessionToken("headless-secret"));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var client = new HttpClient();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await NodeOperatorCommand.RunAsync(
            [
                "--url", address, "--token", "headless-secret", "--json",
                "export", "--scope", "forms",
            ],
            client,
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr.ToString());
        using var handleJson = JsonDocument.Parse(stdout.ToString());
        var exportId = handleJson.RootElement.GetProperty("exportId").GetGuid();
        var status = await exports.GetStatusAsync(exportId);
        Assert.Equal(ExportState.Completed, status.State);

        await using var download = await exports.OpenDownloadAsync(exportId);
        using var package = await JsonDocument.ParseAsync(download);
        var contributor = Assert.Single(package.RootElement.GetProperty("contributors").EnumerateArray());
        var entry = Assert.Single(contributor.GetProperty("entries").EnumerateArray());
        Assert.EndsWith("/forms/contact-1", entry.GetProperty("key").GetString(), StringComparison.Ordinal);

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
