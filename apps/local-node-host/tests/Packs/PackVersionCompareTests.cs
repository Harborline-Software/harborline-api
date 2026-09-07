using Harborline.Api.Foundation.Packs.Install;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// Ticket 152/160 review — <see cref="PackVersion.Compare"/> must order PRE-RELEASE identifiers per
/// SemVer §11, not ordinally: an ordinal compare ranks <c>alpha.9</c> ABOVE <c>alpha.10</c>, which is a
/// fail-OPEN on every restricting check that compares pre-releases (a dependency pin
/// <c>1.0.0-alpha.10</c> satisfied by installed <c>1.0.0-alpha.9</c>; a platform floor
/// <c>2.0.0-rc.10</c> passed by a running <c>2.0.0-rc.9</c>).
/// </summary>
public sealed class PackVersionCompareTests
{
    [Theory(DisplayName = "SemVer §11 pre-release ordering: numeric identifiers compare numerically")]
    [InlineData("1.0.0-alpha.9", "1.0.0-alpha.10")]
    [InlineData("2.0.0-rc.9", "2.0.0-rc.10")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")] // fewer identifiers rank first when the prefix ties.
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")] // numeric < alphanumeric.
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-rc.1", "1.0.0")] // release ranks above any pre-release of the same core.
    public void PreRelease_Orders_Per_SemVer(string lower, string higher)
    {
        Assert.True(PackVersion.Compare(lower, higher) < 0, $"{lower} must precede {higher}");
        Assert.True(PackVersion.Compare(higher, lower) > 0, $"{higher} must follow {lower}");
    }

    [Fact(DisplayName = "existing comparisons unchanged: core segments, equality, unparseable-as-lowest")]
    public void Existing_Comparisons_Unchanged()
    {
        Assert.True(PackVersion.Compare("1.2.3", "1.2.4") < 0);
        Assert.True(PackVersion.Compare("1.10.0", "1.9.0") > 0);
        Assert.Equal(0, PackVersion.Compare("1.2.3", "1.2.3"));
        Assert.Equal(0, PackVersion.Compare("1.2.3+build.1", "1.2.3+build.2")); // build metadata ignored.
        Assert.Equal(0, PackVersion.Compare("1.2", "1.2.0")); // missing segments read as 0.
        Assert.True(PackVersion.IsDowngrade(watermark: "2.0.0", candidate: "1.9.9"));
        // Fail-closed CANDIDATE direction: an unparseable version is the lowest possible.
        Assert.True(PackVersion.Compare("not-a-version", "0.0.1") < 0);
    }

    [Fact(DisplayName = "IsWellFormed: the pin-direction guard rejects what Compare would degrade to 0")]
    public void IsWellFormed_Rejects_Unparseable_Pins()
    {
        Assert.True(PackVersion.IsWellFormed("1.0.0"));
        Assert.True(PackVersion.IsWellFormed("1.0.0-alpha.10"));
        Assert.True(PackVersion.IsWellFormed("1.0.0+build.7"));

        Assert.False(PackVersion.IsWellFormed(""));
        Assert.False(PackVersion.IsWellFormed("   "));
        Assert.False(PackVersion.IsWellFormed("not-a-version"));
        Assert.False(PackVersion.IsWellFormed("1.x.0"));
        // Passes the exporter's pinned-version regex yet overflows the comparator's int segments —
        // the exact in-band shape that silently degraded to 0.0.0 and was trivially satisfied.
        Assert.False(PackVersion.IsWellFormed("99999999999999999999.0.0"));
    }
}
