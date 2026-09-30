namespace Harborline.Api.LocalNodeHost.Tests;

/// <summary>
/// The two-ceiling convention (T-680, api PR #262) for a test that waits on the wall clock: the quiet
/// ceiling only when <c>HARBORLINE_PERF_QUIET=1</c>, which a lane that has the box to itself sets;
/// anything else is a busy box and gets the busy ceiling. The host lanes run the whole suite in
/// parallel on shared runners, so they get the busy ceiling. A busy box costs the wait its tight
/// bound, never its verdict: the condition the test waits for must still arrive.
/// </summary>
internal static class LoadCeiling
{
    /// <summary>The variable the perf lanes set, read the same way as the TypeScript perf rows.</summary>
    internal const string QuietVariable = "HARBORLINE_PERF_QUIET";

    /// <summary>True only when this box is marked quiet.</summary>
    public static bool IsQuiet => Environment.GetEnvironmentVariable(QuietVariable) == "1";

    /// <summary><paramref name="quiet"/> on a quiet box, otherwise <paramref name="busy"/>.</summary>
    public static TimeSpan Pick(TimeSpan quiet, TimeSpan busy) => IsQuiet ? quiet : busy;
}
