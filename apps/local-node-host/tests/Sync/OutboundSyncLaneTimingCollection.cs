namespace Harborline.Api.LocalNodeHost.Tests.Sync;

// This fixture asserts a one-second end-to-end guard while a producer is blocked.
// Keep its lane-isolation cases together, without moving unrelated test classes.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OutboundSyncLaneTimingCollection
{
    public const string Name = "Outbound sync lane timing isolation";
}
