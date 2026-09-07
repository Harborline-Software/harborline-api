using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.OrgBranding;

/// <summary>
/// Default <see cref="IOrgBrandingResolver"/> — reads the ACTIVE tenant's <see cref="OrgBrandingProfile"/>
/// from <see cref="IOrgBrandingStore"/> and applies the fallback ladder (design §2.5). Resolution is keyed to
/// the active team-derived tenant via <see cref="NodeTenant.Resolve"/> — the SAME server-side tenant posture
/// as the other node-local routes — so a tenant NEVER supplies its own branding scope over the wire, and a
/// future active-org switch re-resolves branding automatically.
/// </summary>
public sealed class OrgBrandingResolver : IOrgBrandingResolver
{
    private readonly IOrgBrandingStore _store;
    private readonly IActiveTeamAccessor _activeTeam;

    /// <summary>Construct over the branding store + the active-team accessor.</summary>
    public OrgBrandingResolver(IOrgBrandingStore store, IActiveTeamAccessor activeTeam)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _activeTeam = activeTeam ?? throw new ArgumentNullException(nameof(activeTeam));
    }

    /// <inheritdoc />
    public async Task<ResolvedOrgBranding> ResolveActiveAsync(CancellationToken ct = default)
    {
        var tenant = NodeTenant.Resolve(_activeTeam);
        var profile = await _store.GetAsync(tenant, ct).ConfigureAwait(false);
        return Resolve(profile);
    }

    /// <summary>Apply the fallback ladder to a (possibly null) profile — the pure decision, exposed for tests.</summary>
    public static ResolvedOrgBranding Resolve(OrgBrandingProfile? profile)
    {
        var hasLightLogo = !string.IsNullOrEmpty(profile?.LogoRef);
        var hasDarkLogo = !string.IsNullOrEmpty(profile?.LogoDarkRef);
        var hasName = !string.IsNullOrWhiteSpace(profile?.DisplayName);

        // org logo -> org-name wordmark -> platform default (design §2.5).
        var source = hasLightLogo
            ? BrandingLogoSource.OrgLogo
            : hasName
                ? BrandingLogoSource.OrgNameWordmark
                : BrandingLogoSource.PlatformDefault;

        return new ResolvedOrgBranding(
            HasOrgIdentity: hasName || hasLightLogo,
            DisplayName: hasName ? profile!.DisplayName : null,
            LogoSource: source,
            HasLightLogo: hasLightLogo,
            HasDarkLogo: hasDarkLogo,
            AccentColor: profile?.AccentColor,
            AccentForeground: profile?.AccentForeground);
    }
}
