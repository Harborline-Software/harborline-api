using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Packs.Model;


namespace Harborline.Api.Foundation.Packs.Install.Admission;

/// <summary>A translation and the source revision against which it was written.</summary>
public sealed record TerminologyTranslation(string Text, string SourceRevision);

/// <summary>Version 1 terminology content, keyed by the unchanged pack content identity.</summary>
public sealed record TerminologyContent(
    int SchemaVersion, string StableId, string Version, string? TenantId, string DefaultLocale,
    string SourceRevision, IReadOnlyDictionary<string, TerminologyTranslation> PackageTranslations,
    IReadOnlyDictionary<string, TerminologyTranslation> TenantOverrides);

/// <summary>The explicit outcome of resolving one stable identity in a user locale.</summary>
public sealed record TerminologyResolution(
    string StableId, string Version, string Text, string RequestedLocale, string ResolvedLocale,
    string Source, bool UsedFallback, bool IsStale);

/// <summary>Published terminology admission and projection refusal codes.</summary>
public static class PackTerminologyCodes
{
    /// <summary>Malformed or unknown contract shape.</summary>
    public const string Malformed = "pack.terminology.malformed";
    /// <summary>The reader does not support the declared schema version.</summary>
    public const string UnsupportedVersion = "pack.terminology.unsupported_version";
    /// <summary>The content claims another tenant.</summary>
    public const string CrossTenant = "pack.terminology.cross_tenant";
    /// <summary>A tenant patch changed a package-owned identity or translation.</summary>
    public const string ImmutableSourceChanged = "pack.terminology.immutable_source_changed";
    /// <summary>Competing package claims have no resolved terminology owner.</summary>
    public const string OwnershipUnresolved = "pack.terminology.ownership_unresolved";
}

/// <summary>Strict versioned terminology parsing shared by installation and runtime projection.</summary>
public static class PackTerminologyContent
{
    /// <summary>Validates both signed source and final composed content before any installation effect.</summary>
    public static IReadOnlyList<PackAdmissionRefusal> Validate(IReadOnlyList<PackComposedItem> composed, TenantId tenant)
    {
        var refusals = new List<PackAdmissionRefusal>();
        foreach (var item in composed.Where(item => item.Kind == PackContentKind.TerminologyOverride))
        {
            var code = TryReadTerminology(item, tenant, out _);
            if (code is not null)
                refusals.Add(new PackAdmissionRefusal(item.Key, code, "Terminology content failed versioned tenant admission."));
        }
        return refusals;
    }

    /// <summary>Parses an admitted content candidate; a refusal never returns a partial model.</summary>
    public static string? TryReadTerminology(PackComposedItem item, TenantId tenant, out TerminologyContent? content)
    {
        content = null;
        if (item.Kind != PackContentKind.TerminologyOverride) return PackTerminologyCodes.Malformed;
        try
        {
            using var document = JsonDocument.Parse(item.CanonicalJson);
            var code = ReadTerminologyBody(document.RootElement, item, tenant, out var candidate);
            if (code is not null) return code;
            if (item.SeedCanonicalJson is { } seedJson)
            {
                using var seed = JsonDocument.Parse(seedJson);
                code = ReadTerminologyBody(seed.RootElement, item, tenant, out var source);
                if (code is not null) return code;
                if (source!.TenantOverrides.Count != 0) return PackTerminologyCodes.Malformed;
                var original = JsonNode.Parse(seedJson)!.AsObject();
                var composed = JsonNode.Parse(item.CanonicalJson)!.AsObject();
                original.Remove("tenantOverrides");
                composed.Remove("tenantOverrides");
                if (!JsonNode.DeepEquals(original, composed)) return PackTerminologyCodes.ImmutableSourceChanged;
            }
            content = candidate;
            return null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            return PackTerminologyCodes.Malformed;
        }
    }

    private static string? ReadTerminologyBody(JsonElement root, PackComposedItem item, TenantId tenant, out TerminologyContent? content)
    {
        content = null;
        Fields(root, ["schemaVersion", "stableId", "version", "tenantId", "defaultLocale", "sourceRevision", "packageTranslations", "tenantOverrides"]);
        if (!root.GetProperty("schemaVersion").TryGetInt32(out var schema)) return PackTerminologyCodes.Malformed;
        if (schema != 1) return PackTerminologyCodes.UnsupportedVersion;
        var id = Text(root, "stableId");
        var version = Text(root, "version");
        if (id != item.Key || version != item.Version) return PackTerminologyCodes.Malformed;
        var tenantId = root.TryGetProperty("tenantId", out var scope) ? scope.GetString() : null;
        if (scope.ValueKind != JsonValueKind.Undefined && (string.IsNullOrWhiteSpace(tenantId) || tenantId != tenant.ToString())) return PackTerminologyCodes.CrossTenant;
        var locale = Text(root, "defaultLocale");
        if (!IsLocale(locale)) return PackTerminologyCodes.Malformed;
        var revision = Text(root, "sourceRevision");
        var package = Translations(root.GetProperty("packageTranslations"));
        var overrides = Translations(root.GetProperty("tenantOverrides"));
        if (!package.TryGetValue(locale, out var fallback) || fallback.SourceRevision != revision) return PackTerminologyCodes.Malformed;
        content = new TerminologyContent(schema, id, version, tenantId, locale, revision, package, overrides);
        return null;
    }

    private static ReadOnlyDictionary<string, TerminologyTranslation> Translations(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
        var result = new Dictionary<string, TerminologyTranslation>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!IsLocale(property.Name)) throw new JsonException();
            Fields(property.Value, ["text", "sourceRevision"]);
            if (!result.TryAdd(property.Name, new TerminologyTranslation(Text(property.Value, "text"), Text(property.Value, "sourceRevision"))))
                throw new JsonException();
        }
        return new ReadOnlyDictionary<string, TerminologyTranslation>(result);
    }

    private static void Fields(JsonElement root, string[] allowed)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw new JsonException();
        if (allowed.Any(field => field != "tenantId" && !seen.Contains(field))) throw new JsonException();
    }

    private static string Text(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString())) throw new JsonException();
        return value.GetString()!;
    }

    private static bool IsLocale(string locale)
    {
        try { return !string.IsNullOrWhiteSpace(locale) && CultureInfo.GetCultureInfo(locale).Name == locale; }
        catch (CultureNotFoundException) { return false; }
    }

    /// <summary>Overlays tenant translations, then selects exact locale, parent locales and the package default.</summary>
    public static TerminologyResolution Resolve(TerminologyContent content, string userLocale)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!IsLocale(userLocale)) throw new ArgumentException("A canonical user locale is required.", nameof(userLocale));
        var locales = new List<string>();
        var culture = CultureInfo.GetCultureInfo(userLocale);
        while (culture.Name.Length > 0)
        {
            locales.Add(culture.Name);
            culture = culture.Parent;
        }
        locales.Add(content.DefaultLocale);
        foreach (var locale in locales.Distinct(StringComparer.Ordinal))
        {
            var tenant = content.TenantOverrides.TryGetValue(locale, out var translation);
            if (!tenant && !content.PackageTranslations.TryGetValue(locale, out translation)) continue;
            return new TerminologyResolution(content.StableId, content.Version, translation!.Text, userLocale, locale,
                tenant ? "tenant" : "pack", locale != userLocale, translation.SourceRevision != content.SourceRevision);
        }
        throw new InvalidOperationException("Admitted terminology must have a default translation.");
    }
}
