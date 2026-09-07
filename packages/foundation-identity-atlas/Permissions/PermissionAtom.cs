namespace Harborline.Api.Foundation.IdentityAtlas.Permissions;

/// <summary>An authorization operation qualified by a tenant-relative scope.</summary>
public readonly record struct PermissionAtom
{
    /// <summary>Creates a scoped permission atom.</summary>
    public PermissionAtom(AuthorizationOperation operation, ScopeExpression scope)
    {
        if (string.IsNullOrWhiteSpace(operation.Value))
        {
            throw new ArgumentException("A permission atom requires a valid operation.", nameof(operation));
        }

        ArgumentNullException.ThrowIfNull(scope);
        Operation = operation;
        Scope = scope;
    }

    /// <summary>The operation performed.</summary>
    public AuthorizationOperation Operation { get; }

    /// <summary>The scope in which the operation may be performed.</summary>
    public ScopeExpression Scope { get; }

    /// <summary>Parses <c>resource:verb@/path</c>.</summary>
    public static PermissionAtom Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var separator = value.IndexOf('@', StringComparison.Ordinal);
        if (separator <= 0 || separator != value.LastIndexOf('@'))
        {
            throw new ArgumentException("A permission atom must have the form resource:verb@/path.", nameof(value));
        }

        return new PermissionAtom(
            AuthorizationOperation.Parse(value[..separator]),
            ScopeExpression.Parse(value[(separator + 1)..]));
    }

    /// <summary>Returns whether this atom covers the requested operation and scope.</summary>
    public bool Covers(PermissionAtom requested) =>
        Operation.Equals(requested.Operation) && Scope.Contains(requested.Scope);

    /// <summary>Deconstructs the operation and scope.</summary>
    public void Deconstruct(out AuthorizationOperation operation, out ScopeExpression scope)
    {
        operation = Operation;
        scope = Scope;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Operation}@{Scope}";
}
