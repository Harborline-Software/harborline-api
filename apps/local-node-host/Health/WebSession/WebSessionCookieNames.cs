using Microsoft.AspNetCore.Http;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>Frozen ADR 0160 hosted-web cookie audiences and their shared security posture.</summary>
internal static class WebSessionCookieNames
{
    internal const string Challenge = "__Host-hl-challenge";
    internal const string Selected = "__Host-hl-selected";
    internal const string Installation = "__Host-hl-install";
    internal const string AnonymousAntiforgery = "__Host-hl-antiforgery";

    internal static CookieOptions For(DateTimeOffset expiresAt) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        Expires = expiresAt,
        IsEssential = true,
    };

    internal static CookieOptions ForDeletion() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
    };
}
