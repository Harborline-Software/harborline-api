using System.Collections.ObjectModel;

namespace Harborline.Api.Foundation.IdentityAtlas.Permissions;

/// <summary>An immutable, order-insensitive set of scoped permission atoms.</summary>
public sealed class PermissionAtomSet : IEquatable<PermissionAtomSet>
{
    private readonly HashSet<PermissionAtom> _atoms;
    private readonly IReadOnlyCollection<PermissionAtom> _snapshot;

    private PermissionAtomSet(IEnumerable<PermissionAtom> atoms)
    {
        var materialized = atoms.ToArray();
        if (materialized.Any(atom => string.IsNullOrWhiteSpace(atom.Operation.Value) || atom.Scope is null))
        {
            throw new ArgumentException("An atom set cannot contain an uninitialized atom.", nameof(atoms));
        }

        _atoms = new HashSet<PermissionAtom>(materialized);
        _snapshot = new ReadOnlyCollection<PermissionAtom>(
            _atoms.OrderBy(atom => atom.Operation.Value, StringComparer.Ordinal)
                .ThenBy(atom => atom.Scope.Value, StringComparer.Ordinal)
                .ToArray());
    }

    /// <summary>The empty atom set.</summary>
    public static PermissionAtomSet Empty { get; } = new(Array.Empty<PermissionAtom>());

    /// <summary>A stable read-only snapshot of the atoms.</summary>
    public IReadOnlyCollection<PermissionAtom> Atoms => _snapshot;

    /// <summary>Creates a set from explicit atoms.</summary>
    public static PermissionAtomSet Of(params PermissionAtom[] atoms)
    {
        ArgumentNullException.ThrowIfNull(atoms);
        return new PermissionAtomSet(atoms);
    }

    /// <summary>Creates a set from an atom sequence.</summary>
    public static PermissionAtomSet From(IEnumerable<PermissionAtom> atoms)
    {
        ArgumentNullException.ThrowIfNull(atoms);
        return new PermissionAtomSet(atoms);
    }

    /// <summary>Returns whether any held atom covers the requested atom.</summary>
    public bool Covers(PermissionAtom requested) => _atoms.Any(atom => atom.Covers(requested));

    /// <summary>Returns whether every requested atom is covered.</summary>
    public bool Covers(PermissionAtomSet requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        return requested._atoms.All(Covers);
    }

    /// <summary>Returns the additive union of two atom sets.</summary>
    public PermissionAtomSet Union(PermissionAtomSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new PermissionAtomSet(_atoms.Concat(other._atoms));
    }

    /// <inheritdoc />
    public bool Equals(PermissionAtomSet? other) =>
        other is not null && _atoms.SetEquals(other._atoms);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as PermissionAtomSet);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = 0;
        foreach (var atom in _atoms)
        {
            hash ^= atom.GetHashCode();
        }

        return hash;
    }
}
