using System.Globalization;

namespace Harborline.Api.Foundation.RuleEngine.Registry;

/// <summary>
/// The per-rule-key monotonic-version comparator (ADR 0146 D5). Reuses the shipped S-8
/// monotonic-watermark SEMANTICS of <c>foundation-packs</c>' <c>PackVersion</c> — parse
/// <c>major.minor.patch</c> (extra numeric segments compared in order; a pre-release suffix after
/// '-' orders BEFORE the same core per SemVer §11; an unparseable version is the lowest possible,
/// fail-closed) — as the "which rule version wins offline" order. This is NOT a new versioning
/// primitive (D5: compose shipped patterns, no new primitive); it is the pack watermark applied
/// per rule key. It is mirrored here rather than referenced because the kernel rule engine cannot
/// depend UP on the pack machinery (packs consume rules as <c>PackContentKind.RuleDefinition</c>,
/// D8) — kept byte-compatible with <c>PackVersion.Compare</c>/<c>IsDowngrade</c>.
///
/// <para><c>foundation-versioning.VersionVector</c> is deliberately NOT used — it is a federation
/// handshake check, not a last-writer-wins watermark (a false friend, per the substrate survey).</para>
/// </summary>
public static class RuleVersion
{
    /// <summary>
    /// Compares two rule versions. Returns &lt;0 if <paramref name="a"/> precedes
    /// <paramref name="b"/>, 0 if equal, &gt;0 if <paramref name="a"/> follows <paramref name="b"/>.
    /// A total, deterministic order — so two offline peers holding the same published set resolve
    /// the identical "latest" regardless of the order records synced in (monotonically convergent).
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
            return string.CompareOrdinal(preA, preB);
        }

        return hasPreA ? -1 : 1;
    }

    /// <summary>True iff <paramref name="candidate"/> is strictly below <paramref name="watermark"/>
    /// (a downgrade, refused by default under S-8 — the reject-stale half of the watermark).</summary>
    public static bool IsDowngrade(string watermark, string candidate)
        => Compare(candidate, watermark) < 0;

    private static (int[] Core, string PreRelease) Split(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return (new[] { 0 }, string.Empty);
        }

        var plus = version.IndexOf('+', StringComparison.Ordinal);
        var trimmed = plus >= 0 ? version[..plus] : version;

        var dash = trimmed.IndexOf('-', StringComparison.Ordinal);
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
}
