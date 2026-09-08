namespace Harborline.Api.Contracts;

public sealed record RoleReferenceDto(string Vocabulary, string Name);
public sealed record RoleOwnerDto(string Kind, string OwnerId);
public sealed record RoleDefinitionDto(Guid RoleDefinitionId, RoleReferenceDto Role,
    string DisplayName, RoleOwnerDto Owner, bool IsSealed);

public sealed record PermissionAtomDto(string Operation, string ScopeType, string ScopeValue);
public sealed record AuthorizationDefinitionDto(Guid DefinitionId, string PublisherPackageId,
    long DefinitionRevision, PermissionAtomDto Atom,
    IReadOnlyList<RoleReferenceDto> OfferedRoles,
    AuthorizationBindingDto Binding);
public sealed record AuthorizationBindingDto(long Revision,
    IReadOnlyList<RoleReferenceDto> EffectiveRoles, string? Warning);
public sealed record NarrowAuthorizationBindingRequest(
    IReadOnlyList<RoleReferenceDto> SelectedRoles, string Reason);
public sealed record NarrowAuthorizationBindingResponse(
    Guid DefinitionId, long Revision, IReadOnlyList<RoleReferenceDto> EffectiveRoles,
    string? Warning, string ChangedBy, DateTimeOffset ChangedAt, string Reason, Guid? AuditId = null);

public sealed record StandingFieldCatalogueDto(
    string Field, IReadOnlyList<string> CarryingRecordTypes);
public sealed record StandingDefinitionCatalogueDto(
    string RuleId, string RuleVersion, string Standing, string DeclaredRecordType,
    IReadOnlyList<StandingFieldCatalogueDto> Fields);
