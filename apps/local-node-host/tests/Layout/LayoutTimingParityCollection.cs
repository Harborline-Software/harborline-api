namespace Harborline.Api.LocalNodeHost.Tests.Layout;

// Owner ruling Q36: the Layout timing-parity tests measure one host's response times, so they must not
// share the CPU with the rest of the suite. On macpro (run 36195754191) parallel load pushed most samples
// past the floor, and the paths' timings then reflected the load, not the host.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LayoutTimingParityCollection
{
    public const string Name = "LayoutTimingParity";
}
