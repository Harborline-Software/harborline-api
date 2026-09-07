using Microsoft.AspNetCore.Http;

using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;

namespace Harborline.Api.LocalNodeHost.Tests.Identity.Mtw00CRedFixtures;

/// <summary>
/// Red fixtures for cookie-audience separation (feeds MTW-01C: three distinct `__Host-` cookie
/// audiences — account challenge, selected user, installation session — with an exact
/// endpoint-to-cookie matrix and NO fallback after a miss or refusal; tooling is a separate
/// non-cookie principal). Today one shared cookie/bearer serves every audience (Stage-0 gap
/// "Challenge, selected-user, installation, and tooling audiences are not separated"), so each
/// invariant below is red.
/// </summary>
internal static class CookieAudienceSeparationRedFixtures
{
    private const string Domain = "cookie-audience-separation";

    internal static IEnumerable<RedFixture> Fixtures()
    {
        yield return Production.Fixture(
            Domain,
            "selected_extractor_rejects_challenge_audience_handle",
            authority: "web-cookie-audience-isolation",
            reason:
                "the selected-session extractor must reject a handle minted for the account-challenge " +
                "audience; today one shared web token serves every audience.",
            restartShaped: true,
            authorityType: typeof(SharedHostedWebApp),
            proof: CookieAudienceAuthorityProof.ProveChallengeCannotBecomeSelected);

        yield return Production.Fixture(
            Domain,
            "no_audience_fallback_after_miss",
            authority: "no-audience-fallback-after-miss",
            reason:
                "after the selected-user cookie is absent, no extractor may fall back to the challenge " +
                "or installation audience.",
            restartShaped: false,
            authorityType: typeof(SharedHostedWebApp),
            proof: CookieAudienceAuthorityProof.ProveForeignCookieBlocksLegacyFallback);

        yield return Production.Fixture(
            Domain,
            "no_audience_fallback_after_refusal",
            authority: "no-audience-fallback-after-refusal",
            reason:
                "an expired or refused handle in one audience must not trigger a second-audience " +
                "extraction attempt.",
            restartShaped: false,
            authorityType: typeof(SharedHostedWebApp),
            proof: CookieAudienceAuthorityProof.ProveSelectedCookieRemainsAuthoritative);

        yield return Production.Fixture(
            Domain,
            "installation_audience_cannot_authorize_business_route",
            authority: "installation-audience-scope-fence",
            reason:
                "an installation-session cookie must not authorize any tenant business route; " +
                "installation authority is confined to installation commands.",
            restartShaped: false,
            authorityType: typeof(SharedHostedWebApp),
            proof: CookieAudienceAuthorityProof.ProveInstallationCookieIsForeignToBusinessRoutes);
    }
}

/// <summary>Mutation-sensitive proofs over the production listener's cookie-audience authority.</summary>
internal static class CookieAudienceAuthorityProof
{
    internal static void ProveChallengeCannotBecomeSelected()
    {
        var request = RequestWithCookies(
            $"{WebSessionCookieNames.Challenge}=challenge-handle");

        Assert.Equal(
            WebCookieAudienceDisposition.Foreign,
            SharedHostedWebApp.ClassifyWebCookieAudience(request));
    }

    internal static void ProveForeignCookieBlocksLegacyFallback()
    {
        var request = RequestWithCookies(
            $"{WebSessionCookieNames.Challenge}=challenge-handle; " +
            $"{NodeWebSessionAuthority.SessionCookieName}=legacy-live");

        Assert.Equal(
            WebCookieAudienceDisposition.Foreign,
            SharedHostedWebApp.ClassifyWebCookieAudience(request));
    }

    internal static void ProveSelectedCookieRemainsAuthoritative()
    {
        var request = RequestWithCookies(
            $"{WebSessionCookieNames.Selected}=expired-selected; " +
            $"{NodeWebSessionAuthority.SessionCookieName}=legacy-live; " +
            $"{WebSessionCookieNames.Installation}=installation-live");

        Assert.Equal(
            WebCookieAudienceDisposition.Selected,
            SharedHostedWebApp.ClassifyWebCookieAudience(request));
    }

    internal static void ProveInstallationCookieIsForeignToBusinessRoutes()
    {
        var request = RequestWithCookies(
            $"{WebSessionCookieNames.Installation}=installation-handle; " +
            $"{NodeWebSessionAuthority.SessionCookieName}=legacy-live");

        Assert.Equal(
            WebCookieAudienceDisposition.Foreign,
            SharedHostedWebApp.ClassifyWebCookieAudience(request));
    }

    private static HttpRequest RequestWithCookies(string cookieHeader)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = cookieHeader;
        return context.Request;
    }
}
