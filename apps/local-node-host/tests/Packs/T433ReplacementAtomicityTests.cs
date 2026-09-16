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
            Version = "1.1.2",
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
        var result = Assert.IsType<PackSeedProjectionSummary>(activation.ProjectionResult);
        Assert.Contains(result.Refusals, refusal => refusal.ContentKey == "m6.late-refusal");
        Assert.Equal(viewsBefore, JsonSerializer.Serialize(await _views.ListDefinitionsAsync(Tenant.Value, CancellationToken.None)));
        Assert.Equal(prior, _store.GetActive(Tenant, replacement.Key));
        Assert.Null(await _views.GetDefinitionAsync(Tenant.Value, "m6.early-view", "1.0.0", CancellationToken.None));
        Assert.NotEqual(PackLifecycleState.Active, _store.GetVersion(Tenant, replacement.Key, replacement.Version)!.Lifecycle);
    }
}
