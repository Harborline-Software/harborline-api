using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Graph;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.LocalNodeHost.Data.AssetRegistry;
using Harborline.Blocks.BuilderDefinitions;
using Harborline.Blocks.LayoutRuntime;
using Harborline.Kernel.SchemaValidation.Records;

namespace Harborline.Api.LocalNodeHost.Layout;

/// <summary>The stable codes the Layout surface route refuses with (T-735).</summary>
public static class LayoutSurfaceCodes
{
    /// <summary>No Active installed pack carries a Layout surface with this identity.</summary>
    public const string SurfaceUnknown = "layout.surface.unknown";

    /// <summary>More than one Active installed pack carries a Layout surface with this identity.</summary>
    public const string SurfaceAmbiguous = "layout.surface.ambiguous";

    /// <summary>The root record does not exist. Only a caller who may read it is told so.</summary>
    public const string RootUnknown = "layout.surface.root_unknown";

    /// <summary>A related block's key names no single-target Records reference declared on the root's Record Type.</summary>
    public const string RelationshipUndeclared = "layout.surface.relationship_undeclared";

    /// <summary>A related block's key names a many-target reference, which is a repeating block (layout-ck-27).</summary>
    public const string RelationshipManyTarget = "layout.surface.relationship_many_target";
}

/// <summary>The root record a surface resolves for: its Record Type and its own values.</summary>
/// <param name="RecordTypeId">The root's Record Type identity, which names the relationships it declares.</param>
/// <param name="Values">The root's values, keyed by field.</param>
public sealed record LayoutRootRecord(string RecordTypeId, IReadOnlyDictionary<string, JsonNode?> Values);

/// <summary>Reads a surface's root record. The route calls it only after the caller may read that record.</summary>
public interface ILayoutRootRecordSource
{
    /// <summary>The root record <paramref name="recordId"/> in <paramref name="tenant"/>, or null when it does not exist.</summary>
    ValueTask<LayoutRootRecord?> FindAsync(TenantId tenant, string recordId, CancellationToken ct = default);
}

/// <summary>
/// The production root: a registry entity, typed by its entity type, with the canonical values bound to it
/// (<see cref="PackBoundRegistryRecordWriter.ReadValuesAsync"/>). An entity with no bound values has none.
/// </summary>
public sealed class RegistryLayoutRootRecordSource(IRegistryEntityRepository entities, PackBoundRegistryRecordWriter records)
    : ILayoutRootRecordSource
{
    /// <inheritdoc />
    public async ValueTask<LayoutRootRecord?> FindAsync(TenantId tenant, string recordId, CancellationToken ct = default)
    {
        var entity = await entities.GetByIdAsync(tenant, recordId, ct).ConfigureAwait(false);
        if (entity is null) return null;
        var values = await records.ReadValuesAsync(tenant, entity, ct).ConfigureAwait(false);
        return new LayoutRootRecord(entity.Type.Value, LayoutHostBindingSources.ValuesOf(values));
    }
}

/// <summary>An Active installed pack's Layout surface: the pack that carries it and its admitted definition.</summary>
internal sealed record InstalledLayoutSurface(InstalledPack Pack, LayoutDefinition Definition);

/// <summary>Finds installed Layout surfaces and the Record Types their packs' signed closures declare.</summary>
internal static class InstalledLayoutSurfaces
{
    /// <summary>
    /// The Active surface whose definition identity is <paramref name="surfaceId"/>. More than one Active pack
    /// carrying it is ambiguous and none is chosen.
    /// </summary>
    internal static (InstalledLayoutSurface? Surface, bool Ambiguous) Find(IPackInstallStore store, TenantId tenant, string surfaceId)
    {
        var found = new List<InstalledLayoutSurface>();
        foreach (var pack in store.ListInstalled(tenant).Where(pack => pack.Lifecycle == PackLifecycleState.Active))
        foreach (var item in pack.SeedItems.Where(item => item.Kind == PackContentKind.Layout))
        {
            // Install admitted every Layout seed through the platform's Layout admission, so a seed that no
            // longer parses is not a surface this host can serve; it is skipped, never repaired.
            if (TryParse(item, out var definition) && string.Equals(definition.Envelope.Identity, surfaceId, StringComparison.Ordinal))
                found.Add(new InstalledLayoutSurface(pack, definition));
        }
        return found.Count switch
        {
            1 => (found[0], false),
            0 => (null, false),
            _ => (null, true),
        };
    }

    /// <summary>
    /// DES-0052 layout-ck-39: the Record Type <paramref name="recordTypeId"/> as the surface pack's signed closure
    /// declares it, that is the pack itself and the exact dependency versions it was signed against. Live catalogue
    /// state is never read. Null when the closure declares it zero times or more than once.
    /// </summary>
    internal static RecordTypeDocument? RecordType(IPackInstallStore store, TenantId tenant, InstalledPack pack, string recordTypeId)
    {
        var closure = new List<InstalledPack> { pack };
        closure.AddRange(pack.Dependencies
            .Select(dependency => store.GetVersion(tenant, dependency.Key, dependency.Version))
            .OfType<InstalledPack>());
        var matches = closure
            .SelectMany(member => member.SeedItems)
            .Where(item => item.Kind == PackContentKind.AssetTypeDefinition)
            .Select(item => TryParseRecordType(item, recordTypeId))
            .OfType<RecordTypeDocument>()
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    // A seed is immutable, so its parse is too: each distinct seed body is parsed once per process rather than on every
    // request. The key is the canonical body itself, not its content address, so a cached parse can only ever answer for
    // the exact bytes it was made from, however the seed item was built. The caches hold one entry per distinct body.
    private static readonly ConcurrentDictionary<string, LayoutDefinition?> s_surfaces = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, (string? Id, RecordTypeDocument? Document)> s_recordTypes =
        new(StringComparer.Ordinal);

    private static bool TryParse(PackSeedItem item, out LayoutDefinition definition)
    {
        definition = s_surfaces.GetOrAdd(item.CanonicalJson, static json =>
        {
            try
            {
                return LayoutDefinitionJson.Deserialize(Encoding.UTF8.GetBytes(json));
            }
            catch (JsonException)
            {
                return null;
            }
        })!;
        return definition is not null;
    }

    private static RecordTypeDocument? TryParseRecordType(PackSeedItem item, string recordTypeId)
    {
        var (id, document) = s_recordTypes.GetOrAdd(item.CanonicalJson, static (json, kind) =>
        {
            try
            {
                var recordsId = PackContentReferenceExtractor.RecordsId(kind, JsonNode.Parse(json));
                return recordsId is { Length: > 0 }
                    ? (recordsId, RecordTypeDefinitionJson.Deserialize(Encoding.UTF8.GetBytes(json)))
                    : (recordsId, null);
            }
            catch (JsonException)
            {
                return (null, null);
            }
        }, item.Kind);
        return id == recordTypeId ? document : null;
    }
}

/// <summary>
/// T-735, owner ruling of 2026-11-04 (DES-0052 layout-ck-28): the related-block relationships a surface may traverse
/// from its root, built from the Records reference fields the root's Record Type declares. A key is the root's Record
/// Type id and the field key, joined by a dot (<c>finance.invoice.owner</c>), so the key names its source type as the
/// source Record Type pin would. Only a single-target reference to one Record Type binds a related block.
/// </summary>
internal static class LayoutRelationshipMap
{
    /// <summary>Why a surface's relationships do not bind on this root: the stable code and the authored key.</summary>
    internal sealed record Refusal(string Code, string Relationship);

    /// <summary>
    /// The traversal for every relationship <paramref name="definition"/> authors, or the first relationship, in
    /// document order, that the root's Record Type does not declare as a single-target reference. A related block
    /// inside a related or repeating block resolves from a scope that is not the root, so no root declaration
    /// covers it and it refuses the same way.
    /// </summary>
    internal static IReadOnlyDictionary<string, Func<LayoutBindingScope, LayoutRelatedRecord?>> Build(
        LayoutDefinition definition,
        RecordTypeDocument? rootType,
        LayoutRelatedRecordRequestCache targets,
        out Refusal? refusal)
    {
        var map = new Dictionary<string, Func<LayoutBindingScope, LayoutRelatedRecord?>>(StringComparer.Ordinal);
        refusal = Walk(definition.Blocks ?? [], fromRoot: true, rootType, targets, map);
        return map;
    }

    private static Refusal? Walk(
        IReadOnlyList<LayoutBlock> blocks, bool fromRoot, RecordTypeDocument? rootType,
        LayoutRelatedRecordRequestCache targets, Dictionary<string, Func<LayoutBindingScope, LayoutRelatedRecord?>> map)
    {
        foreach (var block in blocks)
        {
            var related = block.RelatedRelationship is { Length: > 0 } key ? key : null;
            // Every occurrence is checked for its scope, so a key mapped at the root still refuses when it is
            // authored again below a related or repeating block.
            if (related is not null && !fromRoot)
                return new Refusal(LayoutSurfaceCodes.RelationshipUndeclared, related);
            if (related is not null && !map.ContainsKey(related))
            {
                var source = rootType?.RecordTypeId + ".";
                var reference = (rootType?.Fields ?? []).FirstOrDefault(field => field.Reference is not null
                    && string.Equals(source + field.FieldKey, related, StringComparison.Ordinal));
                if (reference?.Reference is not { TargetTypeId: { Length: > 0 } targetType } declaration)
                    return new Refusal(LayoutSurfaceCodes.RelationshipUndeclared, related);
                if (declaration.Cardinality != ReferenceCardinality.One)
                    return new Refusal(LayoutSurfaceCodes.RelationshipManyTarget, related);
                map[related] = Traversal(reference.FieldKey, targetType, targets);
            }
            if (Walk(block.Children ?? [], fromRoot && related is null && !block.Repeating, rootType, targets, map) is { } inner)
                return inner;
        }
        return null;
    }

    /// <summary>
    /// Reads the root's qualified reference value (<c>{"type", "id"}</c>, records-ck-10) and looks the target up.
    /// The declaration supplies the target type: a value naming another type, or no value, has no target.
    /// </summary>
    private static Func<LayoutBindingScope, LayoutRelatedRecord?> Traversal(
        string fieldKey, string targetType, LayoutRelatedRecordRequestCache targets) => scope =>
    {
        if (!scope.Values.TryGetValue(fieldKey, out var value) || value is not JsonObject qualified
            || !TryText(qualified["type"], out var type) || !string.Equals(type, targetType, StringComparison.Ordinal)
            || !TryText(qualified["id"], out var id) || !IsAddressable(id))
            return null;
        var target = new LayoutRecordReference(targetType, id);
        return targets.Find(target) is { } values
            ? new LayoutRelatedRecord(target, new LayoutBindingScope(null, null, values))
            : null;
    };

    /// <summary>
    /// Whether the gate can address <paramref name="id"/> as a record (<c>/records/{id}</c>). An id it cannot address
    /// has no target, decided before the lookup: if it reached the gate only when the record existed, the gate's
    /// refusal of it would answer, outside the floor, whether the record exists.
    /// </summary>
    /// <remarks>
    /// It asks the gate's own grammar, building the same <c>/records/{id}</c> scope the gate builds, so it refuses
    /// exactly what the gate would (a separator, <c>?</c>, <c>#</c>, a backslash, a dot segment) and no more.
    /// </remarks>
    private static bool IsAddressable(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Contains('/', StringComparison.Ordinal)) return false;
        try
        {
            _ = ScopeExpression.Parse($"/records/{id}");
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryText(JsonNode? node, out string text)
    {
        text = "";
        return node is JsonValue value && value.TryGetValue(out text!);
    }
}

/// <summary>
/// The host's binding sources for one resolution: record fields and record-field collections read from the scope's
/// values. Queries, measures and templates have no host source in this slice, so they refuse as unresolvable
/// (layout-run-4). Related traversals are answered by <see cref="LayoutSurfaceHost"/> from the relationship map, so
/// this source never declares one.
/// </summary>
internal sealed class LayoutHostBindingSources : ILayoutBindingSources
{
    /// <summary>The one instance; it holds no state.</summary>
    internal static LayoutHostBindingSources Instance { get; } = new();

    private LayoutHostBindingSources()
    {
    }

    /// <summary>The top-level members of a record's values as a scope dictionary; anything but an object has none.</summary>
    internal static IReadOnlyDictionary<string, JsonNode?> ValuesOf(JsonElement? values)
    {
        var scope = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (values is { ValueKind: JsonValueKind.Object } record)
            foreach (var member in record.EnumerateObject())
                scope[member.Name] = JsonNode.Parse(member.Value.GetRawText());
        return scope;
    }

    /// <summary>
    /// <paramref name="values"/> with every field <paramref name="type"/> declares present: a declared field with no
    /// value resolves to null rather than refusing (<see cref="ILayoutBindingSources.ResolveField"/>).
    /// </summary>
    internal static IReadOnlyDictionary<string, JsonNode?> WithDeclaredFields(IReadOnlyDictionary<string, JsonNode?> values, RecordTypeDocument? type)
    {
        if (type is null) return values;
        var scope = new Dictionary<string, JsonNode?>(values, StringComparer.Ordinal);
        foreach (var field in type.Fields ?? []) scope.TryAdd(field.FieldKey, null);
        return scope;
    }

    public LayoutFieldResult ResolveField(LayoutBindingScope scope, string fieldPath)
        => TryRead(scope, fieldPath, out var value) ? LayoutFieldResult.Resolved(value?.DeepClone()) : LayoutFieldResult.Undeclared;

    public bool TryResolveQuery(LayoutBindingScope scope, string viewDefinitionId, out JsonNode? value) => None(out value);

    public bool TryResolveMeasure(LayoutBindingScope scope, string measurePath, out JsonNode? value) => None(out value);

    public bool TryResolveTemplate(LayoutBindingScope scope, string templateDefinitionId, string templateVersion, out JsonNode? value)
        => None(out value);

    public bool TryResolveCollection(LayoutBindingScope scope, string name, out IReadOnlyList<JsonNode?> rows)
    {
        if (TryRead(scope, name, out var value) && value is JsonArray array)
        {
            rows = array.Select(row => row?.DeepClone()).ToArray();
            return true;
        }
        rows = [];
        return false;
    }

    public LayoutRelatedResult ResolveRelated(LayoutBindingScope scope, string relationship) => LayoutRelatedResult.Undeclared;

    /// <summary>
    /// A dotted path: the first segment must be a field of the scope, and each further segment is a member of the
    /// object before it. A missing member below a declared field is a declared field with no value.
    /// </summary>
    private static bool TryRead(LayoutBindingScope scope, string path, out JsonNode? value)
    {
        value = null;
        if (scope.Values is null || string.IsNullOrEmpty(path)) return false;
        var segments = path.Split('.');
        if (!scope.Values.TryGetValue(segments[0], out value)) return false;
        for (var index = 1; index < segments.Length && value is not null; index++)
            value = value is JsonObject member ? member[segments[index]] : null;
        return true;
    }

    private static bool None(out JsonNode? value)
    {
        value = null;
        return false;
    }
}
