using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.AccessGrant;

/// <summary>A powerless entry in a qualified role vocabulary.</summary>
public sealed record RoleDefinition
{
    /// <summary>Creates a validated role vocabulary entry.</summary>
    public RoleDefinition(
        RoleDefinitionId RoleDefinitionId,
        RoleReference Role,
        string DisplayName,
        RoleOwner Owner,
        bool IsSealed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DisplayName);
        ArgumentNullException.ThrowIfNull(Owner);

        if (Role.Vocabulary == RoleVocabularies.Platform)
        {
            if (!PlatformRoleVocabulary.Contains(Role)
                || Owner.Kind != RoleOwnerKind.Platform
                || !IsSealed)
            {
                throw new ArgumentException(
                    "Platform roles must be sealed, platform-owned entries from the fixed platform vocabulary.",
                    nameof(Role));
            }
        }
        else if (Role.Vocabulary != RoleVocabularies.Domain
            || Owner.Kind == RoleOwnerKind.Platform
            || IsSealed)
        {
            throw new ArgumentException(
                "Package- and tenant-owned roles must be unsealed tax.roles entries.",
                nameof(Role));
        }

        this.RoleDefinitionId = RoleDefinitionId;
        this.Role = Role;
        this.DisplayName = DisplayName;
        this.Owner = Owner;
        this.IsSealed = IsSealed;
    }

    /// <summary>The stable identifier for this vocabulary entry.</summary>
    public RoleDefinitionId RoleDefinitionId { get; }

    /// <summary>The qualified powerless role name.</summary>
    public RoleReference Role { get; }

    /// <summary>The display name shown to reviewers.</summary>
    public string DisplayName { get; }

    /// <summary>The platform, package, or tenant that owns the name.</summary>
    public RoleOwner Owner { get; }

    /// <summary>Whether the vocabulary entry is sealed against replacement.</summary>
    public bool IsSealed { get; }

    /// <summary>Deconstructs the powerless vocabulary entry.</summary>
    public void Deconstruct(
        out RoleDefinitionId roleDefinitionId,
        out RoleReference role,
        out string displayName,
        out RoleOwner owner,
        out bool isSealed)
    {
        roleDefinitionId = RoleDefinitionId;
        role = Role;
        displayName = DisplayName;
        owner = Owner;
        isSealed = IsSealed;
    }

    /// <summary>Creates an unsealed package-owned domain role without persisting it.</summary>
    public static RoleDefinition CreatePackageRole(
        RoleDefinitionId roleDefinitionId,
        string name,
        string displayName,
        string packageId) =>
        new(
            roleDefinitionId,
            new RoleReference(RoleVocabularies.Domain, name),
            displayName,
            new RoleOwner(RoleOwnerKind.Package, packageId),
            IsSealed: false);

    /// <summary>Creates an unsealed tenant-owned domain role without persisting it.</summary>
    public static RoleDefinition CreateTenantRole(
        RoleDefinitionId roleDefinitionId,
        string name,
        string displayName,
        TenantId tenantId) =>
        new(
            roleDefinitionId,
            new RoleReference(RoleVocabularies.Domain, name),
            displayName,
            new RoleOwner(RoleOwnerKind.Tenant, tenantId.Value),
            IsSealed: false);
}

/// <summary>Stable identifier for a <see cref="RoleDefinition"/>.</summary>
public readonly record struct RoleDefinitionId(Guid Value)
{
    /// <summary>Mints a fresh random role-definition identifier.</summary>
    public static RoleDefinitionId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}
