using System.Text.Json.Serialization;

namespace Harborline.Api.Foundation.IdentityAtlas.Permissions;

/// <summary>Qualified role vocabulary names.</summary>
public static class RoleVocabularies
{
    /// <summary>The sealed platform role vocabulary.</summary>
    public const string Platform = "sys.platform-roles";

    /// <summary>The domain role vocabulary for package- and tenant-owned roles.</summary>
    public const string Domain = "tax.roles";
}

/// <summary>A qualified reference to a powerless role name.</summary>
public readonly record struct RoleReference
{
    /// <summary>Creates a qualified role reference.</summary>
    [JsonConstructor]
    public RoleReference(string vocabulary, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vocabulary);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Vocabulary = vocabulary;
        Name = name;
    }

    /// <summary>The qualified vocabulary.</summary>
    public string Vocabulary { get; }

    /// <summary>The role name within the vocabulary.</summary>
    public string Name { get; }

    /// <summary>The sealed platform Administrator role.</summary>
    public static RoleReference Administrator { get; } = new(RoleVocabularies.Platform, "administrator");

    /// <summary>The sealed platform Auditor role.</summary>
    public static RoleReference Auditor { get; } = new(RoleVocabularies.Platform, "auditor");

    /// <summary>Deconstructs the qualified vocabulary and role name.</summary>
    public void Deconstruct(out string vocabulary, out string name)
    {
        vocabulary = Vocabulary;
        name = Name;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Vocabulary}/{Name}";
}

/// <summary>The owner categories for role vocabulary entries.</summary>
public enum RoleOwnerKind
{
    /// <summary>The platform owns the entry.</summary>
    Platform = 0,

    /// <summary>An installed package owns the entry.</summary>
    Package = 1,

    /// <summary>A tenant owns the entry.</summary>
    Tenant = 2,
}

/// <summary>The qualified owner of a role vocabulary entry.</summary>
public sealed record RoleOwner
{
    /// <summary>Creates a role owner.</summary>
    public RoleOwner(RoleOwnerKind kind, string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        Kind = kind;
        OwnerId = ownerId;
    }

    /// <summary>The owner category.</summary>
    public RoleOwnerKind Kind { get; }

    /// <summary>The owner identifier.</summary>
    public string OwnerId { get; }

    /// <summary>Deconstructs the owner kind and identifier.</summary>
    public void Deconstruct(out RoleOwnerKind kind, out string ownerId)
    {
        kind = Kind;
        ownerId = OwnerId;
    }
}
