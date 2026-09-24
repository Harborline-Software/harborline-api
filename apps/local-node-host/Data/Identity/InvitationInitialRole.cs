using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

internal static class InvitationInitialRole
{
    internal const string Default = "tax.roles/member";

    internal static RoleReference? Parse(string? value)
    {
        value ??= Default;
        var separator = value.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1 || value.IndexOf('/', separator + 1, StringComparison.Ordinal) >= 0
            || value.Any(char.IsWhiteSpace)) return null;
        return new RoleReference(value[..separator], value[(separator + 1)..]);
    }

    // Pin both vocabulary identity and effective offers: an outstanding invitation cannot silently
    // acquire a different role definition or a changed permission closure before acceptance.
    internal static string Digest(RoleDefinition definition, PermissionAtomSet permissions) =>
        InstallationAuditIntegrity.Hash("invitation-initial-role/v1", definition.Role.ToString(),
            definition.RoleDefinitionId.ToString(), definition.Owner.ToString()!,
            string.Join('\n', permissions.Atoms.Select(atom => $"{atom.Operation.Value}@{atom.Scope.Value}")
                .Order(StringComparer.Ordinal)));
}
