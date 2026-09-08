using Harborline.Api.LocalNodeHost.OrgBranding;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.OrgBranding;

/// <summary>
/// Tenant-branding slice T1 — the fallback ladder (design §2.5: org logo → org-name wordmark → platform
/// default). Exercises the pure <see cref="OrgBrandingResolver.Resolve"/> decision on each rung.
/// </summary>
public sealed class OrgBrandingResolverTests
{
    [Fact(DisplayName = "no profile -> platform default (never a broken image)")]
    public void NoProfile_PlatformDefault()
    {
        var r = OrgBrandingResolver.Resolve(null);

        Assert.False(r.HasOrgIdentity);
        Assert.Null(r.DisplayName);
        Assert.Equal(BrandingLogoSource.PlatformDefault, r.LogoSource);
        Assert.False(r.HasLightLogo);
        Assert.False(r.HasDarkLogo);
    }

    [Fact(DisplayName = "name only -> org-name wordmark (never an empty logo box)")]
    public void NameOnly_Wordmark()
    {
        var r = OrgBrandingResolver.Resolve(Profile(displayName: "Acme Property Co."));

        Assert.True(r.HasOrgIdentity);
        Assert.Equal("Acme Property Co.", r.DisplayName);
        Assert.Equal(BrandingLogoSource.OrgNameWordmark, r.LogoSource);
        Assert.False(r.HasLightLogo);
    }

    [Fact(DisplayName = "logo set -> org logo (logo wins the ladder over the wordmark)")]
    public void LogoSet_OrgLogo()
    {
        var r = OrgBrandingResolver.Resolve(Profile(displayName: "Acme", logoRef: "bafyLogo"));

        Assert.True(r.HasOrgIdentity);
        Assert.Equal(BrandingLogoSource.OrgLogo, r.LogoSource);
        Assert.True(r.HasLightLogo);
    }

    [Fact(DisplayName = "logo but no name still resolves org logo + has-identity")]
    public void LogoNoName_OrgLogo()
    {
        var r = OrgBrandingResolver.Resolve(Profile(logoRef: "bafyLogo"));

        Assert.True(r.HasOrgIdentity);
        Assert.Null(r.DisplayName);
        Assert.Equal(BrandingLogoSource.OrgLogo, r.LogoSource);
    }

    [Fact(DisplayName = "dark logo + accent surface through to the resolved view")]
    public void DarkLogoAndAccent_SurfaceThrough()
    {
        var r = OrgBrandingResolver.Resolve(Profile(
            displayName: "Acme", logoRef: "bafyLight", logoDarkRef: "bafyDark",
            accentColor: "#06489C", accentForeground: "#FFFFFF"));

        Assert.True(r.HasLightLogo);
        Assert.True(r.HasDarkLogo);
        Assert.Equal("#06489C", r.AccentColor);
        Assert.Equal("#FFFFFF", r.AccentForeground);
    }

    [Fact(DisplayName = "a blank display name is treated as unset (whitespace does not count)")]
    public void BlankName_TreatedAsUnset()
    {
        var r = OrgBrandingResolver.Resolve(Profile(displayName: "   "));

        Assert.False(r.HasOrgIdentity);
        Assert.Equal(BrandingLogoSource.PlatformDefault, r.LogoSource);
    }

    private static OrgBrandingProfile Profile(
        string? displayName = null, string? logoRef = null, string? logoDarkRef = null,
        string? accentColor = null, string? accentForeground = null) =>
        new(
            TenantId: "t-1",
            DisplayName: displayName,
            LogoRef: logoRef,
            LogoDarkRef: logoDarkRef,
            AccentColor: accentColor,
            AccentForeground: accentForeground,
            UpdatedAt: DateTimeOffset.UnixEpoch,
            UpdatedBy: "test");
}
