using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.OrgBranding;

namespace Harborline.Api.LocalNodeHost.Data.OrgBranding;

/// <summary>
/// The durable EF/SQLite <see cref="IOrgBrandingStore"/> (tenant-branding slice T1) — persists one
/// <see cref="OrgBrandingProfile"/> per tenant in the SAME SQLCipher <c>local-node.db</c> file (via
/// <see cref="NodeLocalOrgBrandingDbContext"/>) as the other node-local doctypes, tenant-scoped +
/// cross-tenant isolated. Mirrors the <c>NodeEfCalendarEventStore : ICalendarEventStore</c> precedent.
/// </summary>
public sealed class NodeEfOrgBrandingStore : IOrgBrandingStore
{
    private readonly IDbContextFactory<NodeLocalOrgBrandingDbContext> _contextFactory;

    /// <summary>Construct bound to the org-branding context factory (the SQLCipher file the store lives in).</summary>
    public NodeEfOrgBrandingStore(IDbContextFactory<NodeLocalOrgBrandingDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    /// <inheritdoc />
    public async Task<OrgBrandingProfile?> GetAsync(TenantId tenant, CancellationToken ct = default)
    {
        var tenantId = tenant.Value;
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await ctx.OrgBranding
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId, ct)
            .ConfigureAwait(false);
        return row is null ? null : ToProfile(row);
    }

    /// <inheritdoc />
    public async Task UpsertAsync(OrgBrandingProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var existing = await ctx.OrgBranding
            .FirstOrDefaultAsync(r => r.TenantId == profile.TenantId, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            ctx.OrgBranding.Add(ToRow(profile));
        }
        else
        {
            existing.DisplayName = profile.DisplayName;
            existing.LogoRef = profile.LogoRef;
            existing.LogoDarkRef = profile.LogoDarkRef;
            existing.AccentColor = profile.AccentColor;
            existing.AccentForeground = profile.AccentForeground;
            existing.UpdatedAt = profile.UpdatedAt;
            existing.UpdatedBy = profile.UpdatedBy;
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static OrgBrandingProfile ToProfile(OrgBrandingRow r) => new(
        TenantId: r.TenantId,
        DisplayName: r.DisplayName,
        LogoRef: r.LogoRef,
        LogoDarkRef: r.LogoDarkRef,
        AccentColor: r.AccentColor,
        AccentForeground: r.AccentForeground,
        UpdatedAt: r.UpdatedAt,
        UpdatedBy: r.UpdatedBy);

    private static OrgBrandingRow ToRow(OrgBrandingProfile p) => new()
    {
        TenantId = p.TenantId,
        DisplayName = p.DisplayName,
        LogoRef = p.LogoRef,
        LogoDarkRef = p.LogoDarkRef,
        AccentColor = p.AccentColor,
        AccentForeground = p.AccentForeground,
        UpdatedAt = p.UpdatedAt,
        UpdatedBy = p.UpdatedBy,
    };
}
