namespace Harborline.Api.Contracts;

/// <summary>References a role by vocabulary and name.</summary>
/// <param name="Vocabulary">The role vocabulary.</param><param name="Name">The role name.</param>
public sealed record RoleReferenceDto(string Vocabulary, string Name);
/// <summary>Identifies the kind and identifier of a role owner.</summary>
/// <param name="Kind">The owner kind.</param><param name="OwnerId">The owner identifier.</param>
public sealed record RoleOwnerDto(string Kind, string OwnerId);
/// <summary>Describes a role definition and whether it is sealed.</summary>
/// <param name="RoleDefinitionId">The role definition identifier.</param><param name="Role">The role reference.</param><param name="DisplayName">The display label.</param><param name="Owner">The owning principal.</param><param name="IsSealed">Whether the definition may no longer be changed.</param>
public sealed record RoleDefinitionDto(Guid RoleDefinitionId, RoleReferenceDto Role,
    string DisplayName, RoleOwnerDto Owner, bool IsSealed);

/// <summary>Describes one permission operation and its scope.</summary>
/// <param name="Operation">The permission operation.</param><param name="ScopeType">The scope category.</param><param name="ScopeValue">The scope value.</param>
public sealed record PermissionAtomDto(string Operation, string ScopeType, string ScopeValue);
/// <summary>Publishes an authorization definition and its current binding.</summary>
/// <param name="DefinitionId">The definition identifier.</param><param name="PublisherPackageId">The publishing package identifier.</param><param name="DefinitionRevision">The definition revision.</param><param name="Atom">The permission atom.</param><param name="OfferedRoles">The roles offered for binding.</param><param name="Binding">The current binding.</param>
public sealed record AuthorizationDefinitionDto(Guid DefinitionId, string PublisherPackageId,
    long DefinitionRevision, PermissionAtomDto Atom,
    IReadOnlyList<RoleReferenceDto> OfferedRoles,
    AuthorizationBindingDto Binding);
/// <summary>Represents the roles effective at a binding revision.</summary>
/// <param name="Revision">The binding revision.</param><param name="EffectiveRoles">The roles currently effective.</param><param name="Warning">An optional warning about the binding.</param>
public sealed record AuthorizationBindingDto(long Revision,
    IReadOnlyList<RoleReferenceDto> EffectiveRoles, string? Warning);
/// <summary>Requests narrowing an authorization binding to selected roles.</summary>
/// <param name="SelectedRoles">The roles to retain.</param><param name="Reason">The required explanation for the change.</param>
public sealed record NarrowAuthorizationBindingRequest(
    IReadOnlyList<RoleReferenceDto> SelectedRoles, string Reason);
/// <summary>Reports the resulting authorization binding and audit metadata.</summary>
/// <param name="DefinitionId">The affected definition.</param><param name="Revision">The resulting revision.</param><param name="EffectiveRoles">The roles now effective.</param><param name="Warning">An optional warning.</param><param name="ChangedBy">The actor that made the change.</param><param name="ChangedAt">The change timestamp.</param><param name="Reason">The recorded reason.</param><param name="AuditId">The optional audit record identifier.</param>
public sealed record NarrowAuthorizationBindingResponse(
    Guid DefinitionId, long Revision, IReadOnlyList<RoleReferenceDto> EffectiveRoles,
    string? Warning, string ChangedBy, DateTimeOffset ChangedAt, string Reason, Guid? AuditId = null);

/// <summary>Lists the record types that carry a standing field.</summary>
/// <param name="Field">The field name.</param><param name="CarryingRecordTypes">The record types carrying the field.</param>
public sealed record StandingFieldCatalogueDto(
    string Field, IReadOnlyList<string> CarryingRecordTypes);
/// <summary>Describes a standing rule and its declared fields.</summary>
/// <param name="RuleId">The rule identifier.</param><param name="RuleVersion">The rule version.</param><param name="Standing">The standing name.</param><param name="DeclaredRecordType">The declared record type.</param><param name="Fields">The catalogue of fields.</param>
public sealed record StandingDefinitionCatalogueDto(
    string RuleId, string RuleVersion, string Standing, string DeclaredRecordType,
    IReadOnlyList<StandingFieldCatalogueDto> Fields);
