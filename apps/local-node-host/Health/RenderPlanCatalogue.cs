using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;

using Harborline.Api.LocalNodeHost.Data.PackProjection;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>Stable, locale-independent render-plan codes (ticket 402; a client localizes off these).
/// Ticket 394's fence resolves every refusal code through a *Codes catalogue class -- a raw string
/// literal at the refusal site cannot resolve, which is what reddened the Ubuntu gate before these
/// existed. The class name ending in "Codes" is what makes it discoverable, not a convention.</summary>
public static class PackRenderPlanCodes
{
    /// <summary>The definition's kind has no render-plan shape (only forms and list views do).</summary>
    public const string KindUnsupported = "pack.render-plan.kind_unsupported";

    /// <summary>A binding in the definition did not resolve against the compiled catalogue.</summary>
    public const string BindingUnresolved = "pack.render-plan.binding_unresolved";

    /// <summary>A form field declares a kind the render plan has no shape for.</summary>
    public const string UnsupportedFieldKind = "pack.render-plan.unsupported_field_kind";
}

/// <summary>A lane-neutral render artifact emitted while a pack definition is activated.</summary>
public sealed record RenderPlan(
    string DefinitionHash,
    string DefinitionId,
    string DefinitionVersion,
    string PackKey,
    string PackVersion,
    string DefinitionKind,
    JsonElement Bindings,
    JsonElement SortAndFilterDefaults,
    JsonElement ValidationShape,
    JsonElement EmptyState,
    JsonElement ErrorState);

/// <summary>Stores emitted plans by definition hash and indexes them by definition coordinates.</summary>
public sealed class InMemoryRenderPlanCatalogue
{
    private readonly ConcurrentDictionary<(string Tenant, string Hash), RenderPlan> plans = new();
    private readonly ConcurrentDictionary<(string Tenant, PackContentKind Kind, string Id, string Version), string> hashes = new();

    public void Store(TenantId tenant, PackContentKind kind, RenderPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var detached = Detach(plan);
        plans[(tenant.Value, detached.DefinitionHash)] = detached;
        hashes[(tenant.Value, kind, detached.DefinitionId, detached.DefinitionVersion)] = detached.DefinitionHash;
    }

    public RenderPlan? Get(TenantId tenant, PackContentKind kind, string definitionId, string definitionVersion)
    {
        if (!hashes.TryGetValue((tenant.Value, kind, definitionId, definitionVersion), out var hash)
            || !plans.TryGetValue((tenant.Value, hash), out var plan))
        {
            return null;
        }

        return Detach(plan);
    }

    private static RenderPlan Detach(RenderPlan plan) => plan with
    {
        Bindings = plan.Bindings.Clone(),
        SortAndFilterDefaults = plan.SortAndFilterDefaults.Clone(),
        ValidationShape = plan.ValidationShape.Clone(),
        EmptyState = plan.EmptyState.Clone(),
        ErrorState = plan.ErrorState.Clone(),
    };
}

/// <summary>Compiles the canonical signed definition body into the shared lane-neutral plan IR.</summary>
public static class RenderPlanCompiler
{
    /// <summary>
    /// The hash input is the pack item's canonical UTF-8 JSON, not this derived plan. The pack canonicalizer
    /// owns property ordering and escaping, so the key changes exactly when the signed definition changes.
    /// </summary>
    /// <summary>
    /// Compiles a render plan, or produces the refusal ITSELF rather than a code the caller must
    /// re-wrap. Ticket 394's fence resolves a refusal's code statically at the construction site, and
    /// an out-variable carried across a call boundary cannot be resolved -- the projector's
    /// `new PackSeedProjectionRefusal(..., code, ...)` was unresolvable for exactly that reason. The
    /// construction belongs where the codes are constants, which is here.
    /// </summary>
    public static PackSeedProjectionRefusal? CompileOrRefuse(
        PackSeedItem item,
        string packKey,
        string packVersion,
        string pointer,
        out RenderPlan? plan)
    {
        var refusalCode = string.Empty;
        if (TryCompile(item, packKey, packVersion, out plan, out refusalCode)) return null;
        return refusalCode switch
        {
            PackRenderPlanCodes.KindUnsupported =>
                new PackSeedProjectionRefusal(item.Key, item.Kind, PackRenderPlanCodes.KindUnsupported, pointer),
            PackRenderPlanCodes.BindingUnresolved =>
                new PackSeedProjectionRefusal(item.Key, item.Kind, PackRenderPlanCodes.BindingUnresolved, pointer),
            PackRenderPlanCodes.UnsupportedFieldKind =>
                new PackSeedProjectionRefusal(item.Key, item.Kind, PackRenderPlanCodes.UnsupportedFieldKind, pointer),
            _ => throw new InvalidOperationException($"render plan produced an undeclared refusal code: {refusalCode}"),
        };
    }

    public static bool TryCompile(
        PackSeedItem item,
        string packKey,
        string packVersion,
        out RenderPlan? plan,
        out string refusalCode)
    {
        plan = null;
        refusalCode = string.Empty;
        if (item.Kind is not (PackContentKind.FormDefinition or PackContentKind.ViewDefinition))
        {
            refusalCode = PackRenderPlanCodes.KindUnsupported;
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(item.CanonicalJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                refusalCode = PackRenderPlanCodes.BindingUnresolved;
                return false;
            }

            var bindings = item.Kind == PackContentKind.FormDefinition
                ? FormBindings(root, out refusalCode)
                : ViewBindings(root, out refusalCode);
            if (bindings is null) return false;

            plan = new RenderPlan(
                Hash(item.CanonicalJson), item.Key, item.Version, packKey, packVersion, item.Kind.ToString(),
                bindings.Value,
                ReadOrEmpty(root, "sortAndFilterDefaults"),
                item.Kind == PackContentKind.FormDefinition ? ReadOrEmpty(root, "fieldsMeta") : JsonSerializer.SerializeToElement(new { }),
                JsonSerializer.SerializeToElement(new { code = "empty" }),
                JsonSerializer.SerializeToElement(new { code = "definition-unavailable" }));
            return true;
        }
        catch (JsonException)
        {
            refusalCode = PackRenderPlanCodes.BindingUnresolved;
            return false;
        }
    }

    private static JsonElement? FormBindings(JsonElement root, out string refusalCode)
    {
        refusalCode = string.Empty;
        if (!TryGetProperty(root, "fieldsMeta", out var fields) || fields.ValueKind != JsonValueKind.Object)
        {
            refusalCode = PackRenderPlanCodes.BindingUnresolved;
            return null;
        }

        foreach (var field in fields.EnumerateObject())
        {
            if (!TryGetProperty(field.Value, "type", out var kind) || kind.ValueKind != JsonValueKind.String
                || !IsSupportedFieldKind(kind.GetString()))
            {
                refusalCode = PackRenderPlanCodes.UnsupportedFieldKind;
                return null;
            }
        }

        var overlay = ReadOrEmpty(root, "overlay");
        return JsonSerializer.SerializeToElement(new { fields, overlay });
    }

    private static JsonElement? ViewBindings(JsonElement root, out string refusalCode)
    {
        refusalCode = string.Empty;
        if (!TryGetProperty(root, "viewKind", out var kind) || kind.GetString() != "views.entity-list/grid"
            || !TryGetProperty(root, "parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object
            || !TryGetProperty(parameters, "entityType", out var entityType) || entityType.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(entityType.GetString()))
        {
            refusalCode = PackRenderPlanCodes.BindingUnresolved;
            return null;
        }

        var actions = new List<object>();
        if (TryGetProperty(parameters, "actions", out var declarations))
        {
            if (declarations.ValueKind != JsonValueKind.Array)
            {
                refusalCode = PackRenderPlanCodes.BindingUnresolved;
                return null;
            }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var action in declarations.EnumerateArray())
            {
                if (action.ValueKind != JsonValueKind.Object
                    || !TryGetProperty(action, "id", out var id) || id.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(id.GetString()) || !ids.Add(id.GetString()!)
                    || !TryGetProperty(action, "label", out var label) || label.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(label.GetString())
                    || !TryGetProperty(action, "operation", out var operation) || operation.ValueKind != JsonValueKind.String
                    || operation.GetString() is not ("pack.validate" or "pack.export" or "pack.verify" or "pack.check"
                        or "pack.install" or "pack.activate" or "record.create" or "record.read"))
                {
                    refusalCode = PackRenderPlanCodes.BindingUnresolved;
                    return null;
                }
                actions.Add(new { id = id.GetString(), label = label.GetString() });
            }
        }
        return JsonSerializer.SerializeToElement(new { viewKind = kind.GetString(), entityType = entityType.GetString(), parameters, actions });
    }

    private static bool IsSupportedFieldKind(string? kind) => kind is "text" or "number" or "checkbox"
        or "select" or "date" or "currency" or "email" or "phone" or "url" or "textarea";

    private static JsonElement ReadOrEmpty(JsonElement root, string name) => TryGetProperty(root, name, out var value)
        ? value.Clone()
        : JsonSerializer.SerializeToElement(new { });

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value)) return true;
        foreach (var property in element.EnumerateObject())
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(property.Name, name))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string Hash(string canonicalJson) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant();
}
