using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Navigation;

namespace Harborline.Api.Foundation.Packs.Install.Admission;

/// <summary>
/// Shared install stage for navigation declarations. Structural parsing and role admission happen before
/// the immutable seed is committed; the active read projection reuses the same parser rather than deciding
/// the declaration a second way.
/// </summary>
public static class PackNavigationContentAdmission
{
    public static IReadOnlyList<PackAdmissionRefusal> Validate(
        IReadOnlyList<PackComposedItem> composed,
        TenantId tenant,
        IRoleGateAdmission? roleGateAdmission = null)
    {
        ArgumentNullException.ThrowIfNull(composed);
        var refusals = new List<PackAdmissionRefusal>();
        foreach (var item in composed.Where(content => content.Kind == PackContentKind.NavWorkspaceConfig))
        {
            if (!PackNavigationDeclarationParser.DeclaresNavigation(item.CanonicalJson))
                continue;
            var parsed = PackNavigationDeclarationParser.Parse(item.CanonicalJson);
            if (!parsed.Succeeded)
            {
                refusals.Add(new PackAdmissionRefusal(
                    item.Key,
                    parsed.Refusal!.Code,
                    parsed.Refusal.Message));
                continue;
            }

            var gates = PackNavigationDeclarationParser.RoleGates(parsed.Declaration!);
            if (gates.Count == 0)
                continue;
            if (roleGateAdmission is null)
            {
                refusals.Add(new PackAdmissionRefusal(
                    item.Key,
                    PackNavigationAdmissionCodes.RoleGateNotWired,
                    "This navigation declaration carries role-gated actions, but no shared role gate is wired."));
                continue;
            }

            var definition = new RoleGatedDefinition(
                "navigation",
                item.Key,
                item.Version,
                new RoleGatedDefinitionOwner(RoleGatedDefinitionOwnerKind.VendorPackage, tenant, item.PackageKey),
                gates);
            try
            {
                roleGateAdmission.AdmitAsync(definition).AsTask().GetAwaiter().GetResult();
            }
            catch (RoleGateAdmissionException exception)
            {
                refusals.Add(new PackAdmissionRefusal(item.Key, exception.Code, exception.Message));
            }
        }
        return refusals;
    }
}
