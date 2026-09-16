using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Foundation.ViewDefinitions;

/// <summary>The value shape a registered request accepts at one named input.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ViewRequestValueKind>))]
public enum ViewRequestValueKind
{
    Text,
    Number,
    Boolean,
    Object,
    Binary,
}

/// <summary>Closed host-owned serialization placements, never selectable by a pack binding.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ViewRequestPlacement>))]
public enum ViewRequestPlacement { Path, BodyField, BodyRoot, Header }

/// <summary>A required input whose wire name and placement belong to the host descriptor.</summary>
public sealed record ViewRequestInput(string Name, ViewRequestValueKind Kind,
    ViewRequestPlacement? Placement = null, string? WireName = null);

/// <summary>
/// Host-owned transport and enforcement metadata. A pack names <see cref="Id"/> and binds
/// values; it cannot author or override the route, audience, or authorization operation.
/// A descriptor is registered only alongside the route that implements these requirements.
/// </summary>
public sealed record ViewRequestDescriptor(
    string Id,
    string Method,
    string RouteTemplate,
    string ContentType,
    string Audience,
    bool RequiresAntiforgery,
    string AuthorizationCapability,
    IReadOnlyList<ViewRequestInput> Inputs)
{
    /// <summary>Host-owned top-level response-array pointer for a list source, or null for a non-list action.</summary>
    public string? RowsPointer { get; init; }

    /// <summary>Host-owned top-level stable identity pointer within each list row.</summary>
    public string? RowIdentityPointer { get; init; }
}

/// <summary>Source shapes resolved from the admitted row descriptor and input form.</summary>
public sealed record ViewRequestBindingSources(
    IReadOnlyDictionary<string, ViewRequestValueKind> Selection,
    IReadOnlyDictionary<string, ViewRequestValueKind> Input,
    bool HasInputObject = false,
    bool HasBinaryInput = false);

/// <summary>The admitted request binding, with transport metadata obtained from the host.</summary>
public sealed record CompiledViewRequest(
    ViewRequestDescriptor Descriptor,
    JsonElement Bindings);

/// <summary>
/// Admission for the bounded request binding inside a view action. It performs no authorization
/// decision and executes no request. The registered route retains its ordinary authority gate.
/// </summary>
public static class ViewRequestBindingAdmission
{
    private static readonly HashSet<string> DispatchProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion", "kind", "descriptorId", "bindings",
    };

    /// <summary>
    /// Resolves a request against a closed host registry and validates every source pointer and
    /// required input before the containing view can be activated. Unknown properties are refused,
    /// including attempts to supply a different route, method, actor, tenant, or enforcement gate.
    /// </summary>
    public static CompiledViewRequest Admit(
        JsonElement dispatch,
        IReadOnlyDictionary<string, ViewRequestDescriptor> descriptors,
        ViewRequestBindingSources sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var compiled = Resolve(dispatch, descriptors);
        foreach (var input in compiled.Descriptor.Inputs)
            Require(Kind(compiled.Bindings.GetProperty(input.Name), sources) == input.Kind);
        return compiled;
    }

    /// <summary>
    /// Resolves host transport metadata for a render plan. Activation must also call
    /// <see cref="Admit"/> with the actual admitted selection and input schemas.
    /// </summary>
    public static CompiledViewRequest Resolve(
        JsonElement dispatch,
        IReadOnlyDictionary<string, ViewRequestDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        Require(dispatch.ValueKind == JsonValueKind.Object);
        Require(UniqueProperties(dispatch, DispatchProperties));
        Require(dispatch.TryGetProperty("schemaVersion", out var version)
            && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) && number == 1);
        Require(Text(dispatch, "kind") == "request");
        var id = Text(dispatch, "descriptorId");
        Require(id is not null && descriptors.TryGetValue(id, out _));
        var descriptor = descriptors[id!];
        Require(ViewRequestTransportAdmission.IsValid(descriptor));
        Require(dispatch.TryGetProperty("bindings", out var bindings) && bindings.ValueKind == JsonValueKind.Object);
        var inputs = descriptor.Inputs.ToDictionary(input => input.Name, StringComparer.Ordinal);
        Require(UniqueProperties(bindings, inputs.Keys.ToHashSet(StringComparer.Ordinal)));
        Require(bindings.EnumerateObject().Count() == inputs.Count);
        foreach (var input in inputs.Values)
        {
            Require(bindings.TryGetProperty(input.Name, out var binding));
            Require(binding.ValueKind == JsonValueKind.Object);
        }
        return new CompiledViewRequest(descriptor, bindings.Clone());
    }

    private static ViewRequestValueKind? Kind(JsonElement binding, ViewRequestBindingSources sources)
    {
        if (binding.ValueKind != JsonValueKind.Object) return null;
        var properties = binding.EnumerateObject().ToArray();
        if (properties.Length == 1 && properties[0].Name == "literal")
        {
            return properties[0].Value.ValueKind switch
            {
                JsonValueKind.String => ViewRequestValueKind.Text,
                JsonValueKind.Number => ViewRequestValueKind.Number,
                JsonValueKind.True or JsonValueKind.False => ViewRequestValueKind.Boolean,
                JsonValueKind.Object => ViewRequestValueKind.Object,
                _ => null,
            };
        }
        if (properties.Length != 2 || !UniqueProperties(binding, new HashSet<string>(StringComparer.Ordinal) { "source", "pointer" }))
            return null;
        var source = Text(binding, "source");
        var pointer = Text(binding, "pointer");
        if (source == "invocation")
            return pointer is "/id" or "/idempotencyKey" or "/correlationId" ? ViewRequestValueKind.Text : null;
        if (source == "input" && pointer == "" && sources.HasInputObject) return ViewRequestValueKind.Object;
        if (source == "file" && pointer == "" && sources.HasBinaryInput) return ViewRequestValueKind.Binary;
        // v1 deliberately permits only exact top-level fields, not arbitrary object traversal.
        if (pointer is null || !pointer.StartsWith("/", StringComparison.Ordinal) || pointer.IndexOf('/', 1, StringComparison.Ordinal) >= 0) return null;
        var name = pointer[1..];
        for (var index = 0; index < name.Length; index++)
        {
            if (name[index] == '~' && (++index == name.Length || name[index] is not ('0' or '1'))) return null;
        }
        name = name.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        var fields = source switch { "selection" => sources.Selection, "input" => sources.Input, _ => null };
        return fields is not null && fields.TryGetValue(name, out var kind) ? kind : null;
    }

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static bool UniqueProperties(JsonElement value, HashSet<string> allowed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(property => allowed.Contains(property.Name) && seen.Add(property.Name));
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new ViewDefinitionGovernanceException("view_definition.request_binding_invalid");
    }
}
