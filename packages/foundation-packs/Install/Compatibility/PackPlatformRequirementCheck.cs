using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Packs.Model;

namespace Harborline.Api.Foundation.Packs.Install.Compatibility;

/// <summary>
/// The SINGLE platform-compatibility requirement check (ticket 160). Extracts declared platform
/// requirements (manifest <c>capabilityRequirements</c> + per-content <c>definitionEnvelope.requires</c>)
/// and resolves them against <see cref="IPackPlatformCompatibility"/>. Shared by the installer
/// (install/preview + activate) AND the boot re-projection path, so an upgrade that lands a pack outside
/// the running platform's window is refused with the same semantics everywhere — no reimplementation.
/// </summary>
public static class PackPlatformRequirementCheck
{
    /// <summary>Unmet requirements for a CANDIDATE pack (verified manifest + contents).</summary>
    public static IReadOnlyList<PackUnmetPlatformRequirement> FindUnmet(
        PackManifest manifest, IReadOnlyList<PackContentItem> contents, IPackPlatformCompatibility platform)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(platform);

        var requirements = manifest.CapabilityRequirements
            .Select(capability => new PlatformRequirement(capability, null, manifest.Key))
            .ToList();
        foreach (var content in contents)
        {
            AddEnvelopeRequirements(requirements, content.Key, TryParse(content.CanonicalBytes.Span));
        }

        return FindUnmet(requirements, platform);
    }

    /// <summary>Unmet requirements for an INSTALLED pack (persisted requirements + seed envelopes).</summary>
    public static IReadOnlyList<PackUnmetPlatformRequirement> FindUnmet(
        InstalledPack pack, IPackPlatformCompatibility platform)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(platform);

        var requirements = (pack.CapabilityRequirements ?? Array.Empty<string>())
            .Select(capability => new PlatformRequirement(capability, null, pack.PackKey))
            .ToList();
        foreach (var content in pack.SeedItems)
        {
            // Per-item guard: an unparseable seed item declares no requirements — the projection loop's
            // existing per-item malformed handling refuses THAT item; one corrupted row must never abort
            // the whole scan (pre-160 it cost exactly one Invalid item, and this keeps that blast radius).
            JsonNode? parsed;
            try
            {
                parsed = content.ParseContent();
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            {
                continue;
            }

            AddEnvelopeRequirements(requirements, content.Key, parsed);
        }

        return FindUnmet(requirements, platform);
    }

    /// <summary>
    /// Missing capabilities dominate: when any exist, ONLY those return (a version floor on a capability
    /// the platform lacks entirely is noise); otherwise the version-floor failures return.
    /// </summary>
    private static IReadOnlyList<PackUnmetPlatformRequirement> FindUnmet(
        IReadOnlyList<PlatformRequirement> requirements, IPackPlatformCompatibility platform)
    {
        var missingCapabilities = requirements
            .Where(requirement => !platform.Provides.Contains(requirement.Capability))
            .Select(requirement => new PackUnmetPlatformRequirement(
                requirement.Capability,
                requirement.MinimumPlatformVersion,
                requirement.DeclaredBy,
                PackPlatformRequirementFailure.MissingCapability))
            .ToList();
        if (missingCapabilities.Count > 0)
        {
            return missingCapabilities;
        }

        return requirements
            .Where(requirement =>
                !string.IsNullOrWhiteSpace(requirement.MinimumPlatformVersion)
                && PackVersion.Compare(platform.PlatformVersion, requirement.MinimumPlatformVersion) < 0)
            .Select(requirement => new PackUnmetPlatformRequirement(
                requirement.Capability,
                requirement.MinimumPlatformVersion,
                requirement.DeclaredBy,
                PackPlatformRequirementFailure.PlatformVersionFloor))
            .ToList();
    }

    private static void AddEnvelopeRequirements(
        ICollection<PlatformRequirement> requirements,
        string contentKey,
        JsonNode? content)
    {
        if (content is not JsonObject root
            || root["definitionEnvelope"] is not JsonObject envelope
            || envelope["requires"] is not JsonArray declared)
        {
            return;
        }

        foreach (var node in declared.OfType<JsonObject>())
        {
            var capability = node["capability"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(capability)
                && !PackInterfaceRequirementCheck.TryParse(capability, out _, out _))
            {
                requirements.Add(new PlatformRequirement(
                    capability,
                    node["minimumPlatformVersion"]?.GetValue<string>(),
                    contentKey));
            }
        }
    }

    /// <summary>Same per-item guard as the installed-pack scan, for candidate content bytes.</summary>
    private static JsonNode? TryParse(ReadOnlySpan<byte> canonicalBytes)
    {
        try
        {
            return JsonNode.Parse(canonicalBytes);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private sealed record PlatformRequirement(
        string Capability,
        string? MinimumPlatformVersion,
        string DeclaredBy);
}
