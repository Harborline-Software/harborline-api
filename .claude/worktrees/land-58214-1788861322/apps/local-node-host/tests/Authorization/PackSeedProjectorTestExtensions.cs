using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

namespace Harborline.Api.LocalNodeHost.Data.PackProjection;

internal static class PackSeedProjectorTestExtensions
{
    internal static async Task<PackSeedProjectionSummary> ProjectActivePacksAsync(
        this IPackSeedProjector projector,
        TenantId tenant,
        CancellationToken cancellationToken = default)
    {
        var at = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var concrete = Assert.IsType<PackSeedProjector>(projector);
        var field = typeof(PackSeedProjector).GetField("_store",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var store = (IPackInstallStore)field.GetValue(concrete)!;
        var packs = store.ListInstalled(tenant)
            .Where(pack => pack.Lifecycle is PackLifecycleState.Active or PackLifecycleState.Inactive)
            .ToArray();
        var total = Empty();
        foreach (var pack in packs)
        {
            var scope = ScopeExpression.Parse($"/records/{pack.PackKey}");
            var principal = new ActorId("test-pack-operator");
            var request = new AuthorizationGateRequest(
                new PermissionAtom(AuthorizationOperation.Parse(Permission.PackagesOperate), scope),
                principal,
                tenant,
                new AuthorizationTarget("pack", pack.PackKey, scope),
                at);
            var decision = await TestAuthorization.AllowGate().DecideAsync(request, cancellationToken);
            var authority = new PackProjectionAuthority(
                decision, pack.PackKey, pack.Version, tenant, principal, at);
            total = Add(total, await projector.ProjectActivePacksAsync(authority, cancellationToken));
        }
        return total;
    }

    private static PackSeedProjectionSummary Empty() => new(0, 0, 0, 0, 0, 0, 0);

    private static PackSeedProjectionSummary Add(PackSeedProjectionSummary a, PackSeedProjectionSummary b) => new(
        a.AssetTypesSeeded + b.AssetTypesSeeded,
        a.AssetTypesAlreadyPresent + b.AssetTypesAlreadyPresent,
        a.AssetTypesSkippedInvalid + b.AssetTypesSkippedInvalid,
        a.FormDefinitionsDeferred + b.FormDefinitionsDeferred,
        a.TemplatesPublished + b.TemplatesPublished,
        a.TemplatesSkippedInvalid + b.TemplatesSkippedInvalid,
        a.OtherKindsSkipped + b.OtherKindsSkipped,
        a.AssetTypesOwnedByOtherPack + b.AssetTypesOwnedByOtherPack,
        a.AssetTypesContestedUnresolved + b.AssetTypesContestedUnresolved,
        a.FormDefinitionsPublished + b.FormDefinitionsPublished,
        a.FormDefinitionsAlreadyPresent + b.FormDefinitionsAlreadyPresent,
        a.FormDefinitionsSkippedInvalid + b.FormDefinitionsSkippedInvalid,
        a.FormDefinitionsOwnedByOtherPack + b.FormDefinitionsOwnedByOtherPack,
        a.FormDefinitionsContestedUnresolved + b.FormDefinitionsContestedUnresolved,
        a.WorkflowDefinitionsDeferred + b.WorkflowDefinitionsDeferred,
        a.WorkflowDefinitionsPublished + b.WorkflowDefinitionsPublished,
        a.WorkflowDefinitionsAlreadyPresent + b.WorkflowDefinitionsAlreadyPresent,
        a.WorkflowDefinitionsSkippedInvalid + b.WorkflowDefinitionsSkippedInvalid,
        a.WorkflowDefinitionsOwnedByOtherPack + b.WorkflowDefinitionsOwnedByOtherPack,
        a.WorkflowDefinitionsContestedUnresolved + b.WorkflowDefinitionsContestedUnresolved,
        a.AssetTypesRetracted + b.AssetTypesRetracted,
        a.FormDefinitionsRetracted + b.FormDefinitionsRetracted,
        a.WorkflowDefinitionsRetracted + b.WorkflowDefinitionsRetracted)
    {
        Refusals = a.Refusals.Concat(b.Refusals).ToArray(),
        RetractedByKind = a.RetractedByKind.Concat(b.RetractedByKind)
            .GroupBy(entry => entry.Key)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Value)),
        PlatformRefusals = a.PlatformRefusals.Concat(b.PlatformRefusals)
            .DistinctBy(item => (item.PackKey, item.Version, item.PlatformVersion))
            .ToArray(),
    };
}
