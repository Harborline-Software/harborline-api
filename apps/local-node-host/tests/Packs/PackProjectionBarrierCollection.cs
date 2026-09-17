namespace Harborline.Api.LocalNodeHost.Tests.Packs;

// These tests deliberately pause a lease on the process-global projection barrier. Their bounded
// waits must measure the orchestrated interleave, not unrelated hosts activating packs in parallel.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PackProjectionBarrierCollection
{
    public const string Name = "Pack projection barrier interleavings";
}
