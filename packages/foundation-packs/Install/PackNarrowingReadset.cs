using System.Text.Json;
using System.Text.Json.Nodes;

namespace Harborline.Api.Foundation.Packs.Install;

/// <summary>Frozen active definition and tenant overlays used to admit a narrowing.</summary>
public sealed class PackNarrowingReadset
{
    private readonly string activeJson;
    private readonly Dictionary<string, string> overrides;

    public PackNarrowingReadset(InstalledPack active, IReadOnlyList<PackTenantOverride> overrides)
    {
        activeJson = JsonSerializer.Serialize(active);
        this.overrides = overrides.ToDictionary(row => row.ContentKey, row => row.OverlayPatch.ToJsonString(), StringComparer.Ordinal);
    }

    /// <summary>Returns a detached copy of the admitted active definition.</summary>
    public InstalledPack CopyActive() => JsonSerializer.Deserialize<InstalledPack>(activeJson)!;

    /// <summary>Returns detached copies of every overlay used by composition admission.</summary>
    public IReadOnlyList<PackTenantOverride> CopyOverrides()
        => overrides.Select(pair => new PackTenantOverride(pair.Key, JsonNode.Parse(pair.Value)!)).ToArray();

    /// <summary>Refuses changed premises before the store mutates rows or stages the success audit.</summary>
    public void RequireCurrent(InstalledPack? active, IReadOnlyList<PackTenantOverride> currentOverrides)
    {
        if (active is null || !JsonNode.DeepEquals(JsonNode.Parse(activeJson), JsonSerializer.SerializeToNode(active))
            || currentOverrides.Count != overrides.Count)
            throw new PackInstallStateChangedException();
        foreach (var row in currentOverrides)
            if (!overrides.TryGetValue(row.ContentKey, out var patch)
                || !JsonNode.DeepEquals(JsonNode.Parse(patch), row.OverlayPatch))
                throw new PackInstallStateChangedException();
    }
}
