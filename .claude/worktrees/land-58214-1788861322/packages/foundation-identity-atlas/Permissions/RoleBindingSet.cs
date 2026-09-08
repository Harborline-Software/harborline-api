using System.Collections.ObjectModel;

namespace Harborline.Api.Foundation.IdentityAtlas.Permissions;

/// <summary>An immutable set of qualified role bindings.</summary>
public sealed class RoleBindingSet : IEquatable<RoleBindingSet>
{
    private readonly HashSet<RoleReference> _roles;
    private readonly IReadOnlyCollection<RoleReference> _snapshot;

    private RoleBindingSet(IEnumerable<RoleReference> roles)
    {
        var materialized = roles.ToArray();
        if (materialized.Any(role =>
            string.IsNullOrWhiteSpace(role.Vocabulary) || string.IsNullOrWhiteSpace(role.Name)))
        {
            throw new ArgumentException("A binding set cannot contain an uninitialized role reference.", nameof(roles));
        }

        _roles = new HashSet<RoleReference>(materialized);
        _snapshot = new ReadOnlyCollection<RoleReference>(
            _roles.OrderBy(role => role.Vocabulary, StringComparer.Ordinal)
                .ThenBy(role => role.Name, StringComparer.Ordinal)
                .ToArray());
    }

    /// <summary>The empty binding set.</summary>
    public static RoleBindingSet Empty { get; } = new(Array.Empty<RoleReference>());

    /// <summary>A stable read-only snapshot of the roles.</summary>
    public IReadOnlyCollection<RoleReference> Roles => _snapshot;

    /// <summary>Creates a binding set from explicit role references.</summary>
    public static RoleBindingSet Of(params RoleReference[] roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return new RoleBindingSet(roles);
    }

    /// <summary>Creates a binding set from a role sequence.</summary>
    public static RoleBindingSet From(IEnumerable<RoleReference> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return new RoleBindingSet(roles);
    }

    /// <summary>Returns whether this set is no wider than <paramref name="ceiling"/>.</summary>
    public bool IsSubsetOf(RoleBindingSet ceiling)
    {
        ArgumentNullException.ThrowIfNull(ceiling);
        return _roles.IsSubsetOf(ceiling._roles);
    }

    /// <summary>Returns the roles present in both sets.</summary>
    public RoleBindingSet Intersect(RoleBindingSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new RoleBindingSet(_roles.Intersect(other._roles));
    }

    /// <inheritdoc />
    public bool Equals(RoleBindingSet? other) =>
        other is not null && _roles.SetEquals(other._roles);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as RoleBindingSet);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = 0;
        foreach (var role in _roles)
        {
            hash ^= role.GetHashCode();
        }

        return hash;
    }
}
