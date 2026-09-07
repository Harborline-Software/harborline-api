using System;
using System.Collections.Generic;
using System.Linq;

namespace Harborline.Api.Foundation.IdentityAtlas.Permissions;

/// <summary>
/// An immutable set of atomic permission strings — the MUTABLE-by-replacement value carried on a member's
/// roster edge (taxonomy §2: "roles are MUTABLE named compositions, not statuses; the truth on the membership
/// edge is a mutable set"). The set itself is immutable; mutation is producing a new set via
/// <see cref="With"/> / <see cref="Without"/> / <see cref="Union"/> and replacing the edge's value.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the PBAC atom holder.</b> "owner" / "admin" / "member" / "support" are default TEMPLATES
/// (<see cref="PermissionCompositions"/>) that expand to a <see cref="PermissionSet"/>; once seeded onto an
/// edge the set is independently grow/shrink-able via <c>grant:permissions</c>. <c>HasPermission(p)</c>
/// resolves to <c>set.Contains(p)</c>.
/// </para>
/// <para>
/// <b>The no-escalation guard lives here</b> (<see cref="IsSubsetOf"/>): a grant may only confer a set that is
/// a subset of the granter's currently-held set. A grant exceeding the granter is a privilege-escalation bug,
/// caught at evaluation time (taxonomy §3 guard 1).
/// </para>
/// </remarks>
public sealed class PermissionSet : IEquatable<PermissionSet>
{
    private readonly HashSet<string> _permissions;

    /// <summary>The empty set — holds nothing (the <c>support</c>-after-handoff / non-member resting state).</summary>
    public static readonly PermissionSet Empty = new(Array.Empty<string>());

    private PermissionSet(IEnumerable<string> permissions)
    {
        _permissions = new HashSet<string>(permissions, StringComparer.Ordinal);
    }

    /// <summary>Construct a set from an explicit permission list (deduplicated, order-insensitive).</summary>
    public static PermissionSet Of(params string[] permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        foreach (var p in permissions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(p);
        }
        return new PermissionSet(permissions);
    }

    /// <summary>Construct a set from an enumerable of permissions.</summary>
    public static PermissionSet From(IEnumerable<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        return new PermissionSet(permissions);
    }

    /// <summary>The permissions in the set, as a stable read-only snapshot (sorted for deterministic display).</summary>
    public IReadOnlyCollection<string> Permissions => _permissions.OrderBy(p => p, StringComparer.Ordinal).ToArray();

    /// <summary>The number of permissions held.</summary>
    public int Count => _permissions.Count;

    /// <summary>Resolves <c>HasPermission</c>: true iff <paramref name="permission"/> is held.</summary>
    public bool Contains(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        return _permissions.Contains(permission);
    }

    /// <summary>
    /// THE NO-ESCALATION GUARD (taxonomy §3 guard 1). True iff every permission in THIS set is also in
    /// <paramref name="other"/> — i.e. THIS set could be granted by a holder of <paramref name="other"/>
    /// without escalation. A grant whose set is NOT a subset of the granter's set is a privilege-escalation
    /// bug.
    /// </summary>
    public bool IsSubsetOf(PermissionSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return _permissions.IsSubsetOf(other._permissions);
    }

    /// <summary>Returns a new set with <paramref name="permission"/> added (a grant). Idempotent.</summary>
    public PermissionSet With(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        var next = new HashSet<string>(_permissions, StringComparer.Ordinal) { permission };
        return new PermissionSet(next);
    }

    /// <summary>Returns a new set with <paramref name="permission"/> removed (a revoke). Idempotent.</summary>
    public PermissionSet Without(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        var next = new HashSet<string>(_permissions, StringComparer.Ordinal);
        next.Remove(permission);
        return new PermissionSet(next);
    }

    /// <summary>Returns the union of this set and <paramref name="other"/>.</summary>
    public PermissionSet Union(PermissionSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var next = new HashSet<string>(_permissions, StringComparer.Ordinal);
        next.UnionWith(other._permissions);
        return new PermissionSet(next);
    }

    /// <inheritdoc />
    public bool Equals(PermissionSet? other) =>
        other is not null && _permissions.SetEquals(other._permissions);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as PermissionSet);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        // Order-independent hash over the contents.
        var hash = 0;
        foreach (var p in _permissions)
        {
            hash ^= StringComparer.Ordinal.GetHashCode(p);
        }
        return hash;
    }

    /// <inheritdoc />
    public override string ToString() => $"{{{string.Join(", ", Permissions)}}}";
}
