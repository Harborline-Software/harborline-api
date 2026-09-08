using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.AccessGrant;

/// <summary>
/// (L675) What a pack may carry about access, and what it may never carry. A pack builder ships the
/// default ROLE NAMES and the default CAPABILITY BINDINGS — the publisher ceiling for an operation —
/// and never a GRANT: a grant is what a tenant has, decided by a granter at an instant with a reason,
/// not a fact a package can know. The prohibition is enforced twice over, because a kind check alone
/// is a rename away from being bypassed:
/// <list type="bullet">
/// <item>BY KIND — <c>PackContentKind</c> has no grant member, and the codec refuses any item whose
/// declared kind is not a defined member, so "grant" is unspeakable as a kind.</item>
/// <item>BY SHAPE — <see cref="IsGrantInstance"/> reads the item body itself, at any depth, so a grant
/// smuggled inside a form, a view, a role definition or a binding is refused just the same.</item>
/// </list>
/// </summary>
public static class PackAuthorizationContentAdmission
{
    /// <summary>The stable refusal code for a pack item that is (or hides) a grant instance.</summary>
    public const string GrantInstanceRefusedCode = "pack.projection.grant_instance_refused";

    /// <summary>The stable refusal code for a malformed role-definition item.</summary>
    public const string RoleDefinitionMalformedCode = "pack.projection.role_definition_malformed";

    /// <summary>The stable refusal code for a malformed capability-binding item.</summary>
    public const string CapabilityBindingMalformedCode = "pack.projection.capability_binding_malformed";

    /// <summary>Property names that only a grant instance carries.</summary>
    private static readonly HashSet<string> GrantMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "grantId", "grantedBy", "grantedAt", "granterKind", "grantee", "revokedBy", "revokedAt",
        "accessGrant", "grants",
    };

    /// <summary>Property names that identify WHO holds something.</summary>
    private static readonly HashSet<string> HolderMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "subject", "holder", "grantee", "granteeActorId",
    };

    /// <summary>Property names that identify WHICH role is held.</summary>
    private static readonly HashSet<string> RoleMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "role", "roles", "roleReference",
    };

    /// <summary>
    /// Whether the item body is, or contains at any depth, a grant instance: an object carrying a
    /// grant-only property, or one that names both a holder and a role (the irreducible shape of
    /// "this person holds this role"). Declarative pack content never does either.
    /// </summary>
    public static bool IsGrantInstance(JsonNode? content) => content switch
    {
        JsonObject o =>
            o.Any(p => GrantMarkers.Contains(p.Key))
            || (o.Any(p => HolderMarkers.Contains(p.Key)) && o.Any(p => RoleMarkers.Contains(p.Key)))
            || o.Any(p => IsGrantInstance(p.Value)),
        JsonArray a => a.Any(IsGrantInstance),
        _ => false,
    };

    /// <summary>
    /// Parses a pack role-definition item into an unsealed, package-owned <c>tax.roles</c> entry. A
    /// pack cannot name a platform role: <see cref="RoleDefinition"/>'s own constructor refuses a
    /// platform-vocabulary name that is not sealed and platform-owned, and this path never seals.
    /// </summary>
    public static bool TryParseRoleDefinition(
        string publisherPackageId,
        JsonNode? content,
        out RoleDefinition? role)
    {
        role = null;
        if (content is not JsonObject o) return false;
        var name = Text(o, "role") ?? Text(o, "name");
        var displayName = Text(o, "displayName");
        if (name is null || displayName is null) return false;
        try
        {
            role = RoleDefinition.CreatePackageRole(
                new RoleDefinitionId(Derive("harborline.pack-role/v1", publisherPackageId, name)),
                name,
                displayName,
                publisherPackageId);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Parses a pack capability-binding item into the authorization capability definition whose
    /// offered roles ARE the publisher ceiling for that operation. The definition then passes the
    /// ordinary <see cref="AuthorizationDefinitionAdmission"/>, so a pack's ceiling is bounded by
    /// every platform rule that admission holds — including L628: no pack may offer the Auditor.
    /// </summary>
    public static bool TryParseCapabilityBinding(
        string publisherPackageId,
        JsonNode? content,
        out AuthorizationCapabilityDefinition? definition)
    {
        definition = null;
        if (content is not JsonObject o) return false;
        var operationValue = Text(o, "operation");
        if (operationValue is null || o["offeredRoles"] is not JsonArray offered) return false;
        try
        {
            var operation = AuthorizationOperation.Parse(operationValue);
            var roles = new List<RoleReference>(offered.Count);
            foreach (var entry in offered)
            {
                var qualified = entry?.GetValue<string>();
                var separator = qualified?.IndexOf('/') ?? -1;
                if (qualified is null || separator <= 0 || separator == qualified.Length - 1) return false;
                roles.Add(new RoleReference(qualified[..separator], qualified[(separator + 1)..]));
            }

            var scope = ScopeExpression.Parse(Text(o, "scope") ?? "/");
            definition = new AuthorizationCapabilityDefinition(
                new AuthorizationCapabilityDefinitionId(
                    Derive("harborline.pack-capability-binding/v1", publisherPackageId, operation.Value)),
                publisherPackageId,
                Revision: 1,
                operation,
                new PermissionAtom(operation, scope),
                RoleBindingSet.From(roles));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string? Text(JsonObject o, string property) =>
        o[property] is JsonValue value && value.TryGetValue<string>(out var text)
        && !string.IsNullOrWhiteSpace(text) ? text : null;

    /// <summary>Derives a stable identity from the publisher and the name, so re-projecting the same
    /// pack item on every boot addresses the same row rather than minting a second one.</summary>
    private static Guid Derive(string domain, string publisherPackageId, string name) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{domain}:{publisherPackageId}:{name}")).AsSpan(0, 16));
}
