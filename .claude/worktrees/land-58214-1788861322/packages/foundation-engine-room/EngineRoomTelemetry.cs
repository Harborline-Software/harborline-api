using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Harborline.Api.Foundation.EngineRoom;

/// <summary>
/// Emits Engine Room measurements and activities through the standard .NET diagnostics seams.
/// </summary>
public sealed class EngineRoomTelemetry : IDisposable
{
    private readonly ConcurrentDictionary<EngineRoomSubsystem, SubsystemStatus> _statuses = new();
    private readonly Meter _meter = new(EngineRoomMetrics.MeterName);
    private readonly ActivitySource _activitySource = new(EngineRoomMetrics.ActivitySourceName);

    /// <summary>Constructs the catalog instruments for this emitter.</summary>
    public EngineRoomTelemetry()
    {
        _meter.CreateObservableGauge(
            EngineRoomMetrics.SubsystemStatusGauge,
            ObserveSubsystemStatuses,
            unit: "{status}",
            description: "0=Operational, 1=Warning, 2=Critical, 3=Unknown.");
    }

    /// <summary>
    /// Records the latest status for <paramref name="subsystem"/> and emits a span for the observation.
    /// </summary>
    /// <param name="subsystem">Subsystem that was observed.</param>
    /// <param name="status">Latest subsystem status.</param>
    public void RecordSubsystemStatus(EngineRoomSubsystem subsystem, SubsystemStatus status)
    {
        var subsystemName = SubsystemName(subsystem);
        using var activity = _activitySource.StartActivity(EngineRoomMetrics.SubsystemStatusGauge);
        activity?.SetTag("subsystem", subsystemName);
        activity?.SetTag("status", StatusName(status));

        _statuses[subsystem] = status;
    }

    /// <summary>
    /// Starts the server span for an invoke arriving from the capability membrane.
    /// </summary>
    /// <param name="correlationId">Capability correlation id propagated with the invoke.</param>
    /// <returns>The started activity; dispose it when the invoke completes.</returns>
    public Activity StartCapabilityInvoke(string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var hasW3CParent = TryCreateParentContext(correlationId, out var parentContext);
        var activity = hasW3CParent
            ? _activitySource.StartActivity(
                "capability.invoke",
                ActivityKind.Server,
                parentContext)
            : _activitySource.StartActivity("capability.invoke", ActivityKind.Server);
        activity ??= StartAmbientFallback(correlationId, hasW3CParent, parentContext);
        activity.AddBaggage("capability.correlation_id", correlationId);
        activity.SetTag("capability.correlation_id", correlationId);
        return activity;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _activitySource.Dispose();
        _meter.Dispose();
    }

    private IEnumerable<Measurement<int>> ObserveSubsystemStatuses()
    {
        foreach (var (subsystem, status) in _statuses)
        {
            yield return new Measurement<int>(
                (int)status,
                new KeyValuePair<string, object?>("subsystem", SubsystemName(subsystem)));
        }
    }

    private static Activity StartAmbientFallback(
        string correlationId,
        bool hasW3CParent,
        ActivityContext parentContext)
    {
        var activity = new Activity("capability.invoke")
            .SetIdFormat(ActivityIdFormat.W3C);
        if (hasW3CParent)
        {
            activity.SetParentId(
                parentContext.TraceId,
                parentContext.SpanId,
                parentContext.TraceFlags);
        }

        return activity.Start();
    }

    private static bool TryCreateParentContext(
        string correlationId,
        out ActivityContext parentContext)
    {
        var parentSpanId = ActivitySpanId.CreateRandom();
        return ActivityContext.TryParse(
            $"00-{correlationId}-{parentSpanId}-01",
            null,
            out parentContext);
    }

    private static string SubsystemName(EngineRoomSubsystem subsystem) => subsystem switch
    {
        EngineRoomSubsystem.MainPropulsion => "main_propulsion",
        EngineRoomSubsystem.Electrical => "electrical",
        EngineRoomSubsystem.DamageControl => "damage_control",
        EngineRoomSubsystem.QaWorkshop => "qa_workshop",
        _ => throw new ArgumentOutOfRangeException(nameof(subsystem), subsystem, null),
    };

    private static string StatusName(SubsystemStatus status) => status switch
    {
        SubsystemStatus.Operational => "operational",
        SubsystemStatus.Warning => "warning",
        SubsystemStatus.Critical => "critical",
        SubsystemStatus.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}
