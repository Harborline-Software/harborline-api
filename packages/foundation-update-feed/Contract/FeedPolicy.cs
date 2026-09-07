using Harborline.Api.Foundation.Packs.Dcp;

namespace Harborline.Api.Foundation.UpdateFeed.Contract;

/// <summary>
/// The channel's signed compliance policy (ADR 0153 D3). The policy is never trusted as a
/// free-floating document: <see cref="ChannelIndex.FeedPolicyCid"/> binds its exact bytes into the
/// signed channel index, so it inherits that index's sequence and expiry fences.
/// </summary>
/// <param name="AllowedRegulatoryClasses">Publisher-signed DCP classes this feed may publish.</param>
/// <param name="SelfGoverned">Whether the feed owner supplies its own compliance governance.</param>
/// <param name="CounselRegisterRef">Stable reference to the counsel register used for the ceremony.</param>
public sealed record FeedPolicy(
    IReadOnlyList<RegulatoryClass> AllowedRegulatoryClasses,
    bool SelfGoverned,
    string CounselRegisterRef)
{
    /// <summary>The Harborline public-feed policy: General is the only default-cleared class.</summary>
    public static FeedPolicy HarborlinePublic(string counselRegisterRef) => new(
        [RegulatoryClass.General],
        SelfGoverned: false,
        counselRegisterRef);
}
