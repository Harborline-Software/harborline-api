using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;

namespace Harborline.Api.Foundation.Packs.Export;

/// <summary>
/// The AUTHOR-SIDE floor-raise-only guard (Pack Composer design §7.1). When an author bumps a pack
/// version it warns — BEFORE export — if the candidate version is a downgrade or LOWERS a safety floor
/// below the prior version, so the author does not compose a pack the S-8 watermark will refuse at install.
/// </summary>
/// <remarks>
/// <para>
/// <b>SINGLE SOURCE (council A-3 fold).</b> This guard reuses the INSTALLER's exact floor logic —
/// <see cref="PackSafetyFloors.ExtractFloors"/>, <see cref="PackVersion.IsDowngrade"/>, and
/// <see cref="PackSafetyFloors.Weakened"/> — it does NOT reimplement floor comparison. Author-time guidance
/// and install-time enforcement therefore cannot silently diverge (an A4-drift class defect).
/// </para>
/// <para>
/// <b>Guidance, not authority (design §7.2).</b> The author only knows their OWN prior versions; a consumer
/// may carry a higher S-8 watermark. So this is BEST-EFFORT authoring guidance — the definitive S-8 gate is
/// at install (the installer, on the consumer's node). The UX must say so, not imply the author-side check
/// is the authority.
/// </para>
/// </remarks>
public static class PackAuthorFloorGuard
{
    /// <summary>
    /// Checks a candidate version against the author's prior lineage. <paramref name="priorFloors"/> is the
    /// author's last-exported floor set for this pack key (the same shape the install watermark tracks);
    /// <paramref name="candidateContents"/> is the new composition's canonicalized content. Returns the
    /// downgrade flag + the floor keys the candidate would weaken (empty ⇒ raise-only, clean).
    /// </summary>
    public static PackAuthorFloorGuardResult Check(
        IReadOnlyDictionary<string, int> priorFloors,
        string priorVersion,
        IReadOnlyList<PackContentItem> candidateContents,
        string candidateVersion)
    {
        ArgumentNullException.ThrowIfNull(priorFloors);
        ArgumentNullException.ThrowIfNull(candidateContents);

        // Reuse the installer's single-source extractor — never a second floor parser.
        var candidateFloors = PackSafetyFloors.ExtractFloors(candidateContents);

        var isDowngrade = PackVersion.IsDowngrade(priorVersion, candidateVersion);
        var weakenedFloors = PackSafetyFloors.Weakened(priorFloors, candidateFloors);

        return new PackAuthorFloorGuardResult(isDowngrade, weakenedFloors);
    }
}

/// <summary>
/// The author-side floor-guard finding (Pack Composer design §7.1). <see cref="IsClean"/> is the
/// raise-only happy path; otherwise the author is composing a pack the install-side S-8 gate would refuse
/// (a downgrade and/or a weakened floor) — surface it as a plain-language authoring warning.
/// </summary>
/// <param name="IsDowngrade">True iff the candidate version precedes the author's prior version (S-8
/// monotonic-version refusal at install).</param>
/// <param name="WeakenedFloors">The floor keys the candidate lowers below the prior version (S-8 floor-weaken
/// refusal at install). Empty ⇒ no floor is weakened.</param>
public sealed record PackAuthorFloorGuardResult(
    bool IsDowngrade,
    IReadOnlyList<string> WeakenedFloors)
{
    /// <summary>True iff the candidate raises-or-holds (no downgrade, no weakened floor) — the clean path.</summary>
    public bool IsClean => !IsDowngrade && WeakenedFloors.Count == 0;
}
