using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.ViewDefinitions;
using Harborline.Api.LocalNodeHost.Data.Identity;

using AmbientTenantContext = Harborline.Api.Foundation.MultiTenancy.ITenantContext;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>The common read envelope for a projected definition.</summary>
public sealed record CatalogueEntry(
    string Id,
    string Version,
    PackContentKind Kind,
    InternationalizedTextDto? Title,
    CatalogueProvenance Provenance,
    bool Sealed,
    string Status,
    DateTimeOffset UpdatedAt,
    JsonElement Body,
    string? DefinitionHash = null,
    RenderPlan? RenderPlan = null);

/// <summary>The pack authority that supplied a catalogue entry.</summary>
public sealed record CatalogueProvenance(
    [property: JsonPropertyName("packKey"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PackKey,
    [property: JsonPropertyName("packVersion"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PackVersion,
    [property: JsonPropertyName("kind")] string Kind);

/// <summary>One compiled, sealed system type represented by the catalogue.</summary>
public sealed record SystemRecordType(
    PackContentKind Kind,
    string Name,
    bool Sealed,
    CatalogueProvenance Provenance)
{
    /// <summary>Every pack content kind is a sealed platform record type.</summary>
    public static IReadOnlyList<SystemRecordType> All { get; } = Enum.GetValues<PackContentKind>()
        .Where(kind => kind != PackContentKind.RecordType)
        .Select(kind => new SystemRecordType(
            kind,
            kind.ToString(),
            Sealed: true,
            new CatalogueProvenance("harborline.platform", null, "platform")))
        .ToArray();

    /// <summary>
    /// Resolves the one compiled descriptor set through the active platform pack's carried catalogue.
    /// The seed supplies lifecycle and provenance; it never supplies a second schema.
    /// </summary>
    public static IReadOnlyList<SystemRecordType> FromActivePlatformPack(InstalledPack? platform)
    {
        if (platform is null
            || platform.Lifecycle != PackLifecycleState.Active
            || !string.Equals(platform.PackKey, "harborline.platform", StringComparison.Ordinal))
        {
            return Array.Empty<SystemRecordType>();
        }

        var carried = new Dictionary<string, PackSeedItem>(StringComparer.Ordinal);
        foreach (var item in platform.SeedItems.Where(item => item.Kind == PackContentKind.RecordType))
        {
            if (!carried.TryAdd(item.Key, item))
                return Array.Empty<SystemRecordType>();
        }

        if (carried.Count != All.Count) return Array.Empty<SystemRecordType>();

        foreach (var descriptor in All)
        {
            if (!carried.TryGetValue(descriptor.Name, out var item) || !IsValidPlatformDeclaration(item))
                return Array.Empty<SystemRecordType>();
        }

        var provenance = new CatalogueProvenance(platform.PackKey, platform.Version, "platform");
        return All.Select(descriptor => descriptor with { Provenance = provenance }).ToArray();
    }

    private static bool IsValidPlatformDeclaration(PackSeedItem item)
    {
        try
        {
            using var document = JsonDocument.Parse(item.CanonicalJson);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("sealed", out var sealedProperty)
                   && sealedProperty.ValueKind is JsonValueKind.True
                   && root.TryGetProperty("provenance", out var provenance)
                   && provenance.ValueKind == JsonValueKind.Object
                   && provenance.TryGetProperty("kind", out var kind)
                   && kind.ValueKind == JsonValueKind.String
                   && string.Equals(kind.GetString(), "platform", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>A catalogue response including kinds whose projector is not composed in this host.</summary>
public sealed record CatalogueList(
    IReadOnlyList<CatalogueEntry> Entries,
    IReadOnlyList<PackContentKind> KindsUnavailable);

/// <summary>The single tenant-scoped read seam over projected definition stores.</summary>
public interface ICatalogue
{
    ValueTask<CatalogueList> ListAsync(
        TenantId tenant,
        PackContentKind? kind = null,
        CancellationToken cancellationToken = default);

    ValueTask<CatalogueEntry?> GetAsync(
        TenantId tenant,
        PackContentKind kind,
        string id,
        string? version = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Adapter over the already-composed form definition store. Kinds without a composed adapter are
/// explicit empty/unavailable results: a catalogue read never invents persistence or throws.
/// </summary>
public sealed class ProjectedCatalogue : ICatalogue
{
    private readonly AuthorizedFormDefinitionLifecycle authorizedForms;
    private readonly IViewDefinitionRegistry? viewDefinitions;
    private readonly InMemoryRenderPlanCatalogue? renderPlans;

    /// <summary>
    /// Reads forms through the same authorized lifecycle that owns the Form definition route family.
    /// Raw store access remains private to that lifecycle, so composition has one unambiguous catalogue
    /// constructor and no route can bypass the read authority seam.
    /// </summary>
    public ProjectedCatalogue(
        AuthorizedFormDefinitionLifecycle forms,
        IViewDefinitionRegistry? viewDefinitions = null,
        InMemoryRenderPlanCatalogue? renderPlans = null)
    {
        authorizedForms = forms ?? throw new ArgumentNullException(nameof(forms));
        this.viewDefinitions = viewDefinitions;
        this.renderPlans = renderPlans;
    }
    private static readonly PackContentKind[] UnavailableKinds = Enum.GetValues<PackContentKind>()
        .Where(kind => kind is not (PackContentKind.FormDefinition or PackContentKind.ViewDefinition))
        .ToArray();

    public async ValueTask<CatalogueList> ListAsync(
        TenantId tenant,
        PackContentKind? kind = null,
        CancellationToken cancellationToken = default)
    {
        if (kind is { } requested && requested != PackContentKind.FormDefinition
            && (requested != PackContentKind.ViewDefinition || viewDefinitions is null))
        {
            return new CatalogueList(Array.Empty<CatalogueEntry>(), [requested]);
        }

        var entries = new List<CatalogueEntry>();
        await foreach (var definition in ListFormsAsync(tenant, cancellationToken).ConfigureAwait(false))
        {
            entries.Add(From(tenant, definition));
        }

        if ((kind is null || kind == PackContentKind.ViewDefinition) && viewDefinitions is not null)
        {
            var views = await viewDefinitions.ListDefinitionsAsync(tenant.Value, cancellationToken).ConfigureAwait(false);
            entries.AddRange(views.Select(definition => From(tenant, definition)));
        }

        return new CatalogueList(entries, kind is null ? UnavailableKinds : Array.Empty<PackContentKind>());
    }

    public async ValueTask<CatalogueEntry?> GetAsync(
        TenantId tenant,
        PackContentKind kind,
        string id,
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        if (kind == PackContentKind.ViewDefinition && viewDefinitions is not null)
        {
            if (version is null)
            {
                var history = await viewDefinitions.ListVersionsAsync(tenant.Value, id, cancellationToken).ConfigureAwait(false);
                return history is null ? null : From(tenant, history.Versions[0]);
            }

            var view = await viewDefinitions.GetDefinitionAsync(tenant.Value, id, version, cancellationToken).ConfigureAwait(false);
            return view is null ? null : From(tenant, view);
        }

        if (kind != PackContentKind.FormDefinition)
        {
            return null;
        }

        try
        {
            var definition = version is null
                ? await GetCurrentPublishedAsync(new DefinitionAddress(tenant, id), cancellationToken).ConfigureAwait(false)
                : await GetAsync(new DefinitionCoordinates(tenant, id, version), cancellationToken).ConfigureAwait(false);
            return definition is null || definition.Status != FormDefinitionStatus.Published ? null : From(tenant, definition);
        }
        catch (FormDefinitionNotFoundException)
        {
            return null;
        }
    }

    private CatalogueEntry From(TenantId tenant, FormDefinition definition)
    {
        var source = definition.PackSource;
        var plan = renderPlans?.Get(tenant, PackContentKind.FormDefinition, definition.Id.Value, definition.Version.ToString());
        return new CatalogueEntry(
            definition.Id.Value,
            definition.Version.ToString(),
            PackContentKind.FormDefinition,
            InternationalizedTextDto.From(definition.Overlay.Title),
            source is null
                ? new CatalogueProvenance(null, null, "tenant")
                : new CatalogueProvenance(source.PackId, source.PackVersion, "pack"),
            Sealed: false,
            definition.Status.ToString(),
            definition.UpdatedAt,
            JsonSerializer.SerializeToElement(FormDefinitionDto.From(definition)),
            plan?.DefinitionHash,
            plan);
    }

    private CatalogueEntry From(TenantId tenant, ViewDefinition definition)
    {
        var plan = renderPlans?.Get(tenant, PackContentKind.ViewDefinition, definition.Key, definition.Version);
        return new CatalogueEntry(
            definition.Key,
            definition.Version,
            PackContentKind.ViewDefinition,
            new InternationalizedTextDto("en", new Dictionary<string, string> { ["en"] = definition.Title }),
            new CatalogueProvenance(plan?.PackKey, plan?.PackVersion, plan is null ? "tenant" : "pack"),
            Sealed: false,
            "Published",
            DateTimeOffset.MinValue,
            JsonSerializer.SerializeToElement(ViewDefinitionDto.From(definition)),
            plan?.DefinitionHash,
            plan);
    }

    private IAsyncEnumerable<FormDefinition> ListFormsAsync(TenantId tenant, CancellationToken cancellationToken)
        => authorizedForms.ListByTenantAsync(tenant, cancellationToken);

    private ValueTask<FormDefinition?> GetCurrentPublishedAsync(DefinitionAddress address, CancellationToken cancellationToken)
        => authorizedForms.GetCurrentPublishedAsync(address, cancellationToken);

    private ValueTask<FormDefinition> GetAsync(DefinitionCoordinates coordinates, CancellationToken cancellationToken)
        => authorizedForms.GetAsync(coordinates, cancellationToken);
}

/// <summary>The gate-authorized tenant-scoped catalogue read family.</summary>
public static class CatalogueRoutes
{
    public const string RouteBase = "/api/local-node/catalogue/definitions";
    public const string TypesRoute = "/api/local-node/catalogue/types";

    public static void Map(
        IEndpointRouteBuilder app,
        ICatalogue catalogue,
        IPackInstallStore packStore,
        AmbientTenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(packStore);
        ArgumentNullException.ThrowIfNull(tenantContext);

        app.MapGet(RouteBase, async Task<IResult> (HttpContext http, CancellationToken ct) =>
        {
            var tenant = RequestTenant(http, tenantContext);
            if (await RequestAuthorization.RefusalAsync(
                    http, tenant, Permission.CatalogueRead, RouteRecord.Of("catalogue-definitions"), ct).ConfigureAwait(false) is { } denied)
                return denied;

            if (!TryKind(http.Request.Query["kind"], out var kind))
                return Results.BadRequest(new { code = "catalogue.kind_unknown" });

            var result = await catalogue.ListAsync(tenant, kind, ct).ConfigureAwait(false);
            return Results.Ok(new { entries = result.Entries, kindsUnavailable = result.KindsUnavailable });
        });

        app.MapGet($"{RouteBase}/{{kind}}/{{id}}", async Task<IResult> (
            string kind, string id, HttpContext http, CancellationToken ct) =>
        {
            var tenant = RequestTenant(http, tenantContext);
            if (await RequestAuthorization.RefusalAsync(
                    http, tenant, Permission.CatalogueRead, RouteRecord.Of(id), ct).ConfigureAwait(false) is { } denied)
                return denied;

            if (!Enum.TryParse<PackContentKind>(kind, ignoreCase: true, out var parsed))
                return Results.BadRequest(new { code = "catalogue.kind_unknown" });

            var result = await catalogue.GetAsync(tenant, parsed, id, http.Request.Query["version"], ct).ConfigureAwait(false);
            return result is null ? Results.NotFound(new { code = "catalogue.definition_not_found" }) : Results.Ok(result);
        });

        app.MapGet(TypesRoute, async Task<IResult> (HttpContext http, CancellationToken ct) =>
        {
            var tenant = RequestTenant(http, tenantContext);
            if (await RequestAuthorization.RefusalAsync(
                    http, tenant, Permission.CatalogueRead, RouteRecord.Of("catalogue-types"), ct).ConfigureAwait(false) is { } denied)
                return denied;
            var platform = packStore.GetActive(tenant, "harborline.platform");
            return Results.Ok(SystemRecordType.FromActivePlatformPack(platform));
        });
    }

    private static TenantId RequestTenant(HttpContext http, AmbientTenantContext tenantContext) =>
        http.Features.Get<SelectedSessionRequestPrincipal>()?.TenantId
        ?? tenantContext.Tenant?.Id
        ?? throw new InvalidOperationException("No tenant is resolved for the catalogue request.");

    private static bool TryKind(string? raw, out PackContentKind? kind)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            kind = null;
            return true;
        }

        if (Enum.TryParse<PackContentKind>(raw, ignoreCase: true, out var parsed))
        {
            kind = parsed;
            return true;
        }

        kind = null;
        return false;
    }
}
