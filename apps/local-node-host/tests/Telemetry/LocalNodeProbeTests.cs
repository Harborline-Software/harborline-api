using Harborline.Api.Foundation.EngineRoom;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Health;

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Harborline.Api.LocalNodeHost.Tests.Telemetry;

public sealed class LocalNodeProbeTests
{
    [Fact]
    public async Task NodeWithoutAnActiveTeamIsLiveButNotReady()
    {
        using var telemetry = new EngineRoomTelemetry();
        IHealthCheck liveness = new LocalNodeLivenessCheck(telemetry);
        IHealthCheck readiness = new LocalNodeReadinessCheck(
            new LocalNodeHealthCheck(new NoActiveTeamAccessor()),
            telemetry);

        var liveResult = await liveness.CheckHealthAsync(new HealthCheckContext());
        var readyResult = await readiness.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, liveResult.Status);
        Assert.Equal(HealthStatus.Unhealthy, readyResult.Status);
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
