using System.Text.Json.Serialization;

namespace Harborline.Api.Foundation.IdentityAtlas.Permissions;

/// <summary>The supported scope-expression shapes.</summary>
public enum ScopeExpressionType
{
    /// <summary>An absolute tenant-relative path prefix.</summary>
    PathPrefix = 0,
}

/// <summary>A normalized absolute tenant-relative authorization scope.</summary>
public sealed record ScopeExpression
{
    /// <summary>Creates the normalized expression used by JSON and <see cref="Parse"/>.</summary>
    [JsonConstructor]
    public ScopeExpression(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value[0] != '/'
            || value.Contains("\\", StringComparison.Ordinal)
            || value.Contains("?", StringComparison.Ordinal)
            || value.Contains("#", StringComparison.Ordinal))
            throw new ArgumentException("A scope must be an absolute tenant-relative path.", nameof(value));

        var normalized = value.Length > 1 && value.EndsWith('/', StringComparison.Ordinal) ? value[..^1] : value;
        if (normalized != "/")
        {
            var segments = normalized.Split('/');
            for (var index = 1; index < segments.Length; index++)
                if (segments[index].Length == 0 || segments[index] is "." or ".."
                    || Uri.UnescapeDataString(segments[index]) is "." or "..")
                    throw new ArgumentException("A scope cannot contain empty, current, or parent path segments.", nameof(value));
        }
        Type = ScopeExpressionType.PathPrefix;
        Value = normalized;
    }

    /// <summary>The expression shape.</summary>
    public ScopeExpressionType Type { get; }

    /// <summary>The normalized absolute tenant-relative path.</summary>
    public string Value { get; }

    /// <summary>Parses and normalizes an absolute tenant-relative path prefix.</summary>
    public static ScopeExpression Parse(string value)
    {
        return new ScopeExpression(value);
    }

    /// <summary>Returns whether this prefix contains <paramref name="candidate"/>.</summary>
    public bool Contains(ScopeExpression candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (Type != candidate.Type)
        {
            return false;
        }

        return Value == "/"
            || string.Equals(Value, candidate.Value, StringComparison.Ordinal)
            || candidate.Value.StartsWith(Value + "/", StringComparison.Ordinal);
    }

    /// <summary>Returns the narrower comparable prefix, or null for disjoint scopes.</summary>
    public ScopeExpression? Intersect(ScopeExpression other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Contains(other))
        {
            return other;
        }

        return other.Contains(this) ? this : null;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
