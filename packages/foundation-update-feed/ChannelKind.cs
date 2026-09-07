namespace Harborline.Api.Foundation.UpdateFeed;

/// <summary>
/// The POSTURE a consumer applies to a channel past its signed <c>validUntil</c> deadline (design
/// note §2.1 / F1 — "posture past <c>validUntil</c> is channel-TYPED"). This is <b>consumer-side
/// config</b>, not a field in the feed: the SAME signed bytes serve an online channel, an intranet
/// mirror, or a USB sideload (that is what makes a mirror a dumb copy, §2.3) — only the channel-table
/// row that names the channel knows which kind it is.
/// </summary>
public enum ChannelKind
{
    /// <summary>An online channel (the official CDN, an in-country mirror, an intranet marketplace).
    /// Past <c>validUntil</c> it goes <b>fail-closed for the install path</b> — a stale online channel
    /// could be a freeze attack (§7.2), so staleness is not tolerated.</summary>
    Online = 0,

    /// <summary>A deliberately offline / air-gapped sideload channel (a local filesystem / USB path).
    /// It is <b>expected-stale</b> and must NOT lock out past <c>validUntil</c> (else air-gapped
    /// installs brick — the local-first counter-pressure, §2.1). Its revocation freshness is inherently
    /// bounded by the media date; that is stated, not hidden.</summary>
    Sideload = 1,
}
