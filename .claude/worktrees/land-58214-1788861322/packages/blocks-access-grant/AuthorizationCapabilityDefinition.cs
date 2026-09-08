using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.AccessGrant;

/// <summary>Stable identity of a versioned authorization capability definition.</summary>
public readonly record struct AuthorizationCapabilityDefinitionId(Guid Value);

/// <summary>A code operation and scoped atom offered to explicit powerless role names.</summary>
public sealed record AuthorizationCapabilityDefinition(
    AuthorizationCapabilityDefinitionId DefinitionId,
    string PublisherPackageId,
    long Revision,
    AuthorizationOperation Operation,
    PermissionAtom Atom,
    RoleBindingSet OfferedRoles);

/// <summary>A bounded reason for changing a tenant authorization binding.</summary>
public readonly record struct BindingChangeReason
{
    /// <summary>Creates a validated reason code.</summary>
    public BindingChangeReason(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 100)
        {
            throw new ArgumentException("A binding change reason cannot exceed 100 characters.", nameof(value));
        }

        Value = value;
    }

    /// <summary>The stable reason code.</summary>
    public string Value { get; }
}

/// <summary>An append-only tenant selection revision for one definition.</summary>
public sealed record CapabilityRoleBindingRevision(
    TenantId TenantId,
    AuthorizationCapabilityDefinitionId DefinitionId,
    long Revision,
    RoleBindingSet SelectedRoles,
    ActorId ChangedBy,
    DateTimeOffset ChangedAt,
    BindingChangeReason Reason);

/// <summary>The committed binding revision, its effective intersection, and warning.</summary>
public sealed record BindingChangeResult(
    CapabilityRoleBindingRevision Revision,
    RoleBindingSet EffectiveRoles,
    BindingWarningCode? Warning);
