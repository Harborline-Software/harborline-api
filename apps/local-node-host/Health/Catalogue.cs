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
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.LocalNodeHost.Data.Identity;

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
    JsonElement Body);

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
        .Select(kind => new SystemRecordType(
            kind,
            kind.ToString(),
            Sealed: true,
            new CatalogueProvenance("harborline.platform", "compiled", "platform")))
        .ToArray();
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

    /// <summary>
    /// Reads forms through the same authorized lifecycle that owns the Form definition route family.
    /// Raw store access remains private to that lifecycle, so composition has one unambiguous catalogue
    /// constructor and no route can bypass the read authority seam.
    /// </summary>
    public ProjectedCatalogue(AuthorizedFormDefinitionLifecycle forms) =>
        authorizedForms = forms ?? throw new ArgumentNullException(nameof(forms));
    private static readonly PackContentKind[] UnavailableKinds = Enum.GetValues<PackContentKind>()
        .Where(kind => kind != PackContentKind.FormDefinition)
        .ToArray();

    public async ValueTask<CatalogueList> ListAsync(
        TenantId tenant,
        PackContentKind? kind = null,
        CancellationToken cancellationToken = default)
    {
        if (kind is { } requested && requested != PackContentKind.FormDefinition)
        {
            return new CatalogueList(Array.Empty<CatalogueEntry>(), [requested]);
        }

        var entries = new List<CatalogueEntry>();
        await foreach (var definition in ListFormsAsync(tenant, cancellationToken).ConfigureAwait(false))
        {
            entries.Add(From(definition));
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
        if (kind != PackContentKind.FormDefinition)
        {
            return null;
        }

        try
        {
            var definition = version is null
                ? await GetCurrentPublishedAsync(new DefinitionAddress(tenant, id), cancellationToken).ConfigureAwait(false)
                : await GetAsync(new DefinitionCoordinates(tenant, id, version), cancellationToken).ConfigureAwait(false);
            return definition is null || definition.Status != FormDefinitionStatus.Published ? null : From(definition);
        }
        catch (FormDefinitionNotFoundException)
        {
            return null;
        }
    }

    private static CatalogueEntry From(FormDefinition definition)
    {
        var source = definition.PackSource;
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
            JsonSerializer.SerializeToElement(FormDefinitionDto.From(definition)));
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

    public static void Map(IEndpointRouteBuilder app, ICatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(catalogue);

        app.MapGet(RouteBase, async Task<IResult> (HttpContext http, CancellationToken ct) =>
        {
            var principal = http.Features.Get<SelectedSessionRequestPrincipal>();
            if (principal is null) return Results.Unauthorized();
            var tenant = principal.TenantId;
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
            var principal = http.Features.Get<SelectedSessionRequestPrincipal>();
            if (principal is null) return Results.Unauthorized();
            var tenant = principal.TenantId;
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
            var principal = http.Features.Get<SelectedSessionRequestPrincipal>();
            if (principal is null) return Results.Unauthorized();
            var tenant = principal.TenantId;
            if (await RequestAuthorization.RefusalAsync(
                    http, tenant, Permission.CatalogueRead, RouteRecord.Of("catalogue-types"), ct).ConfigureAwait(false) is { } denied)
                return denied;
            return Results.Ok(SystemRecordType.All);
        });
    }

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
