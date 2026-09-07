namespace Harborline.Api.Foundation.Authorization;

/// <summary>An opaque decision minted only by the authorization seed's bootstrap path.</summary>
internal sealed class PlatformBootstrapDecision
{
    private PlatformBootstrapDecision(AuthorizationDecision decision) => Decision = decision;

    internal AuthorizationDecision Decision { get; }

    internal static PlatformBootstrapDecision Mint(AuthorizationDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Verdict != AuthorizationVerdict.Allowed
            || !decision.Resolution.Any(step => step.Stage == AuthorizationResolutionStage.Bootstrap))
        {
            throw new ArgumentException("A platform bootstrap decision must carry bootstrap-stage evidence.", nameof(decision));
        }
        return new PlatformBootstrapDecision(decision);
    }
}
