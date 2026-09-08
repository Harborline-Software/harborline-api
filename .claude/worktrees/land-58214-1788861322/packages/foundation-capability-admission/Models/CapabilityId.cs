namespace Harborline.Api.Foundation.CapabilityAdmission;

/// <summary>A stable, case-sensitive release capability identifier.</summary>
public readonly record struct CapabilityId
{
    private CapabilityId(string value) => Value = value;

    /// <summary>The ordinal identifier value.</summary>
    public string Value { get; }

    /// <summary>Creates an identifier, rejecting empty, padded, or path-like values.</summary>
    public static CapabilityId Of(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Contains('/', StringComparison.Ordinal) ||
            value.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                "Capability id must be an unpadded, whitespace-free identifier without a version path.",
                nameof(value));
        }

        return new CapabilityId(value);
    }

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>A stable, case-sensitive capability contract version such as <c>v1</c>.</summary>
public readonly record struct CapabilityVersion
{
    private CapabilityVersion(string value) => Value = value;

    /// <summary>The version value.</summary>
    public string Value { get; }

    /// <summary>Creates a version, requiring the closed <c>v&lt;positive integer&gt;</c> shape.</summary>
    public static CapabilityVersion Of(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length < 2 || value[0] != 'v' ||
            !int.TryParse(value.AsSpan(1), out var number) || number <= 0 ||
            !string.Equals(value, $"v{number}", StringComparison.Ordinal))
        {
            throw new ArgumentException("Capability version must use the v<positive integer> shape.", nameof(value));
        }

        return new CapabilityVersion(value);
    }

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>The version-qualified key used by manifests, registrations, and catalog snapshots.</summary>
public readonly record struct CapabilityKey(CapabilityId Id, CapabilityVersion Version)
    : IComparable<CapabilityKey>
{
    /// <inheritdoc />
    public int CompareTo(CapabilityKey other) =>
        StringComparer.Ordinal.Compare(ToString(), other.ToString());

    /// <inheritdoc />
    public override string ToString() => $"{Id}/{Version}";
}
