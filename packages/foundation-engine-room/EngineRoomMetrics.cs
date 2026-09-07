namespace Harborline.Api.Foundation.EngineRoom;

/// <summary>
/// OpenTelemetry source and instrument names for the Engine Room observability surface.
/// </summary>
public static class EngineRoomMetrics
{
    /// <summary>OpenTelemetry meter name.</summary>
    public const string MeterName = "Sunfish.EngineRoom";

    /// <summary>OpenTelemetry activity-source name.</summary>
    public const string ActivitySourceName = "Sunfish.EngineRoom";

    /// <summary>Current active peer count.</summary>
    public const string PeerCount = "harborline.engine_room.peer_count";

    /// <summary>Event throughput over the trailing window.</summary>
    public const string EventsThroughput = "harborline.engine_room.events_throughput";

    /// <summary>Cumulative gossip cycles completed.</summary>
    public const string GossipCycles = "harborline.engine_room.gossip_cycles";

    /// <summary>Total CRDT bytes across documents in scope.</summary>
    public const string CrdtTotalBytes = "harborline.engine_room.crdt_total_bytes";

    /// <summary>Number of documents eligible for compaction.</summary>
    public const string CrdtCompactionEligible = "harborline.engine_room.crdt_compaction_eligible";

    /// <summary>Per-subsystem status, encoded by <see cref="SubsystemStatus"/>.</summary>
    public const string SubsystemStatusGauge = "harborline.engine_room.subsystem_status";
}
