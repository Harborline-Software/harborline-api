using System.Diagnostics;
using System.Globalization;
using System.Text;

using Xunit.Abstractions;

namespace Harborline.Api.LocalNodeHost.Tests.Layout;

/// <summary>
/// The T-731 timing-parity gate (owner rulings Q41 and Q41b; research R-0119, after SILENT, Dunsche et
/// al. 2025, arXiv 2504.19821, and tlsfuzzer's timing analysis). It checks specified timing bounds for a
/// missing and a denied related target under these test conditions: one host, one process, the floor
/// measured on the host. It does not establish indistinguishability against every remote attacker, and it
/// is not a LAN certification.
/// </summary>
/// <remarks>
/// <para>
/// The floor is derived the way owner ruling 1 derives the production one: the worst of 100 denied runs
/// with no floor, doubled, rounded up to the 15.6 ms tick, at least four ticks. Then 60 preplanned pairs
/// run, one missing and one denied resolution each, counterbalanced: even pairs run missing first, odd
/// pairs denied first.
/// </para>
/// <para>
/// Two rules decide, and both must hold:
/// </para>
/// <list type="bullet">
///   <item>The median rule. The 60 paired differences (denied minus missing), sorted, have their 17th
///     and 44th order statistics (1-based) strictly inside ±0.1 ms. For 60 independent pairs those two
///     order statistics bound the median of the difference with 99.961% coverage (binomial: 1 - 2 P(X ≤
///     16), X ~ Bin(60, 1/2)). The coverage holds only if the pairs are independent; the lag-1
///     autocorrelation is logged so that can be checked.</item>
///   <item>The tail rule. |p90(denied) - p90(missing)| &lt; 1 ms, each path's p90 taken over its own 60
///     timings, as a point estimate with the linear-interpolation quantile (Hyndman-Fan type 7). It is
///     the gap between the paths' p90s, not the p90 of the paired differences, so noise that moves
///     both paths alike cancels. It catches a leak that hits only a minority of denials, which the
///     median rule cannot see.</item>
/// </list>
/// <para>
/// The two-sample KS D, the median interval's endpoints, the p90 gap and the lag-1 autocorrelation of the
/// paired differences in run order are diagnostics only and never decide. All 60 raw pairs are written to
/// the test output.
/// </para>
/// </remarks>
internal static class TimingParity
{
    private const int Pairs = 60;
    private const double Tick = 15.625;

    /// <summary>1-based order statistics bounding the median of 60 paired differences at 99.961% coverage.</summary>
    internal const int LowerRank = 17, UpperRank = 44;

    /// <summary>The median rule's bound: both order statistics strictly inside ±0.1 ms.</summary>
    internal const double MedianBoundMs = 0.1;

    /// <summary>The tail rule's bound: the between-path p90 gap strictly under 1 ms.</summary>
    internal const double TailBoundMs = 1.0;

    /// <summary>One preplanned pair, in run order.</summary>
    internal sealed record Pair(int Index, bool DeniedFirst, double MissingMs, double DeniedMs)
    {
        public double DifferenceMs => DeniedMs - MissingMs;
    }

    /// <summary>What the two rules decided, and the diagnostics beside them.</summary>
    internal sealed record Verdict(
        bool MedianRule, bool TailRule, double LowerMs, double UpperMs, double P90GapMs, double KsD, double Lag1)
    {
        public bool Passed => MedianRule && TailRule;

        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"{(Passed ? "PASS" : "FAIL")}: median rule {(MedianRule ? "held" : "failed")} "
            + $"(d[{LowerRank}] {LowerMs:F3} ms, d[{UpperRank}] {UpperMs:F3} ms, bound ±{MedianBoundMs} ms); "
            + $"tail rule {(TailRule ? "held" : "failed")} (p90 gap {P90GapMs:F3} ms, bound {TailBoundMs} ms); "
            + $"diagnostics KS D {KsD:F3}, lag-1 autocorrelation {Lag1:F3}");
    }

    /// <summary>Applies the two rules to <paramref name="pairs"/>. Pure, so synthetic samples can test it.</summary>
    internal static Verdict Evaluate(IReadOnlyList<Pair> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var differences = pairs.Select(pair => pair.DifferenceMs).Order().ToArray();
        var lower = differences[LowerRank - 1];
        var upper = differences[UpperRank - 1];
        var medianRule = lower > -MedianBoundMs && upper < MedianBoundMs;

        var gap = QuantileType7(pairs.Select(pair => pair.DeniedMs), 0.9) - QuantileType7(pairs.Select(pair => pair.MissingMs), 0.9);
        var tailRule = Math.Abs(gap) < TailBoundMs;

        return new Verdict(medianRule, tailRule, lower, upper, gap,
            KolmogorovSmirnov(pairs.Select(pair => pair.MissingMs), pairs.Select(pair => pair.DeniedMs)),
            Lag1(pairs.Select(pair => pair.DifferenceMs).ToArray()));
    }

    /// <param name="at">For a floor, a sampler that runs one missing (false) or denied (true) resolution.</param>
    /// <param name="settle">Waits for background work to finish between the calibration and the samples.</param>
    /// <param name="label">Names the row in the output and in a failure.</param>
    /// <param name="output">Receives the verdict and all 60 raw pairs, pass or fail.</param>
    public static async Task AssertSameAsync(
        Func<TimeSpan, Func<bool, Task>> at, Func<Task> settle, string label, ITestOutputHelper output)
    {
        var calibration = at(TimeSpan.Zero);
        for (var i = 0; i < 5; i++) await calibration(true); // JIT warm-up, discarded
        var worst = 0.0;
        for (var i = 0; i < 100; i++)
        {
            var clock = Stopwatch.StartNew();
            await calibration(true);
            worst = Math.Max(worst, clock.Elapsed.TotalMilliseconds);
        }
        await settle();
        var floor = TimeSpan.FromMilliseconds(Math.Max(4, Math.Ceiling(2 * worst / Tick)) * Tick);
        var sample = at(floor);

        var pairs = new List<Pair>(Pairs);
        for (var i = 0; i < Pairs; i++)
        {
            var deniedFirst = i % 2 == 1;
            double missing = 0, denied = 0;
            foreach (var ownerExists in deniedFirst ? new[] { true, false } : [false, true])
            {
                var clock = Stopwatch.StartNew();
                await sample(ownerExists);
                if (ownerExists) denied = clock.Elapsed.TotalMilliseconds;
                else missing = clock.Elapsed.TotalMilliseconds;
            }
            pairs.Add(new Pair(i, deniedFirst, missing, denied));
        }
        await settle();

        var verdict = Evaluate(pairs);
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"{label}: {verdict} at floor {floor.TotalMilliseconds} ms");
        report.AppendLine(CultureInfo.InvariantCulture, $"{label}: pairs (index, first, missing ms, denied ms, denied - missing ms)");
        foreach (var pair in pairs)
            report.AppendLine(CultureInfo.InvariantCulture,
                $"{label}: {pair.Index} {(pair.DeniedFirst ? "denied" : "missing")} {pair.MissingMs:F4} {pair.DeniedMs:F4} {pair.DifferenceMs:F4}");
        output.WriteLine(report.ToString());
        Assert.True(verdict.Passed, $"{label}: {verdict} at floor {floor.TotalMilliseconds} ms");
    }

    /// <summary>Hyndman-Fan type 7: linear interpolation between order statistics at (n - 1) p.</summary>
    internal static double QuantileType7(IEnumerable<double> values, double p)
    {
        var sorted = values.Order().ToArray();
        var h = (sorted.Length - 1) * p;
        var low = (int)Math.Floor(h);
        return low + 1 < sorted.Length ? sorted[low] + (h - low) * (sorted[low + 1] - sorted[low]) : sorted[low];
    }

    private static double KolmogorovSmirnov(IEnumerable<double> a, IEnumerable<double> b)
    {
        var x = a.Order().ToArray();
        var y = b.Order().ToArray();
        double d = 0;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            var t = Math.Min(x[i], y[j]);
            while (i < x.Length && x[i] <= t) i++;
            while (j < y.Length && y[j] <= t) j++;
            d = Math.Max(d, Math.Abs((double)i / x.Length - (double)j / y.Length));
        }
        return d;
    }

    private static double Lag1(double[] series)
    {
        var mean = series.Average();
        double numerator = 0, denominator = 0;
        for (var i = 0; i < series.Length; i++)
        {
            denominator += (series[i] - mean) * (series[i] - mean);
            if (i + 1 < series.Length) numerator += (series[i] - mean) * (series[i + 1] - mean);
        }
        return denominator == 0 ? 0 : numerator / denominator;
    }
}
