using System.Net;

using Harborline.Api.Foundation.EngineRoom;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Health;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.Telemetry;

public sealed class LocalNodeHealthProbeRouteTests
{
    [Fact]
    public async Task DedicatedRoutesSeparateLivenessReadinessAndAggregateHealth()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<EngineRoomTelemetry>();
        builder.Services.AddSingleton<IActiveTeamAccessor, NoActiveTeamAccessor>();
        builder.Services.AddTransient<LocalNodeHealthCheck>();
        builder.Services.AddHealthChecks()
            .AddCheck<LocalNodeLivenessCheck>("local-node-liveness", tags: ["live"])
            .AddCheck<LocalNodeReadinessCheck>("local-node-readiness", tags: ["ready"]);

        await using var app = builder.Build();
        app.MapLocalNodeHealthProbes();
        await app.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health")).StatusCode);
    }

    private sealed class NoActiveTeamAccessor : IActiveTeamAccessor
    {
        public TeamContext? Active => null;

        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged
        {
            add { }
            remove { }
        }

        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
    }
}
