namespace Harborline.Api.Foundation.EngineRoom;

/// <summary>Closed set of Engine Room operational subsystems from ADR 0079.</summary>
public enum EngineRoomSubsystem
{
    /// <summary>Sync daemon and CRDT transport.</summary>
    MainPropulsion,

    /// <summary>Runtime resources and CRDT storage growth.</summary>
    Electrical,

    /// <summary>Quarantine, release, and compaction operations.</summary>
    DamageControl,

    /// <summary>Structural-integrity and conformance checks.</summary>
    QaWorkshop,
}
