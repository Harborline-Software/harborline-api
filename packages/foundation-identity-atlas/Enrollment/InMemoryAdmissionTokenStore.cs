using System;
using System.Collections.Generic;

namespace Harborline.Api.Foundation.IdentityAtlas.Enrollment;

/// <summary>
/// The v1 in-memory <see cref="IAdmissionTokenStore"/> — process-local single-use + TTL enforcement for invite
/// admission tokens (enrollment Phase B). Thread-safe by a single lock (admission is low-frequency; no contention
/// concern). The durable, synced store replaces this behind the same interface when the roster doctype persists
/// (survey #1275 §4).
/// </summary>
/// <remarks>
/// Single-use is enforced by REMOVING the token on a successful redeem (so a replay sees
/// <see cref="RedeemOutcome.UnknownToken"/>) AND tracking redeemed ids so a replay of a still-present-but-already-
/// consumed id reports <see cref="RedeemOutcome.AlreadyRedeemed"/> rather than a misleading "unknown". TTL is
/// checked at redeem time against the caller-supplied <c>now</c> (testable clock; no ambient time).
/// </remarks>
public sealed class InMemoryAdmissionTokenStore : IAdmissionTokenStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AdmissionToken> _issued = new(StringComparer.Ordinal);
    private readonly HashSet<string> _redeemed = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void Issue(AdmissionToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(token.TokenId);
        lock (_gate)
        {
            _issued[token.TokenId] = token;
        }
    }

    /// <inheritdoc />
    public RedeemResult Redeem(string tokenId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenId);
        lock (_gate)
        {
            if (!_issued.TryGetValue(tokenId, out var token))
            {
                // Either never issued, or already consumed-and-removed: distinguish a replay from an unknown id.
                return _redeemed.Contains(tokenId) ? RedeemResult.Replayed() : RedeemResult.Unknown();
            }
            if (token.IsExpired(now))
            {
                // Expired: purge it (it can never succeed) but it was NOT a successful single-use, so it is not
                // recorded as redeemed.
                _issued.Remove(tokenId);
                return RedeemResult.Expired();
            }
            // Success — consume single-use: remove from issued + record the id as redeemed so a replay reports
            // AlreadyRedeemed.
            _issued.Remove(tokenId);
            _redeemed.Add(tokenId);
            return RedeemResult.Ok(token);
        }
    }
}
