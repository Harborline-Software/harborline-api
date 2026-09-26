namespace Harborline.Api.LocalNodeHost.Tests.Layout;

/// <summary>
/// T-731, owner rulings Q41 and Q41b: the timing-parity gate against seeded, synthetic samples, with no
/// real timing. The noise is 60 draws of 62.5 ms plus up to 0.04 ms. The denied path gets the same
/// draws in another order, plus any seeded leak, so each path's own distribution is exactly the noise
/// and the leak.
/// </summary>
public sealed class TimingParityGateTests
{
    public enum Leak { None, FiveMsOnEveryDenial, OneMsOnEveryDenial, FiveMsOnOneInFiveDenials }

    [Theory(DisplayName = "layout-eng-31: the timing-parity gate passes identical noise and fails a seeded leak (+5 ms or +1 ms on every denial, +5 ms on 20% of denials)")]
    [InlineData(Leak.None, true)]
    [InlineData(Leak.FiveMsOnEveryDenial, false)]
    [InlineData(Leak.OneMsOnEveryDenial, false)]
    [InlineData(Leak.FiveMsOnOneInFiveDenials, false)]
    public void TheGateCatchesASeededLeak(Leak leak, bool passes)
    {
        var verdict = TimingParity.Evaluate(Samples(leak));

        Assert.Equal(passes, verdict.Passed);
        // Which rule sees what: a leak on every denial moves the median and the tail; a leak on a
        // minority of denials leaves the median where it was, and only the tail rule sees it.
        var (median, tail) = leak switch
        {
            Leak.None => (true, true),
            Leak.FiveMsOnOneInFiveDenials => (true, false),
            _ => (false, false),
        };
        Assert.Equal((median, tail), (verdict.MedianRule, verdict.TailRule));
    }

    [Fact(DisplayName = "layout-eng-31: the timing-parity gate reads p90 by linear interpolation (Hyndman-Fan type 7)")]
    public void TheTailRuleUsesTypeSevenQuantiles()
    {
        // (n - 1) p = 9 * 0.9 = 8.1, so the p90 of 1..10 is 9 + 0.1 * (10 - 9).
        Assert.Equal(9.1, TimingParity.QuantileType7(Enumerable.Range(1, 10).Select(i => (double)i), 0.9), 10);
    }

    private static List<TimingParity.Pair> Samples(Leak leak)
    {
        var random = new Random(731);
        var noise = Enumerable.Range(0, 60).Select(_ => 62.5 + random.NextDouble() * 0.04).ToArray();
        var reordered = noise.OrderBy(_ => random.Next()).ToArray();
        return Enumerable.Range(0, 60).Select(i =>
        {
            var extra = leak switch
            {
                Leak.FiveMsOnEveryDenial => 5.0,
                Leak.OneMsOnEveryDenial => 1.0,
                Leak.FiveMsOnOneInFiveDenials when i % 5 == 0 => 5.0,
                _ => 0.0,
            };
            return new TimingParity.Pair(i, i % 2 == 1, noise[i], reordered[i] + extra);
        }).ToList();
    }
}
