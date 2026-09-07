using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Foundation.Forms.Models;

/// <summary>
/// Semantic version (Major.Minor.Patch) used to pin a <see cref="FormDefinition"/>
/// revision (ADR 0055 §"Schema Registry").
/// </summary>
/// <remarks>
/// <para>
/// Defined locally on the keystone substrate; promotable to
/// <c>Harborline.Api.Foundation.Assets.Common</c> if a second foundation substrate
/// needs the same type. Kept narrow on purpose — pre-release / build-metadata
/// segments (SemVer 2.0 §9 / §10) are not honoured in v1; the registry
/// compares versions by the three numeric segments and rejects extra
/// segments at parse time.
/// </para>
/// <para>
/// Equality + ordering are structural (Major, then Minor, then Patch). Wire
/// form is the canonical <c>"{Major}.{Minor}.{Patch}"</c> string.
/// </para>
/// </remarks>
/// <param name="Major">Major segment (breaking changes); MUST be non-negative.</param>
/// <param name="Minor">Minor segment (additive changes); MUST be non-negative.</param>
/// <param name="Patch">Patch segment (fixes); MUST be non-negative.</param>
[JsonConverter(typeof(SemanticVersionJsonConverter))]
public readonly record struct SemanticVersion(int Major, int Minor, int Patch)
    : IComparable<SemanticVersion>
{
    /// <summary>Canonical wire form: <c>"{Major}.{Minor}.{Patch}"</c>.</summary>
    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    /// <summary>
    /// Parses the canonical string form. Accepts exactly three non-negative
    /// integer segments separated by periods; anything else raises
    /// <see cref="FormatException"/>.
    /// </summary>
    public static SemanticVersion Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var parts = value.Split('.');
        if (parts.Length != 3)
        {
            throw new FormatException($"SemanticVersion expects 'Major.Minor.Patch'; got '{value}'.");
        }

        if (!int.TryParse(parts[0], out var major) || major < 0 ||
            !int.TryParse(parts[1], out var minor) || minor < 0 ||
            !int.TryParse(parts[2], out var patch) || patch < 0)
        {
            throw new FormatException($"SemanticVersion segments must be non-negative integers; got '{value}'.");
        }

        return new SemanticVersion(major, minor, patch);
    }

    /// <inheritdoc />
    public int CompareTo(SemanticVersion other)
    {
        var byMajor = Major.CompareTo(other.Major);
        if (byMajor != 0) return byMajor;
        var byMinor = Minor.CompareTo(other.Minor);
        if (byMinor != 0) return byMinor;
        return Patch.CompareTo(other.Patch);
    }

    /// <summary>Strict-less-than comparison.</summary>
    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    /// <summary>Strict-greater-than comparison.</summary>
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    /// <summary>Less-than-or-equal comparison.</summary>
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    /// <summary>Greater-than-or-equal comparison.</summary>
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;
}

internal sealed class SemanticVersionJsonConverter : JsonConverter<SemanticVersion>
{
    public override SemanticVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString() ?? throw new JsonException("SemanticVersion must be a non-null string.");
        try
        {
            return SemanticVersion.Parse(str);
        }
        catch (FormatException ex)
        {
            throw new JsonException(ex.Message, ex);
        }
    }

    public override void Write(Utf8JsonWriter writer, SemanticVersion value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
