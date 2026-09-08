using System.Collections.ObjectModel;

namespace Harborline.Api.Foundation.IdentityAtlas.Permissions;

/// <summary>The exhaustive sealed platform role seed.</summary>
public static class PlatformRoleVocabulary
{
    private static readonly IReadOnlyCollection<RoleReference> DefinedRoles =
        new ReadOnlyCollection<RoleReference>(
            [RoleReference.Administrator, RoleReference.Auditor]);

    /// <summary>Administrator and Auditor, the only platform-owned roles.</summary>
    public static IReadOnlyCollection<RoleReference> Roles => DefinedRoles;

    /// <summary>Returns whether a qualified role is in the sealed platform seed.</summary>
    public static bool Contains(RoleReference role) => DefinedRoles.Contains(role);
}
