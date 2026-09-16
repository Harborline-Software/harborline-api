using System.Text.Json;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.Blocks.AccessGrant;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed partial class AccessAdministrationPreloadTests
{
    [Fact]
    public async Task Forbidden_auditor_offer_refuses_activation_without_changing_platform()
    {
        await _platformPreload.PreloadAsync(Tenant, CancellationToken.None);
        var prior = _store.GetActive(Tenant, PlatformPackPreloadHostedService.PackKey)!;
        var viewsBefore = JsonSerializer.Serialize(await _views.ListDefinitionsAsync(Tenant.Value));
        var source = PlatformPackPreloadHostedService.ReadExportRequest(_signer.Signer.IssuerId.ToBase64Url());
        var replacement = source with
        {
            Version = "1.3.1",
            Contents = source.Contents.Append(new PackContentSource(
                "platform.binding.forbidden-auditor", PackContentKind.AuthorizationCapabilityBinding, "1.0.0",
                JsonSerializer.SerializeToNode(new
                {
                    operation = "audit:read", scope = "/", offeredRoles = new[] { "platform/auditor" },
                })!)).ToArray(),
        };
        var context = new PackInstallContext(Tenant, TrustingTheNodeKey(), PackRevocationList.Empty,
            DateTimeOffset.UtcNow, PackInstallRoutes.RevocationMaxAge,
            Principal: AccessGrantAuthorizationSeed.NodeOperatorPrincipal);
        Assert.True(_installer.Install(await ExportAsync(replacement), context).Installed);
        var activation = _installer.Activate(context, replacement.Key, replacement.Version);
        Assert.False(activation.Activated);
        Assert.Equal(PackSeedProjector.AccessProjectionRefusedCode, activation.Refusal!.Code);
        Assert.Equal(prior, _store.GetActive(Tenant, replacement.Key));
        Assert.Equal(viewsBefore, JsonSerializer.Serialize(await _views.ListDefinitionsAsync(Tenant.Value)));
        Assert.Equal(PackLifecycleState.Draft, _store.GetVersion(Tenant, replacement.Key, replacement.Version)!.Lifecycle);
    }

    [Fact]
    public async Task Replacement_late_view_refusal_keeps_old_active_catalogue_and_publishes_no_early_view()
    {
        await PreloadPlatformThenAccessAsync();
        var prior = _store.GetActive(Tenant, AccessAdministrationPreloadHostedService.PackKey)!;
        var viewsBefore = JsonSerializer.Serialize(await _views.ListDefinitionsAsync(Tenant.Value, CancellationToken.None));
        var source = AccessAdministrationPreloadHostedService.ReadExportRequest(_signer.Signer.IssuerId.ToBase64Url());
        var holder = source.Contents.Single(item => item.Key == "access.holders");
        var early = holder.Content.DeepClone();
        early["key"] = "m6.early-view";
        var late = holder.Content.DeepClone();
        late["key"] = "m6.late-refusal";
        late["viewKind"] = "views.not-registered";
        var replacement = source with
        {
            Version = "1.1.2-atomicity-probe.0",
            Contents = source.Contents.Concat([
                new PackContentSource("m6.early-view", holder.Kind, holder.Version, early),
                new PackContentSource("m6.late-refusal", holder.Kind, holder.Version, late),
            ]).ToArray(),
        };
        var bytes = await ExportAsync(replacement);
        var context = new PackInstallContext(Tenant, TrustingTheNodeKey(), PackRevocationList.Empty,
            DateTimeOffset.UtcNow, PackInstallRoutes.RevocationMaxAge,
            Principal: AccessGrantAuthorizationSeed.NodeOperatorPrincipal);
        Assert.True(_installer.Install(bytes, context).Installed);
        var activation = _installer.Activate(context, replacement.Key, replacement.Version);
        Assert.False(activation.Activated);
        Assert.False(activation.Projected);
        Assert.Equal(PackInstallCodes.ActivateProjectionRefused, activation.Error);
        Assert.Equal("pack.view-definition.malformed", activation.Refusal!.Code);
        var result = Assert.IsType<PackSeedProjectionSummary>(activation.ProjectionResult);
        Assert.Contains(result.Refusals, refusal => refusal.ContentKey == "m6.late-refusal"
            && refusal.Code == "pack.view-definition.malformed");
        Assert.Equal(viewsBefore, JsonSerializer.Serialize(await _views.ListDefinitionsAsync(Tenant.Value, CancellationToken.None)));
        Assert.Equal(prior, _store.GetActive(Tenant, replacement.Key));
        Assert.Null(await _views.GetDefinitionAsync(Tenant.Value, "m6.early-view", "1.0.0", CancellationToken.None));
        Assert.NotEqual(PackLifecycleState.Active, _store.GetVersion(Tenant, replacement.Key, replacement.Version)!.Lifecycle);
    }
}
