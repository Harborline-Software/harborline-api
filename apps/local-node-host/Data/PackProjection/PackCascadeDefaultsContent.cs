using System.Text.Json;
using System.Text.Json.Serialization;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Governance.Bridges;
using Harborline.Api.Foundation.Governance.Resolution;
using Harborline.Api.Foundation.Governance.Enforcement;

namespace Harborline.Api.LocalNodeHost.Data.PackProjection;

/// <summary>Strict v1 admission shared by installation and active projection.</summary>
internal static class PackCascadeDefaultsContent
{
    internal const string Malformed = "pack.defaults.malformed";
    internal const string UnsupportedVersion = "pack.defaults.unsupported_version";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
    };

    private sealed record Wire(int SchemaVersion, string Title, IReadOnlyList<Entry> Defaults);
    private sealed record Entry(string? RecordType = null, string? Field = null,
        IReadOnlyList<Tag>? Classification = null, bool? PersonalData = null,
        CascadeMasking? Masking = null, RetentionRequirement? Retention = null,
        string? ConflictPolicy = null, bool? TrackChanges = null);

    internal static bool TryParse(string json, out CascadeDefaults? result, out string code, out string pointer)
    {
        result = null;
        code = Malformed;
        pointer = "/";
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            RejectDuplicates(root, "");
            pointer = "/schemaVersion";
            if (!root.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var version)) return false;
            if (version != 1) { code = UnsupportedVersion; return false; }
            pointer = "/";
            var wire = JsonSerializer.Deserialize<Wire>(json, Json);
            if (wire is null || string.IsNullOrWhiteSpace(wire.Title) || wire.Defaults is null) return false;
            var coordinates = new HashSet<(string?, string?)>();
            var declarations = new List<CascadeDeclaration>();
            for (var i = 0; i < wire.Defaults.Count; i++)
            {
                pointer = $"/defaults/{i}";
                var entry = wire.Defaults[i];
                var authored = root.GetProperty("defaults")[i];
                if (entry is null || !Target(entry.RecordType) || !Target(entry.Field)
                    || (entry.Field is not null && entry.RecordType is null)
                    || !coordinates.Add((entry.RecordType, entry.Field))) return false;
                if (entry.Masking is not null && !authored.GetProperty("masking").TryGetProperty("revealLast", out _)) return false;
                if (entry.Classification?.Any(tag => tag is null || string.IsNullOrWhiteSpace(tag.System)
                        || string.IsNullOrWhiteSpace(tag.Code)) == true
                    || entry.Masking is { RevealLast: < 0 }
                    || entry.ConflictPolicy is not (null or "ask")) return false;
                if (entry.Retention is { } retention)
                {
                    if (!authored.GetProperty("retention").TryGetProperty("minimumRetentionDays", out _)) return false;
                    if (retention.MinimumRetentionDays < 0 || !GovernanceRetentionFloor.SupportsRegime(retention.Regime)) return false;
                    if (!Enum.IsDefined(new DefaultFieldClassAuditEventClassMap().Resolve(retention.FloorClass))) return false;
                }
                declarations.Add(new(entry.RecordType, entry.Field, new(entry.Classification is null ? null : Array.AsReadOnly(entry.Classification.ToArray()),
                    entry.PersonalData, entry.Masking, entry.Retention, entry.ConflictPolicy, entry.TrackChanges)));
            }
            result = new(1, wire.Title, declarations.AsReadOnly());
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or GovernanceConfigurationException)
        {
            if (exception is JsonException { Path: { } path })
                pointer = path.StartsWith("$", StringComparison.Ordinal) ? path[1..].Replace(".", "/", StringComparison.Ordinal)
                    .Replace("[", "/", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal) : path;
            return false;
        }
    }

    private static bool Target(string? target) => target is null || (!string.IsNullOrWhiteSpace(target)
        && target == target.Trim() && !target.Any(char.IsControl));

    private static void RejectDuplicates(JsonElement element, string pointer)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var path = pointer + "/" + property.Name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
                if (!names.Add(property.Name)) throw new JsonException("Duplicate property", path, null, null);
                RejectDuplicates(property.Value, path);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var child in element.EnumerateArray()) RejectDuplicates(child, pointer + "/" + i++);
        }
    }
}
