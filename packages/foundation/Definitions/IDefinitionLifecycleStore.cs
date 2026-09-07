using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Definitions;

/// <summary>A definition identity within its tenant boundary.</summary>
/// <param name="Tenant">The tenant boundary.</param>
/// <param name="Identity">The stable definition identity.</param>
public readonly record struct DefinitionAddress(TenantId Tenant, DefinitionIdentity Identity)
{
    /// <summary>Constructs an address from its canonical identity text.</summary>
    public DefinitionAddress(TenantId tenant, string identity)
        : this(tenant, new DefinitionIdentity(identity))
    {
    }
}

/// <summary>A stable definition identity independent of its domain body.</summary>
/// <param name="Value">The canonical identity text.</param>
public readonly record struct DefinitionIdentity(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>A semantic definition version independent of its domain body.</summary>
/// <param name="Major">The major segment.</param>
/// <param name="Minor">The minor segment.</param>
/// <param name="Patch">The patch segment.</param>
public readonly record struct DefinitionLifecycleVersion(int Major, int Minor, int Patch) : IComparable<DefinitionLifecycleVersion>
{
    /// <summary>Parses the canonical <c>major.minor.patch</c> representation.</summary>
    public static DefinitionLifecycleVersion Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var parts = value.Split('.');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor)
            || !int.TryParse(parts[2], out var patch)
            || major < 0
            || minor < 0
            || patch < 0)
        {
            throw new FormatException($"Definition version must be major.minor.patch; got '{value}'.");
        }

        return new DefinitionLifecycleVersion(major, minor, patch);
    }

    /// <inheritdoc />
    public int CompareTo(DefinitionLifecycleVersion other)
    {
        var byMajor = Major.CompareTo(other.Major);
        if (byMajor != 0) return byMajor;
        var byMinor = Minor.CompareTo(other.Minor);
        return byMinor != 0 ? byMinor : Patch.CompareTo(other.Patch);
    }

    /// <inheritdoc />
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

/// <summary>The complete immutable coordinates of one definition revision.</summary>
/// <param name="Address">The tenant-scoped definition address.</param>
/// <param name="Version">The semantic revision.</param>
public readonly record struct DefinitionCoordinates(DefinitionAddress Address, DefinitionLifecycleVersion Version)
{
    /// <summary>Constructs coordinates from canonical identity and version text.</summary>
    public DefinitionCoordinates(TenantId tenant, string identity, string version)
        : this(new DefinitionAddress(tenant, identity), DefinitionLifecycleVersion.Parse(version))
    {
    }
}

/// <summary>
/// The common immutable-definition lifecycle. Domain stores extend this contract only for operations
/// that require their body or provenance shape.
/// </summary>
/// <typeparam name="TDefinition">The stored definition revision.</typeparam>
public interface IDefinitionLifecycleStore<TDefinition>
{
    /// <summary>Loads one exact revision.</summary>
    ValueTask<TDefinition> GetAsync(DefinitionCoordinates coordinates, CancellationToken cancellationToken = default);

    /// <summary>Loads the highest published revision at an address, or <see langword="null"/>.</summary>
    ValueTask<TDefinition?> GetCurrentPublishedAsync(DefinitionAddress address, CancellationToken cancellationToken = default);

    /// <summary>Lists every revision in a tenant in stable identity/version order.</summary>
    IAsyncEnumerable<TDefinition> ListByTenantAsync(TenantId tenant, CancellationToken cancellationToken = default);

    /// <summary>Lists every currently published revision across tenants in stable coordinate order.</summary>
    IAsyncEnumerable<TDefinition> ListPublishedAsync(CancellationToken cancellationToken = default);
}
