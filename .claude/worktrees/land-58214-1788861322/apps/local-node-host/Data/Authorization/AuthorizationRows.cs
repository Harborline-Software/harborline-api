namespace Harborline.Api.LocalNodeHost.Data.Authorization;

public sealed class AuthorizationRoleRow
{
    public required string Vocabulary { get; set; }
    public required string RoleName { get; set; }
    public required string RoleDefinitionId { get; set; }
    public required string DisplayName { get; set; }
    public required int OwnerKind { get; set; }
    public required string OwnerId { get; set; }
    public required bool IsSealed { get; set; }
}

public sealed class AuthorizationDefinitionRow
{
    public required string DefinitionId { get; set; }
    public required long Revision { get; set; }
    public long? EffectiveAtUnixMs { get; set; }
    public string? DeclaringTenantId { get; set; }
    public required string PublisherPackageId { get; set; }
    public required string Operation { get; set; }
    public required int ScopeType { get; set; }
    public required string ScopeValue { get; set; }
}

public sealed class AuthorizationOfferedRoleRow
{
    public required string DefinitionId { get; set; }
    public required long Revision { get; set; }
    public required string Vocabulary { get; set; }
    public required string RoleName { get; set; }
}

public sealed class AuthorizationBindingRevisionRow
{
    public required string TenantId { get; set; }
    public required string DefinitionId { get; set; }
    public required long Revision { get; set; }
    public required string ChangedBy { get; set; }
    public required long ChangedAtUnixMs { get; set; }
    public required string Reason { get; set; }
    public int? Warning { get; set; }
}

public sealed class AuthorizationBindingRoleRow
{
    public required string TenantId { get; set; }
    public required string DefinitionId { get; set; }
    public required long Revision { get; set; }
    public required string Vocabulary { get; set; }
    public required string RoleName { get; set; }
}

public sealed class AuthorizationCatalogVersionRow { public int Id { get; set; } = 1; public long Version { get; set; } }
public sealed class AuthorizationTenantVersionRow { public required string TenantId { get; set; } public long Version { get; set; } }

/// <summary>One rebuildable grant + definition derivation path in the current authorization closure.</summary>
public sealed class AuthorizationClosureEntryRow
{
    public required string TenantId { get; set; }
    public required string PrincipalId { get; set; }
    public required string Operation { get; set; }
    public required int ScopeType { get; set; }
    public required string ScopeValue { get; set; }
    public required string RoleVocabulary { get; set; }
    public required string RoleName { get; set; }
    public required string GrantId { get; set; }
    public required long GrantOwnerVersion { get; set; }
    public required string DefinitionId { get; set; }
    public required long ValidFromUnixMs { get; set; }
    public long? ValidToUnixMs { get; set; }
}

/// <summary>The source generations against which one tenant's closure was last rebuilt.</summary>
public sealed class AuthorizationClosureStateRow
{
    public required string TenantId { get; set; }
    public required long BuiltCatalogVersion { get; set; }
    public required long BuiltTenantVersion { get; set; }
}
