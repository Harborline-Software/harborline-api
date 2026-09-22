using System.Collections.Frozen;
using System.Text;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;

namespace Harborline.Api.LocalNodeHost.Data.PackProjection;

/// <summary>
/// Projects the retired table-kind token carried by exact, immutable released pack predecessors onto
/// the canonical platform token. The installed seed item is never mutated; new and unknown pack
/// versions receive no compatibility treatment.
/// </summary>
public static class ReleasedViewKindCompatibility
{
    private const string RetiredTableKind = "views.entity-list/grid";

    private static readonly FrozenSet<(string PackKey, string Version)> ReleasedPredecessors = new[]
    {
        ("harborline.platform", "1.3.0"),
        ("harborline.platform", "1.6.0"),
        // 1.0.0 is the first Access release and the version the embedded seed export
        // (_shared/packs/access-administration) is installed as when an upgrade starts from the
        // previous package. That seed still carries the retired token, so omitting this pair
        // refused activation of the predecessor with view_definition.kind_unknown.
        ("harborline.access-administration", "1.0.0"),
        ("harborline.access-administration", "1.1.1"),
        ("harborline.access-administration", "1.1.2"),
        ("harborline.access-administration", "1.1.2-atomicity-probe.0"),
        ("harborline.access-administration", "1.1.3"),
        ("harborline.access-administration", "1.1.4"),
        ("harborline.access-administration", "1.1.4-atomicity-probe.0"),
    }.ToFrozenSet();

    /// <summary>Returns a transient canonical projection, or the original item when no ruling applies.</summary>
    public static PackSeedItem Project(string packKey, string packVersion, PackSeedItem item)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(packVersion);
        ArgumentNullException.ThrowIfNull(item);

        if (item.Kind != PackContentKind.ViewDefinition
            || !ReleasedPredecessors.Contains((packKey, packVersion)))
        {
            return item;
        }

        JsonObject? content;
        try
        {
            content = JsonNode.Parse(item.CanonicalJson) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return item;
        }

        if (content?["viewKind"] is not JsonValue kind
            || !kind.TryGetValue<string>(out var token)
            || !StringComparer.Ordinal.Equals(token, RetiredTableKind))
        {
            return item;
        }

        content["viewKind"] = Harborline.Blocks.EntityViews.ViewKindIds.Table;
        var canonicalJson = content.ToJsonString();
        return item with
        {
            CanonicalJson = canonicalJson,
            ContentAddress = Cid.FromBytes(Encoding.UTF8.GetBytes(canonicalJson)),
        };
    }
}
