namespace Harborline.Api.LocalNodeHost.Tests.Layout;

// Owner ruling Q36: the Layout timing-parity tests measure one host's response times, so they must not
// share the CPU with the rest of the suite. On macpro (run 36195754191) parallel load pushed most samples
// past the floor, and the paths' timings then reflected the load, not the host.
// The timing theories carry Lane=perf. The host lanes' exact-clone excludes that trait;
// verify-perf-hosted runs it on Linux, with verify-perf comparing results on mac16.
// The classes' other tests (shape parity, fault paths, alerts, probe logging) stay in the host lanes.
// T-680: those perf jobs set HARBORLINE_PERF_QUIET=1 and the gate applies its quiet bounds; anywhere else
// (an unfiltered local run beside other lanes) it applies TimingParity.Bounds.Busy instead of going red.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LayoutTimingParityCollection
{
    public const string Name = "LayoutTimingParity";

    /// <summary>The trait key the host lanes filter on.</summary>
    public const string LaneTrait = "Lane";

    /// <summary>The perf lane: excluded from exact-clone and run by the dedicated perf jobs.</summary>
    public const string PerfLane = "perf";
}
