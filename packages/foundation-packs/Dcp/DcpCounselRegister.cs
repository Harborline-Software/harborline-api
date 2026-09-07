using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Harborline.Api.Foundation.Packs.Dcp;

/// <summary>
/// Default <see cref="IDcpCounselRegister"/>. The counsel-cleared <see cref="RegulatoryClass"/> set is
/// read from the committed, embedded config <c>Validation/dcp-counsel-cleared-classes.json</c> (ADR 0145
/// D4), whose contents are driven by <c>company/legal/counsel-clearance-register.md</c>. Fail-closed: an
/// absent/unreadable config or an unrecognised class token yields an EMPTY cleared set (which blocks every
/// class, including <c>general</c>) rather than silently clearing anything.
/// </summary>
public sealed class DcpCounselRegister : IDcpCounselRegister
{
    private const string ResourceSuffix = "dcp-counsel-cleared-classes.json";

    private readonly IReadOnlySet<RegulatoryClass> _cleared;

    /// <summary>Constructs a register over an explicit cleared set (the test / config-injection path).</summary>
    public DcpCounselRegister(IEnumerable<RegulatoryClass> clearedClasses)
    {
        ArgumentNullException.ThrowIfNull(clearedClasses);
        _cleared = new HashSet<RegulatoryClass>(clearedClasses);
    }

    /// <inheritdoc />
    public bool IsCleared(RegulatoryClass regulatoryClass) => _cleared.Contains(regulatoryClass);

    /// <inheritdoc />
    public IReadOnlySet<RegulatoryClass> ClearedClasses => _cleared;

    /// <summary>
    /// Builds the register from the committed embedded config. Fail-closed on any read/parse problem —
    /// an unreadable config clears NOTHING (every class blocks), never everything.
    /// </summary>
    public static DcpCounselRegister FromEmbeddedResource()
        => new(ReadClearedFromResource());

    private static IReadOnlyList<RegulatoryClass> ReadClearedFromResource()
    {
        var assembly = typeof(DcpCounselRegister).Assembly;
        var resourceName = Array.Find(
            assembly.GetManifestResourceNames(),
            n => n.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        if (resourceName is null)
        {
            return Array.Empty<RegulatoryClass>();
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return Array.Empty<RegulatoryClass>();
        }

        return ParseCleared(stream);
    }

    /// <summary>
    /// Parses the counsel-cleared class list from a config <paramref name="stream"/>. Fail-closed: malformed
    /// JSON, a missing/non-array <c>cleared</c> node, or unrecognised class tokens all yield an EMPTY set
    /// (clears nothing — never everything). Internal so the fail-closed contract is directly testable with a
    /// malformed config (ADR 0145 D4), rather than only through the embedded-resource happy path.
    /// </summary>
    internal static IReadOnlyList<RegulatoryClass> ParseCleared(Stream stream)
    {
        try
        {
            var node = JsonNode.Parse(stream);
            if (node?["cleared"] is not JsonArray array)
            {
                return Array.Empty<RegulatoryClass>();
            }

            var cleared = new List<RegulatoryClass>(array.Count);
            foreach (var entry in array)
            {
                if (entry is JsonValue value
                    && value.TryGetValue(out string? token)
                    && TryParseClass(token, out var regulatoryClass))
                {
                    cleared.Add(regulatoryClass);
                }
            }

            return cleared;
        }
        catch (JsonException)
        {
            // Fail-closed: a malformed config clears nothing.
            return Array.Empty<RegulatoryClass>();
        }
    }

    /// <summary>Parses a config token (lowercase, hyphenated — <c>"safety-critical"</c>) to a
    /// <see cref="RegulatoryClass"/>. Case-insensitive, hyphen-insensitive; unknown ⇒ ignored.</summary>
    private static bool TryParseClass(string? token, out RegulatoryClass regulatoryClass)
    {
        regulatoryClass = default;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var normalized = token.Replace("-", string.Empty, StringComparison.Ordinal).Trim();
        return Enum.TryParse(normalized, ignoreCase: true, out regulatoryClass)
            && Enum.IsDefined(regulatoryClass);
    }
}
