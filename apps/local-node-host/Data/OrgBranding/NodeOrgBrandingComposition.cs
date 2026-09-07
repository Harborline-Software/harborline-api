using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.LocalNodeHost.OrgBranding;

namespace Harborline.Api.LocalNodeHost.Data.OrgBranding;

/// <summary>
/// Composition helper (tenant-branding slice T1) that registers the node-local org-branding services: the
/// durable EF <see cref="IOrgBrandingStore"/> over the recoverable SQLCipher <c>local-node.db</c> (its
/// <c>NodeLocalOrgBrandingDbContext</c> factory is registered by <c>AddSqlCipherLocalNodeDbContext</c>) and
/// the <see cref="IOrgBrandingResolver"/> that applies the fallback ladder for the active tenant. Mirrors
/// <c>NodeCalendarComposition</c>.
/// </summary>
public static class NodeOrgBrandingComposition
{
    /// <summary>Registers the node-local org-branding store + resolver (singletons, last-wins).</summary>
    public static IServiceCollection AddNodeOrgBranding(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IOrgBrandingStore, NodeEfOrgBrandingStore>();
        services.AddSingleton<IOrgBrandingResolver, OrgBrandingResolver>();
        return services;
    }
}
