using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Health;

public sealed record CatalogueDetailProjection(string DetailId, string DetailVersion,
    CatalogueFieldSourceBinding DetailBinding, JsonElement Overlay,
    IReadOnlyDictionary<string, JsonElement> FieldsMeta, IReadOnlyDictionary<string, JsonElement> Values)
{
    public bool ReadOnly { get; } = true;
    [JsonIgnore] public IReadOnlyList<AuthorizationDecision> Denials { get; init; } = [];
}

/// <summary>Templates captured from successful authentic pack projection, not client declarations.</summary>
public sealed class CatalogueDetailTemplates : IPackProjectionParticipant
{
    private ConcurrentDictionary<(TenantId, string, string), Template> templates = new();
    public void StageProjection(PackProjectionTransaction transaction) => transaction.Stage(this, () =>
    {
        var before = templates;
        var next = new ConcurrentDictionary<(TenantId, string, string), Template>(before);
        templates = next;
        return () => templates = before;
    });
    internal sealed record Template(JsonElement Content, CatalogueFormSourceIdentity Identity, CatalogueFieldSourceBinding SeedBinding);
    internal void Store(JsonElement content, CatalogueFormSourceIdentity identity, CatalogueFieldSourceBinding seedBinding)
    {
        using var projectionLease = PackProjectionActivationBarrier.Read();
        templates[(identity.Tenant, identity.Id, identity.Version)] = new(content.Clone(), identity, seedBinding);
    }
    internal Template? Get(TenantId tenant, string id, string version)
    {
        using var projectionLease = PackProjectionActivationBarrier.Read();
        return templates.GetValueOrDefault((tenant, id, version));
    }
}

/// <summary>Fresh read-only projections over the admitted template and exact one-field gate decisions.</summary>
public sealed class CatalogueDetailRuntime(ICatalogueFormSources sources, CatalogueDetailTemplates templates, AuthorizationGate gate)
{
    public static bool Supports(CatalogueFieldSource declaration) => declaration.CapabilityId == CatalogueFieldSourceContract.CapabilityId
        && declaration.CoordinateSchemaVersion == 1 && declaration.SourceMappingSchemaVersion == 1
        && declaration.SourceKind == CatalogueFieldSourceContract.SourceKind;

    public async ValueTask<CatalogueDetailProjection> ProjectAsync(string detailId, string detailVersion,
        JsonElement request, AuthorizationWriteContext authority,
        Func<AuthorizationDecision, CancellationToken, ValueTask>? denied = null, CancellationToken ct = default)
    {
        var template = templates.Get(authority.Tenant, detailId, detailVersion)
            ?? throw new CatalogueFieldSourceException(CatalogueFieldSourceCodes.SourceVersionUnavailable);
        var mapping = CatalogueFieldSourceAdmission.ParseContent(template.Content)
            ?? throw new CatalogueFieldSourceException(CatalogueFieldSourceCodes.LegacyMappingAbsent);
        if (request.ValueKind != JsonValueKind.Array || request.GetArrayLength() is < 1 or > 4)
            throw new CatalogueFieldSourceException(CatalogueFieldSourceCodes.MalformedCoordinate);
        var requests = request.EnumerateArray().Select(CatalogueFieldSourceContract.ParseRequest).ToArray();
        var parsed = requests[0];
        var previous = -1;
        foreach (var fieldRequest in requests)
        {
            var index = mapping.Fields.Select(field => field.FieldId).ToList().IndexOf(fieldRequest.Coordinate.Field);
            if (index <= previous)
                throw new CatalogueFieldSourceException(CatalogueFieldSourceCodes.MalformedCoordinate);
            previous = index;
            if (fieldRequest.Coordinate.Kind != parsed.Coordinate.Kind || fieldRequest.Coordinate.Id != parsed.Coordinate.Id
                || fieldRequest.Coordinate.Version != parsed.Coordinate.Version || fieldRequest.SourceBinding != parsed.SourceBinding)
                throw new CatalogueFieldSourceException(CatalogueFieldSourceCodes.SourceBindingMismatch);
        }
        var detailCoordinate = new CatalogueFieldCoordinate(1, "FormDefinition", detailId, detailVersion, "formId");
        if (sources.Resolve(authority.Tenant, detailCoordinate)?.Identity != template.Identity)
            throw new CatalogueFieldSourceException(CatalogueFieldSourceCodes.SourceVersionUnavailable);
        var handle = sources.Resolve(authority.Tenant, parsed.Coordinate)
            ?? throw new CatalogueFieldSourceException(CatalogueFieldSourceCodes.SourceVersionUnavailable);
        var identity = handle.Identity;
        if (identity.Tenant != authority.Tenant || identity.Kind != parsed.Coordinate.Kind
            || identity.Id != parsed.Coordinate.Id || identity.Version != parsed.Coordinate.Version
            || identity.Binding != parsed.SourceBinding)
            throw new CatalogueFieldSourceException(CatalogueFieldSourceCodes.SourceBindingMismatch);

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var denials = new List<AuthorizationDecision>();
        foreach (var fieldRequest in requests)
        {
            var coordinate = fieldRequest.Coordinate;
            var read = handle.Bind(coordinate, fieldRequest.SourceBinding);
            var scope = new CatalogueFieldTarget(coordinate.Kind, coordinate.Id, coordinate.Version, coordinate.Field).Scope;
            var operation = AuthorizationOperation.Parse(Permission.CatalogueRead);
            var decision = await gate.DecideAsync(new AuthorizationGateRequest(new PermissionAtom(operation, scope),
                authority.Principal, authority.Tenant, new AuthorizationTarget("catalogue", coordinate.Id, scope), authority.At), ct)
                .ConfigureAwait(false);
            if (decision.Verdict != AuthorizationVerdict.Allowed)
            {
                denials.Add(decision);
                if (denied is not null) await denied(decision, ct).ConfigureAwait(false);
                continue;
            }
            ct.ThrowIfCancellationRequested();
            if (sources.Resolve(authority.Tenant, detailCoordinate)?.Identity != template.Identity)
                throw new CatalogueFieldSourceException(CatalogueFieldSourceCodes.SourceChangedAfterAuthorization);
            var payload = read();
            if (payload.Identity != identity || payload.Coordinate != coordinate || !ValidValue(coordinate, payload.Value))
                throw new CatalogueFieldSourceException(CatalogueFieldSourceCodes.PayloadBindingMismatch);
            values.Add(coordinate.Field, payload.Value.Clone());
        }

        var authored = template.Content.GetProperty("overlay");
        var fields = new JsonObject();
        var metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in mapping.Fields.Where(field => values.ContainsKey(field.FieldId)))
        {
            fields[field.FieldId] = JsonNode.Parse(authored.GetProperty("fields").GetProperty(field.FieldId).GetRawText());
            metadata[field.FieldId] = template.Content.GetProperty("fieldsMeta").GetProperty(field.FieldId).Clone();
        }
        var sections = new JsonArray();
        foreach (var section in authored.GetProperty("sections").EnumerateArray())
        {
            var projected = JsonNode.Parse(section.GetRawText())!.AsObject();
            projected["fields"] = JsonSerializer.SerializeToNode(section.GetProperty("fields").EnumerateArray()
                .Select(field => field.GetString()!).Where(values.ContainsKey).ToArray());
            projected.Remove("items");
            if (projected["fieldPlacement"] is JsonObject placements)
                foreach (var key in placements.Select(pair => pair.Key).Where(key => !values.ContainsKey(key)).ToArray()) placements.Remove(key);
            sections.Add(projected);
        }
        var overlay = new JsonObject { ["fields"] = fields, ["sections"] = sections, ["rules"] = new JsonArray() };
        // Detail chrome is independent of the selected source. Source identity is never echoed here.
        if (authored.TryGetProperty("title", out var title)) overlay["title"] = JsonNode.Parse(title.GetRawText());
        return new CatalogueDetailProjection(detailId, detailVersion, template.SeedBinding,
            JsonSerializer.SerializeToElement(overlay), metadata, values) { Denials = denials };
    }

    private static bool ValidValue(CatalogueFieldCoordinate coordinate, JsonElement value)
    {
        if (coordinate.Field == "title")
        {
            if (value.ValueKind == JsonValueKind.Null) return true;
            if (value.ValueKind != JsonValueKind.Object) return false;
            try
            {
                CatalogueFieldSourceContract.RequireUniqueMembers(value, CatalogueFieldSourceCodes.PayloadBindingMismatch);
                if (value.EnumerateObject().Count() != 2 || !value.TryGetProperty("defaultLocale", out var locale)
                    || locale.ValueKind != JsonValueKind.String || !Locale(locale.GetString()!)
                    || !value.TryGetProperty("values", out var translations) || translations.ValueKind != JsonValueKind.Object
                    || !translations.TryGetProperty(locale.GetString()!, out _)) return false;
                return translations.EnumerateObject().All(pair => Locale(pair.Name) && pair.Value.ValueKind == JsonValueKind.String);
            }
            catch (CatalogueFieldSourceException) { return false; }
        }
        if (value.ValueKind != JsonValueKind.String) return false;
        return coordinate.Field switch
        {
            "formId" => value.GetString() == coordinate.Id,
            "version" => value.GetString() == coordinate.Version,
            "cascadeLayer" => value.GetString() is "Base" or "Pack" or "Tenant" or "Instance",
            _ => false,
        };
    }

    private static bool Locale(string value) => value.Length > 0 && value.Split('-').All(part => part.Length is >= 1 and <= 8
        && part.All(char.IsAsciiLetterOrDigit)) && char.IsAsciiLetter(value[0]);
}
