namespace Harborline.Api.Foundation.EngineRoom;

/// <summary>Engine Room subsystem status values and their metric encodings.</summary>
public enum SubsystemStatus
{
    /// <summary>The subsystem is operating normally; metric value 0.</summary>
    Operational = 0,

    /// <summary>The subsystem is impaired but remains available; metric value 1.</summary>
    Warning = 1,

    /// <summary>The subsystem is unavailable; metric value 2.</summary>
    Critical = 2,

    /// <summary>The subsystem has not reported; metric value 3.</summary>
    Unknown = 3,
}
