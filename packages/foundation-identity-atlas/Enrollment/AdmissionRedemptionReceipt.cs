namespace Harborline.Api.Foundation.IdentityAtlas.Enrollment;

/// <summary>
/// Unforgeable-by-callers proof that the configured <see cref="IAdmissionTokenStore"/> successfully redeemed one
/// live, single-use admission token. The constructor is assembly-private: callers can obtain an instance only from
/// <see cref="AdmissionCoordinator.RedeemForAdmission"/>.
/// </summary>
/// <remarks>
/// This is the structural gate between token consumption and roster signing on the web-pairing path. The signing
/// method accepts this receipt rather than a caller-supplied token id, so no route or DI caller can reach that
/// method without first winning the token store's atomic redemption.
/// </remarks>
public sealed class AdmissionRedemptionReceipt
{
    internal AdmissionRedemptionReceipt(AdmissionToken token)
    {
        Token = token ?? throw new ArgumentNullException(nameof(token));
    }

    /// <summary>The exact successfully redeemed token id, safe for admission audit provenance.</summary>
    public string TokenId => Token.TokenId;

    /// <summary>The exact team decision carried by the successfully redeemed token.</summary>
    public TeamTrustAnchor Anchor => Token.Anchor;

    internal AdmissionToken Token { get; }
}

/// <summary>The result of attempting to obtain an <see cref="AdmissionRedemptionReceipt"/>.</summary>
/// <param name="Outcome">The token-store redemption outcome.</param>
/// <param name="Receipt">Present only when <paramref name="Outcome"/> is <see cref="RedeemOutcome.Accepted"/>.</param>
public sealed record AdmissionRedemptionResult(
    RedeemOutcome Outcome,
    AdmissionRedemptionReceipt? Receipt)
{
    /// <summary>True only when the store atomically consumed the token and issued the structural receipt.</summary>
    public bool Accepted => Outcome == RedeemOutcome.Accepted && Receipt is not null;
}
