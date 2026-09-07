using System.Diagnostics;
using System.Diagnostics.Metrics;

using Harborline.Api.Foundation.EngineRoom;

namespace Harborline.Api.LocalNodeHost.Tests.Telemetry;

public sealed class EngineRoomTelemetryTests
{
    [Fact]
    public void RecordSubsystemStatusEmitsCatalogMetricAndSpan()
    {
        var measurements = new List<(string Instrument, int Value, string? Subsystem)>();
        var activities = new List<Activity>();

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Sunfish.EngineRoom")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
        {
            var subsystem = tags.ToArray()
                .Single(tag => tag.Key == "subsystem")
                .Value as string;
            measurements.Add((instrument.Name, value, subsystem));
        });
        meterListener.Start();

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Sunfish.EngineRoom",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(activityListener);

        using var telemetry = new EngineRoomTelemetry();

        telemetry.RecordSubsystemStatus(
            EngineRoomSubsystem.MainPropulsion,
            SubsystemStatus.Warning);
        meterListener.RecordObservableInstruments();

        Assert.Contains(
            measurements,
            measurement =>
                measurement.Instrument == "harborline.engine_room.subsystem_status" &&
                measurement.Value == 1 &&
                measurement.Subsystem == "main_propulsion");

        Assert.Contains(
            activities,
            activity =>
                activity.Source.Name == "Sunfish.EngineRoom" &&
                activity.OperationName == "harborline.engine_room.subsystem_status" &&
                Equals(activity.GetTagItem("subsystem"), "main_propulsion") &&
                Equals(activity.GetTagItem("status"), "warning"));
    }
}
