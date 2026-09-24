using System.Net.Http.Json;

using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Data.DataExchange;
using Harborline.Foundation.DataExchange;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.DataExchangeDefinitions;

public sealed class DataExchangePlatformRouteTests
{
    [Fact]
    public void Host_composes_operational_stores_from_the_released_package()
    {
        using var services = new ServiceCollection()
            .AddPlatformDataExchange()
            .BuildServiceProvider();

        Assert.IsType<InMemoryExchangeRunStore>(services.GetRequiredService<IExchangeRunStore>());
        Assert.IsType<InMemoryAcquisitionCheckpointStore>(services.GetRequiredService<IAcquisitionCheckpointStore>());
        Assert.IsType<InMemoryProtectedEffectPayloadStore>(services.GetRequiredService<IProtectedEffectPayloadStore>());
        Assert.Equal("Harborline.Foundation.DataExchange", typeof(IExchangeRunStore).Assembly.GetName().Name);
    }

    [Fact]
    public async Task Runtime_contract_reports_the_released_inbound_profile()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        DataExchangePlatformRoutes.Map(app);
        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        using var client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
        var contract = await client.GetFromJsonAsync<DataExchangeRuntimeContractDto>(DataExchangePlatformRoutes.Route);

        Assert.NotNull(contract);
        Assert.Equal("hl:tabular-mapping/v1", contract.Profile);
        Assert.Equal("https://schemas.harborline.software/mapping/tabular/v1", contract.Schema);
        Assert.Equal("1.0.0", contract.DocumentVersion);
        Assert.True(contract.InboundOnly);
        Assert.Equal(
            ["data-exchange:author", "data-exchange:dry-run", "data-exchange:commit", "data-exchange:read-run-results"],
            contract.Permissions);
        Assert.Equal(["Applied", "Skipped", "Conflicted", "Rejected", "Failed", "Halted"], contract.EffectOutcomes);
    }
}
