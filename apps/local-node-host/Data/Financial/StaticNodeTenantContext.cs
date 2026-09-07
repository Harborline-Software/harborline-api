using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// RETIRED as the node's production ambient tenant (ADR 0032 identity layer; survey #1275 §5).
/// The production <see cref="ITenantContext"/> the financial write services consult is now
/// <see cref="ActiveTeamTenantContext"/>, which derives the data <see cref="TenantId"/> from the
/// ACTIVE TEAM rather than the install-constant <c>"local"</c> literal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type still exists.</b> The production ambient <c>ITenantContext</c> is now
/// <see cref="ActiveTeamTenantContext"/> (registered by <c>AddNodeBillWrites</c> /
/// <c>AddNodeInvoiceWrites</c>), and ALL node-local routes + the accounting-summary service resolve
/// their data tenant from the active team via <see cref="NodeTenant"/> — the <c>"local"</c> literal is
/// fully retired from production tenant scoping. This type is kept ONLY as a fixed-"local"
/// <c>ITenantContext</c> for the handful of test fakes that want a deterministic tenant, plus the
/// <c>LocalTenantId</c> const those fakes reference. It is slated for deletion once those test fakes
/// migrate to the active-team-derived context. No production path pins <c>"local"</c> any longer.
/// </para>
/// <para>
/// Identity-only: <c>Harborline.Api.Foundation.MultiTenancy.ITenantContext</c> (the narrow
/// tenant-resolution interface, ADR 0008), NOT the authorization sum-facade.
/// </para>
/// </remarks>
public sealed class StaticNodeTenantContext : ITenantContext
{
    /// <summary>The install-constant local tenant id (the retired <c>"local"</c> sentinel the un-migrated read routes still pin).</summary>
    public const string LocalTenantId = "local";

    private static readonly TenantMetadata LocalTenant = new()
    {
        Id = new TenantId(LocalTenantId),
        Name = LocalTenantId,
    };

    /// <inheritdoc />
    public TenantMetadata? Tenant => LocalTenant;
}
