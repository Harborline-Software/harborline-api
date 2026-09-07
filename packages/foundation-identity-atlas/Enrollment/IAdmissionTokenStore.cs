using System;

namespace Harborline.Api.Foundation.IdentityAtlas.Enrollment;

/// <summary>
/// The issuing + single-use enforcement store for invite-code admission tokens (enrollment Phase B). An admin
/// MINTS a token here; a joiner REDEEMS it exactly once. The store is what makes the bearer token single-use:
/// a second redemption of the same token id — replay — is rejected.
/// </summary>
/// <remarks>
/// The store enforces the two bearer-credential bounds that live on the SERVER side of the exchange: single-use
/// (a token redeemed once is consumed) and TTL (a token past its <see cref="AdmissionToken.ExpiresAt"/> is dead
/// even if never redeemed). The third bound — minimal-default permissions — lives in the admit call, not here.
/// The v1 in-memory implementation (<see cref="InMemoryAdmissionTokenStore"/>) is process-local; the durable
/// synced store replaces it behind this interface when the roster doctype persists (survey #1275 §4).
/// </remarks>
public interface IAdmissionTokenStore
{
    /// <summary>Record a freshly-minted invite so it can later be redeemed exactly once.</summary>
    void Issue(AdmissionToken token);

    /// <summary>
    /// Atomically REDEEM a token by id: succeeds (returns the token) only if the id was issued, has not already
    /// been redeemed, and is not expired at <paramref name="now"/> — and CONSUMES it so a replay fails. Returns
    /// a <see cref="RedeemResult"/> describing the outcome (fail-closed: unknown / replayed / expired all reject).
    /// </summary>
    RedeemResult Redeem(string tokenId, DateTimeOffset now);
}

/// <summary>The outcome of an <see cref="IAdmissionTokenStore.Redeem"/> attempt.</summary>
/// <param name="Outcome">Why the redemption succeeded or failed.</param>
/// <param name="Token">The redeemed token when <see cref="RedeemOutcome.Accepted"/>; otherwise null.</param>
public sealed record RedeemResult(RedeemOutcome Outcome, AdmissionToken? Token)
{
    /// <summary>True iff the token was accepted (issued, unredeemed, unexpired) and is now consumed.</summary>
    public bool Accepted => Outcome == RedeemOutcome.Accepted;

    internal static RedeemResult Ok(AdmissionToken token) => new(RedeemOutcome.Accepted, token);
    internal static RedeemResult Unknown() => new(RedeemOutcome.UnknownToken, null);
    internal static RedeemResult Replayed() => new(RedeemOutcome.AlreadyRedeemed, null);
    internal static RedeemResult Expired() => new(RedeemOutcome.Expired, null);
}

/// <summary>Why a redemption succeeded or was refused (all non-Accepted values are fail-closed rejections).</summary>
public enum RedeemOutcome
{
    /// <summary>The token was issued, unredeemed, and unexpired — accepted and now consumed.</summary>
    Accepted,

    /// <summary>No such token id was ever issued (or it was already consumed and purged).</summary>
    UnknownToken,

    /// <summary>The token id was issued but has already been redeemed — a replay.</summary>
    AlreadyRedeemed,

    /// <summary>The token id was issued but is past its TTL.</summary>
    Expired,
}
