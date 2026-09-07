namespace Harborline.Api.Foundation.UpdateFeed;

/// <summary>
/// The feed ENVELOPE contract version (design note §2.5). Every feed document carries
/// <c>feedFormat</c>; a reader <b>refuses a <c>feedFormat</c> it does not understand</b> — fail-closed,
/// honest message, never a guess at a newer shape. Because a channel cannot serve format N until
/// deployed nodes understand N, format bumps LAG node capability (the reader's capability gates the
/// writer's format — the same schema-epoch discipline as ADR 0147 D4).
/// </summary>
public static class FeedFormats
{
    /// <summary>The only format this build understands. A document declaring any other value is
    /// refused (see <see cref="Verify.FeedVerifyCodes.UnsupportedFeedFormat"/>).</summary>
    public const int V1 = 1;

    /// <summary>True iff <paramref name="feedFormat"/> is a version this build can read.</summary>
    public static bool IsSupported(int feedFormat) => feedFormat == V1;
}
