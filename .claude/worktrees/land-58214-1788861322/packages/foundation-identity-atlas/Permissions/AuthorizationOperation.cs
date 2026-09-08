namespace Harborline.Api.Foundation.IdentityAtlas.Permissions;

/// <summary>A canonical operation identifier from the platform permission vocabulary.</summary>
public readonly record struct AuthorizationOperation
{
    /// <summary>Creates a validated operation identifier.</summary>
    public AuthorizationOperation(string value)
    {
        Value = Validate(value);
    }

    /// <summary>The canonical operation identifier.</summary>
    public string Value { get; }

    /// <summary>Parses a canonical lower-case <c>resource:verb</c> operation.</summary>
    public static AuthorizationOperation Parse(string value) => new(value);

    /// <summary>Deconstructs the operation value.</summary>
    public void Deconstruct(out string value) => value = Value;

    /// <inheritdoc />
    public override string ToString() => Value;

    private static string Validate(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Contains("@", StringComparison.Ordinal)
            || value.Contains("/", StringComparison.Ordinal)
            || value.Contains("\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("Authorization operations must be canonical resource:verb identifiers.", nameof(value));
        }

        var segments = value.Split(':');
        if (segments.Length < 2 || segments.Any(segment => !IsCanonicalSegment(segment)))
        {
            throw new ArgumentException("Authorization operations must be canonical resource:verb identifiers.", nameof(value));
        }

        return value;
    }

    private static bool IsCanonicalSegment(string segment) =>
        segment.Length > 0
        && segment[0] is >= 'a' and <= 'z'
        && segment[^1] != '-'
        && segment.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}
