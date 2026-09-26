namespace Harborline.Api.LocalNodeHost.Tests.Layout;

// Owner ruling Q36: the Layout timing-parity tests measure one host's response times, so they must not
// share the CPU with the rest of the suite. On macpro (run 36195754191) parallel load pushed most samples
// past the floor, and the paths' timings then reflected the load, not the host.
// Owner ruling Q38 (T-724 rulings 7 to 9, T-268 option A): both classes carry Lane=perf. The host lanes'
// exact-clone excludes that trait, and verify-perf runs it alone on mac16 (perf-quiet).
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LayoutTimingParityCollection
{
    public const string Name = "LayoutTimingParity";

    /// <summary>The trait key the host lanes filter on.</summary>
    public const string LaneTrait = "Lane";

    /// <summary>The perf lane: excluded from exact-clone, run only by verify-perf.</summary>
    public const string PerfLane = "perf";
}
