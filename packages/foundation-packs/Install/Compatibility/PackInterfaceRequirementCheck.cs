using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Packs.Model;

namespace Harborline.Api.Foundation.Packs.Install.Compatibility;

/// <summary>Resolves exact <c>pack-key@positive-integer</c> interface requirements in ADR 0006 envelopes.</summary>
public static class PackInterfaceRequirementCheck
{
    public static IReadOnlyList<PackUnmetInterfaceRequirement> FindUnmet(InstalledPack target, IReadOnlyList<InstalledPack> active)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(active);
        var unmet = new List<PackUnmetInterfaceRequirement>();
        foreach (var item in target.SeedItems)
        foreach (var requirement in ReadRequirements(item))
            if (!active.Any(pack => string.Equals(pack.PackKey, requirement.PackKey, StringComparison.Ordinal)
                && pack.InterfaceVersion == requirement.InterfaceVersion))
                unmet.Add(new PackUnmetInterfaceRequirement(item.Key, requirement.PackKey, requirement.InterfaceVersion));
        return unmet.OrderBy(r => r.ContentKey, StringComparer.Ordinal).ThenBy(r => r.PackKey, StringComparer.Ordinal)
            .ThenBy(r => r.InterfaceVersion).ToList();
    }

    public static PackUnexposedDefinitionReference? FindUnexposed(InstalledPack target, IReadOnlyList<InstalledPack> active)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(active);
        foreach (var reference in target.ContentReferences ?? Array.Empty<PackContentReferenceEdge>())
        {
            var provider = active.FirstOrDefault(pack => string.Equals(pack.PackKey, reference.ToPackKey, StringComparison.Ordinal));
            if (provider is null || !(provider.Exposes ?? Array.Empty<string>()).Contains(reference.ToContentKey, StringComparer.Ordinal))
                return new PackUnexposedDefinitionReference(reference.FromContentKey, reference.ToPackKey, reference.ToContentKey);
        }
        return null;
    }

    public static bool TryParse(string? capability, out string packKey, out int interfaceVersion)
    {
        packKey = string.Empty;
        interfaceVersion = default;
        // Ordinal: '@' is a structural separator in the ADR 0006 pack-key@integer spelling, not text a
        // culture may reinterpret. CA1307 asks for the intent to be stated rather than defaulted.
        var separator = capability?.LastIndexOf('@', StringComparison.Ordinal) ?? -1;
        if (separator <= 0 || separator == capability!.Length - 1
            || !int.TryParse(capability[(separator + 1)..], out interfaceVersion) || interfaceVersion <= 0) return false;
        packKey = capability[..separator];
        return !string.IsNullOrWhiteSpace(packKey);
    }

    private static IEnumerable<PackInterfaceRequirement> ReadRequirements(PackSeedItem item)
    {
        JsonNode? parsed;
        try { parsed = item.ParseContent(); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { yield break; }
        if (parsed?["definitionEnvelope"]?["requires"] is not JsonArray requirements) yield break;
        foreach (var requirement in requirements.OfType<JsonObject>())
            if (TryParse(requirement["capability"]?.GetValue<string>(), out var packKey, out var interfaceVersion))
                yield return new PackInterfaceRequirement(packKey, interfaceVersion);
    }

    private sealed record PackInterfaceRequirement(string PackKey, int InterfaceVersion);
}

/// <summary>One definition-envelope interface requirement no active pack exposes.</summary>
public sealed record PackUnmetInterfaceRequirement(string ContentKey, string PackKey, int InterfaceVersion);

/// <summary>One content reference that reaches beyond an active provider's frozen export surface.</summary>
public sealed record PackUnexposedDefinitionReference(string FromContentKey, string ToPackKey, string ToContentKey);
