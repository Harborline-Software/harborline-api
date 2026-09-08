
namespace Harborline.Api.Blocks.Assets.Registry.Services;

/// <summary>
/// Internal guard enforcing that Asset-Type-System operations always run against a real tenant.
/// The system / default <see cref="TenantId"/> (per <see cref="TenantId.IsSystemSentinel"/>, which
/// is also <c>true</c> for a default-constructed value) is rejected — the Wave-1 registry, edge
/// store, condition store, and audit log all fail closed on the sentinel (ADR 0101 Rev 3.1 / A5a,
/// mirroring the concrete Asset domain's <c>TenantGuard</c>).
/// </summary>
internal static class RegistryTenantGuard
{
    /// <summary>
    /// Throws <see cref="ArgumentException"/> when <paramref name="tenant"/> is a system / default
    /// sentinel (fail-closed for the multi-tenant path).
    /// </summary>
    public static void Require(TenantId tenant)
    {
        if (tenant.IsSystemSentinel)
        {
            throw new ArgumentException(
                "Asset-Type-System operations require a real tenant; the system / default TenantId "
                + "sentinel is rejected (fail-closed multi-tenant isolation, ADR 0084 / 0101 Rev 3.1 A5a).",
                nameof(tenant));
        }
    }
}
