using System.Globalization;

namespace Harborline.Api.Foundation.Packs.Install;

/// <summary>
/// A minimal pinned semantic-version comparator for the S-8 monotonic-version watermark. Parses
/// <c>major.minor.patch</c> (extra numeric segments compared in order; a pre-release suffix after '-' is
/// ordered BEFORE the same core version per SemVer §11, which is conservative for a downgrade check — a
/// pre-release never satisfies a stable watermark). Pre-release identifiers order per SemVer §11:
/// dot-separated, numeric identifiers numerically, numeric before alphanumeric, and a shorter identifier
/// set before a longer one when all preceding identifiers are equal — so <c>alpha.9</c> precedes
/// <c>alpha.10</c> (an ordinal compare would invert that, a fail-open on every restricting check that
/// compares pre-releases: dependency pins and platform floors). Fail-closed: an unparseable version is
/// treated as the lowest possible, so it can never silently satisfy a watermark — the CANDIDATE
/// direction. The opposite direction (a malformed PIN that would be trivially satisfied by anything) is
/// the caller's to refuse; <see cref="IsWellFormed"/> exists for exactly that check.
/// </summary>
public static class PackVersion
{
    /// <summary>
    /// Compares two pinned pack versions. Returns &lt;0 if <paramref name="a"/> precedes
    /// <paramref name="b"/>, 0 if equal, &gt;0 if <paramref name="a"/> follows <paramref name="b"/>.
    /// </summary>
    public static int Compare(string a, string b)
    {
        var (coreA, preA) = Split(a);
        var (coreB, preB) = Split(b);

        var max = System.Math.Max(coreA.Length, coreB.Length);
        for (var i = 0; i < max; i++)
        {
            var ai = i < coreA.Length ? coreA[i] : 0;
            var bi = i < coreB.Length ? coreB[i] : 0;
            if (ai != bi)
            {
                return ai.CompareTo(bi);
            }
        }

        // Equal core: a pre-release precedes the stable release of the same core (SemVer §11).
        var hasPreA = preA.Length > 0;
        var hasPreB = preB.Length > 0;
        if (hasPreA == hasPreB)
        {
            return hasPreA ? ComparePreRelease(preA, preB) : 0;
        }

        return hasPreA ? -1 : 1;
    }

    /// <summary>True iff <paramref name="candidate"/> is strictly below <paramref name="watermark"/>
    /// (a downgrade, refused by default under S-8).</summary>
    public static bool IsDowngrade(string watermark, string candidate)
        => Compare(candidate, watermark) < 0;

    /// <summary>
    /// True iff every dotted core segment of <paramref name="version"/> parses as a non-negative
    /// integer. <see cref="Compare"/> deliberately degrades an unparseable segment to 0 — the
    /// fail-closed direction for a CANDIDATE or watermark (lowest possible, satisfies nothing). For a
    /// dependency PIN that degradation is fail-OPEN (a pin of 0 is trivially satisfied by anything
    /// installed), so a pin-consuming caller must refuse a version this returns false for instead of
    /// comparing it.
    /// </summary>
    public static bool IsWellFormed(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var plus = version.IndexOf('+');
        var trimmed = plus >= 0 ? version[..plus] : version;
        var dash = trimmed.IndexOf('-');
        var core = dash >= 0 ? trimmed[..dash] : trimmed;

        foreach (var segment in core.Split('.'))
        {
            if (!int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }
        }

        return true;
    }

    private static (int[] Core, string PreRelease) Split(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return (new[] { 0 }, string.Empty);
        }

        var plus = version.IndexOf('+');
        var trimmed = plus >= 0 ? version[..plus] : version;

        var dash = trimmed.IndexOf('-');
        var core = dash >= 0 ? trimmed[..dash] : trimmed;
        var pre = dash >= 0 ? trimmed[(dash + 1)..] : string.Empty;

        var segments = core.Split('.');
        var parsed = new int[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            parsed[i] = int.TryParse(segments[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        }

        return (parsed, pre);
    }

    /// <summary>SemVer §11 pre-release ordering: dot-separated identifiers compared left to right —
    /// numerically when both are numeric, ordinally when both are alphanumeric, numeric below
    /// alphanumeric when mixed; equal prefixes rank the shorter identifier set first.</summary>
    private static int ComparePreRelease(string a, string b)
    {
        var idsA = a.Split('.');
        var idsB = b.Split('.');
        var shared = System.Math.Min(idsA.Length, idsB.Length);
        for (var i = 0; i < shared; i++)
        {
            // long, not int: a numeric identifier has no size bound in SemVer, and overflowing to the
            // "not numeric" branch would silently flip the ordering rule for that identifier.
            var aNumeric = long.TryParse(idsA[i], NumberStyles.None, CultureInfo.InvariantCulture, out var aValue);
            var bNumeric = long.TryParse(idsB[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bValue);
            var c = (aNumeric, bNumeric) switch
            {
                (true, true) => aValue.CompareTo(bValue),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(idsA[i], idsB[i]),
            };
            if (c != 0)
            {
                return c;
            }
        }

        return idsA.Length.CompareTo(idsB.Length);
    }
}
