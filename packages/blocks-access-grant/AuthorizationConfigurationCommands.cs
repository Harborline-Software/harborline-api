using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.AccessGrant;

/// <summary>Base command for the one authorization configuration write path.</summary>
public abstract record AuthorizationConfigurationCommand;

/// <summary>Installs the first revision of an authorization definition.</summary>
public sealed record InstallAuthorizationDefinition(
    AuthorizationCapabilityDefinition Definition,
    TenantId? DeclaringTenantId = null) : AuthorizationConfigurationCommand;

/// <summary>Replaces an authorization definition with its next narrow-only revision.</summary>
public sealed record ReplaceAuthorizationDefinition(
    AuthorizationCapabilityDefinition Definition,
    TenantId? DeclaringTenantId = null) : AuthorizationConfigurationCommand;

/// <summary>Appends a narrow-only tenant role selection.</summary>
public sealed record NarrowCapabilityRoleBinding(
    TenantId TenantId,
    AuthorizationCapabilityDefinitionId DefinitionId,
    RoleBindingSet SelectedRoles,
    ActorId ChangedBy,
    DateTimeOffset ChangedAt,
    BindingChangeReason Reason) : AuthorizationConfigurationCommand;

/// <summary>The result of a committed authorization configuration write.</summary>
public sealed record AuthorizationConfigurationWriteResult(
    AuthorizationCapabilityDefinition? Definition,
    BindingChangeResult? BindingChange,
    IReadOnlyList<string> Stages);
