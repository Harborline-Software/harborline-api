using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Trust;
using NSubstitute;

namespace Harborline.Api.Foundation.Packs.Install;

internal static class PackInstallerTestExtensions
{
    internal static PackActivationOutcome Activate(
        this IPackInstaller installer,
        TenantId tenant,
        string packKey,
        string version,
        DateTimeOffset now,
        string? actingPrincipal = null) => installer.Activate(
        Context(tenant, now, actingPrincipal), packKey, version);

    internal static PackDeactivationOutcome Deactivate(
        this IPackInstaller installer,
        TenantId tenant,
        string packKey,
        string version,
        DateTimeOffset now,
        string? actingPrincipal = null) => installer.Deactivate(
        Context(tenant, now, actingPrincipal), packKey, version);

    private static PackInstallContext Context(TenantId tenant, DateTimeOffset now, string? principal) => new(
        tenant,
        Substitute.For<IPackTrustStore>(),
        Substitute.For<IPackRevocationList>(),
        now,
        TimeSpan.FromHours(1),
        Principal: principal);
}
