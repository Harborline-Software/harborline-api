using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.ViewDefinitions;

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
public sealed class InMemoryRenderPlanCatalogue : IPackProjectionParticipant
{
    private ConcurrentDictionary<(string Tenant, string Hash), RenderPlan> plans = new();
    private ConcurrentDictionary<(string Tenant, PackContentKind Kind, string Id, string Version), string> hashes = new();

    public void StageProjection(PackProjectionTransaction transaction) => transaction.Stage(this, () =>
    {
        var oldPlans = plans;
        var oldHashes = hashes;
        var nextPlans = new ConcurrentDictionary<(string Tenant, string Hash), RenderPlan>(plans);
        var nextHashes = new ConcurrentDictionary<(string Tenant, PackContentKind Kind, string Id, string Version), string>(hashes);
        plans = nextPlans;
        hashes = nextHashes;
        return () => { plans = oldPlans; hashes = oldHashes; };
    });

    public void Store(TenantId tenant, PackContentKind kind, RenderPlan plan)
    {
        using var projectionLease = PackProjectionActivationBarrier.Read();
        ArgumentNullException.ThrowIfNull(plan);
        var detached = Detach(plan);
        plans[(tenant.Value, detached.DefinitionHash)] = detached;
        hashes[(tenant.Value, kind, detached.DefinitionId, detached.DefinitionVersion)] = detached.DefinitionHash;
    }

    public RenderPlan? Get(TenantId tenant, PackContentKind kind, string definitionId, string definitionVersion)
    {
        using var projectionLease = PackProjectionActivationBarrier.Read();
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
    public static async ValueTask<(RenderPlan? Plan, PackSeedProjectionRefusal? Refusal)> CompileOrRefuseAsync(
        PackSeedItem item,
        string packKey,
        string packVersion,
        string pointer)
    {
        var (plan, refusalCode) = await CompileAsync(item, packKey, packVersion).ConfigureAwait(false);
        if (plan is not null) return (plan, null);
        return (null, refusalCode switch
        {
            PackRenderPlanCodes.KindUnsupported =>
                new PackSeedProjectionRefusal(item.Key, item.Kind, PackRenderPlanCodes.KindUnsupported, pointer),
            PackRenderPlanCodes.BindingUnresolved =>
                new PackSeedProjectionRefusal(item.Key, item.Kind, PackRenderPlanCodes.BindingUnresolved, pointer),
            PackRenderPlanCodes.UnsupportedFieldKind =>
                new PackSeedProjectionRefusal(item.Key, item.Kind, PackRenderPlanCodes.UnsupportedFieldKind, pointer),
            _ => throw new InvalidOperationException($"render plan produced an undeclared refusal code: {refusalCode}"),
        });
    }

    /// <summary>Compiles a plan, or returns a null plan with the refusal code. Async because a value-domain
    /// field's editor is the field runtime's decision (T-752), and the runtime resolves asynchronously.</summary>
    public static async ValueTask<(RenderPlan? Plan, string RefusalCode)> CompileAsync(
        PackSeedItem item,
        string packKey,
        string packVersion)
    {
        if (item.Kind is not (PackContentKind.FormDefinition or PackContentKind.ViewDefinition))
        {
            return (null, PackRenderPlanCodes.KindUnsupported);
        }

        try
        {
            using var document = JsonDocument.Parse(item.CanonicalJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, PackRenderPlanCodes.BindingUnresolved);
            }

            var (bindings, refusalCode) = item.Kind == PackContentKind.FormDefinition
                ? await FormBindingsAsync(root).ConfigureAwait(false)
                : await ViewBindingsAsync(root).ConfigureAwait(false);
            if (bindings is null) return (null, refusalCode);

            return (new RenderPlan(
                Hash(item.CanonicalJson), item.Key, item.Version, packKey, packVersion, item.Kind.ToString(),
                bindings.Value,
                ReadOrEmpty(root, "sortAndFilterDefaults"),
                item.Kind == PackContentKind.FormDefinition ? ReadOrEmpty(root, "fieldsMeta") : JsonSerializer.SerializeToElement(new { }),
                JsonSerializer.SerializeToElement(new { code = "empty" }),
                JsonSerializer.SerializeToElement(new { code = "definition-unavailable" })), string.Empty);
        }
        catch (Exception exception) when (exception is JsonException or ViewDefinitionGovernanceException)
        {
            return (null, PackRenderPlanCodes.BindingUnresolved);
        }
    }

    private static async ValueTask<(JsonElement? Bindings, string RefusalCode)> FormBindingsAsync(JsonElement root)
    {
        if (!TryGetProperty(root, "fieldsMeta", out var fields) || fields.ValueKind != JsonValueKind.Object)
        {
            return (null, PackRenderPlanCodes.BindingUnresolved);
        }

        foreach (var field in fields.EnumerateObject())
        {
            if (!TryGetProperty(field.Value, "type", out var kind) || kind.ValueKind != JsonValueKind.String
                || !IsSupportedFieldKind(kind.GetString()))
            {
                return (null, PackRenderPlanCodes.UnsupportedFieldKind);
            }
        }

        var overlay = JsonNode.Parse(ReadOrEmpty(root, "overlay").GetRawText())!.AsObject();
        foreach (var field in fields.EnumerateObject())
        {
            if (!TryGetProperty(field.Value, "options", out var options) || options.ValueKind != JsonValueKind.Array) continue;
            var values = options.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
            if (values.Count == 0) continue;
            // T-752 (T-724 rulings 37, 64): a value-domain field renders the runtime's editor, the same as the
            // Forms wire; a signed pack's authored hint on such a field is overwritten, never passed through.
            var domain = await FieldEditorChoice.ResolveAsync(values, TimeProvider.System, $"/{field.Name}", CancellationToken.None)
                .ConfigureAwait(false);
            var overlayFields = overlay["fields"] as JsonObject ?? (JsonObject)(overlay["fields"] = new JsonObject());
            var presentation = overlayFields[field.Name] as JsonObject ?? (JsonObject)(overlayFields[field.Name] = new JsonObject());
            presentation["controlHint"] = domain.Editor.ToString();
            presentation["permittedValues"] = new JsonArray(domain.Values.Select(v => (JsonNode)v!).ToArray());
        }
        return (JsonSerializer.SerializeToElement(new { fields, overlay }), string.Empty);
    }

    private static async ValueTask<(JsonElement? Bindings, string RefusalCode)> ViewBindingsAsync(JsonElement root)
    {
        if (!TryGetProperty(root, "viewKind", out var kind)
            || kind.GetString() != Harborline.Blocks.EntityViews.ViewKindIds.Table
            || !TryGetProperty(root, "parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object
            || !TryGetProperty(parameters, "entityType", out var entityType) || entityType.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(entityType.GetString()))
        {
            return (null, PackRenderPlanCodes.BindingUnresolved);
        }

        var actions = new List<object>();
        if (TryGetProperty(parameters, "actions", out var declarations))
        {
            if (declarations.ValueKind != JsonValueKind.Array)
            {
                return (null, PackRenderPlanCodes.BindingUnresolved);
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
                    || (!action.TryGetProperty("dispatch", out _) && !IsSupportedOperation(operation.GetString())))
                {
                    return (null, PackRenderPlanCodes.BindingUnresolved);
                }
                if (action.TryGetProperty("dispatch", out var dispatch))
                {
                    _ = ViewRequestPresentationAdmission.Admit(action);
                    var request = HostViewRequestDescriptors.Resolve(dispatch);
                    JsonElement? input = null;
                    if (action.TryGetProperty("input", out var authoredInput))
                    {
                        var (compiled, refusalCode) = await FormBindingsAsync(authoredInput).ConfigureAwait(false);
                        if (compiled is null) return (null, refusalCode);
                        input = compiled;
                    }
                    actions.Add(new
                    {
                        id = id.GetString(), label = label.GetString(), operation = operation.GetString(),
                        dispatch = new { schemaVersion = 1, kind = "request", descriptor = request.Descriptor, bindings = request.Bindings },
                        input,
                        inputForm = action.TryGetProperty("inputForm", out var form) ? form : (JsonElement?)null,
                        fileInput = action.TryGetProperty("fileInput", out var file) ? file : (JsonElement?)null,
                        result = action.TryGetProperty("result", out var result) ? result : (JsonElement?)null,
                    });
                }
                else actions.Add(new { id = id.GetString(), label = label.GetString() });
            }
        }
        var dataSource = parameters.TryGetProperty("dataSource", out var source) ? HostViewRequestDescriptors.ResolveDataSource(source) : null;
        return (JsonSerializer.SerializeToElement(new { viewKind = kind.GetString(), entityType = entityType.GetString(), parameters, actions, dataSource },
            JsonSerializerOptions.Web), string.Empty);
    }

    private static bool IsSupportedFieldKind(string? kind) => kind is "text" or "number" or "checkbox"
        or "select" or "date" or "currency" or "email" or "phone" or "url" or "textarea";

    private static bool IsSupportedOperation(string? operation) => operation is
        "pack.validate" or "pack.export" or "pack.verify" or "pack.install" or "pack.activate"
        or "record.create" or "record.read"
        or "access.grant.submit" or "access.grant.review" or "access.holder.read"
        or "access.grant.narrow" or "access.grant.revoke" or "access.pack.replace";

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
