using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;

namespace Harborline.Api.LocalNodeHost.Health;

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
public interface IRenderPlanCatalogue
{
    void Store(TenantId tenant, PackContentKind kind, RenderPlan plan);

    RenderPlan? Get(TenantId tenant, PackContentKind kind, string definitionId, string definitionVersion);
}

/// <summary>In-memory reference catalogue for activation-produced render plans.</summary>
public sealed class InMemoryRenderPlanCatalogue : IRenderPlanCatalogue
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
            refusalCode = "pack.render-plan.kind_unsupported";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(item.CanonicalJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                refusalCode = "pack.render-plan.binding_unresolved";
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
            refusalCode = "pack.render-plan.binding_unresolved";
            return false;
        }
    }

    private static JsonElement? FormBindings(JsonElement root, out string refusalCode)
    {
        refusalCode = string.Empty;
        if (!TryGetProperty(root, "fieldsMeta", out var fields) || fields.ValueKind != JsonValueKind.Object)
        {
            refusalCode = "pack.render-plan.binding_unresolved";
            return null;
        }

        foreach (var field in fields.EnumerateObject())
        {
            if (!TryGetProperty(field.Value, "type", out var kind) || kind.ValueKind != JsonValueKind.String
                || !IsSupportedFieldKind(kind.GetString()))
            {
                refusalCode = "pack.render-plan.unsupported_field_kind";
                return null;
            }
        }

        return JsonSerializer.SerializeToElement(new { fields });
    }

    private static JsonElement? ViewBindings(JsonElement root, out string refusalCode)
    {
        refusalCode = string.Empty;
        if (!TryGetProperty(root, "viewKind", out var kind) || kind.GetString() != "views.entity-list/grid"
            || !TryGetProperty(root, "parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object
            || !TryGetProperty(parameters, "entityType", out var entityType) || entityType.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(entityType.GetString()))
        {
            refusalCode = "pack.render-plan.binding_unresolved";
            return null;
        }

        return JsonSerializer.SerializeToElement(new { entityType = entityType.GetString(), parameters });
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
