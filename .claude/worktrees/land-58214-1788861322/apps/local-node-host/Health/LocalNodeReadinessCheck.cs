using Harborline.Api.Foundation.EngineRoom;

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Reports whether the node has materialized its active team and can serve workload traffic.
/// </summary>
public sealed class LocalNodeReadinessCheck : IHealthCheck
{
    private readonly LocalNodeHealthCheck _healthCheck;
    private readonly EngineRoomTelemetry _telemetry;

    /// <summary>Constructs the readiness probe.</summary>
    /// <param name="healthCheck">Aggregate node health check.</param>
    /// <param name="telemetry">Engine Room diagnostics emitter.</param>
    public LocalNodeReadinessCheck(
        LocalNodeHealthCheck healthCheck,
        EngineRoomTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(healthCheck);
        ArgumentNullException.ThrowIfNull(telemetry);
        _healthCheck = healthCheck;
        _telemetry = telemetry;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var result = await _healthCheck.CheckHealthAsync(context, cancellationToken)
            .ConfigureAwait(false);
        _telemetry.RecordSubsystemStatus(
            EngineRoomSubsystem.MainPropulsion,
            result.Status switch
            {
                HealthStatus.Healthy => SubsystemStatus.Operational,
                HealthStatus.Degraded => SubsystemStatus.Warning,
                _ => SubsystemStatus.Critical,
            });
        return result;
    }
}
