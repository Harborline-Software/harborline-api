using Harborline.Api.Foundation.EngineRoom;

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Reports whether the node process can answer requests, independently of bootstrap readiness.
/// </summary>
public sealed class LocalNodeLivenessCheck : IHealthCheck
{
    private readonly EngineRoomTelemetry _telemetry;

    /// <summary>Constructs the liveness probe.</summary>
    /// <param name="telemetry">Engine Room diagnostics emitter.</param>
    public LocalNodeLivenessCheck(EngineRoomTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        _telemetry = telemetry;
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        _telemetry.RecordSubsystemStatus(
            EngineRoomSubsystem.MainPropulsion,
            SubsystemStatus.Operational);
        return Task.FromResult(HealthCheckResult.Healthy("The node process is responsive."));
    }
}
