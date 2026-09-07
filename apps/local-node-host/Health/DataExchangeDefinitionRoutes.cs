using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Foundation.DataExchangeDefinitions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Node-local READ surface over the pack-projected data exchange definition registry (ticket 087, a
/// literal copy of the ticket-079 admin pattern's api half).
/// </summary>
/// <remarks>
/// <para>Routes under <see cref="RouteBase"/>:</para>
/// <list type="bullet">
/// <item><description><c>GET {RouteBase}</c> — one head revision per key for the active tenant.</description></item>
/// <item><description><c>GET {RouteBase}/{key}</c> — the head revision's detail.</description></item>
/// <item><description><c>GET {RouteBase}/{key}/versions</c> — every admitted revision, newest first, with the ordering rule applied.</description></item>
/// <item><description><c>GET {RouteBase}/{key}/versions/{version}</c> — one exact pinned revision.</description></item>
/// </list>
/// <para>
/// Read-only ON PURPOSE: the registry has no status, no draft concept, and no version-minting
/// authority — definitions arrive exclusively by pack projection. A restore/write surface here
/// would mint caller-invented versions outside pack governance (ticket 085's recorded no-restore
/// ruling). Tenant scoping follows INV-S1: not-found and cross-tenant are indistinguishable.
/// The envelope's <c>cascadeLayer</c> and <c>provenance</c> ARE emitted — they carry
/// pack-authority meaning for projected definitions. (The Forms wire emits <c>cascadeLayer</c>
/// too since ticket 153, closing the gap that forced ticket 079's client-side derivation.)
/// </para>
/// <para>
/// Dependencies are closed-over parameters, never resolved from the request services: the routes
/// are mapped onto <see cref="SharedHostedWebApp"/>'s inner application, whose provider is a
/// different container from the outer host (bug 2849).
/// </para>
/// </remarks>
public static class DataExchangeDefinitionRoutes
{
    /// <summary>The route base every endpoint in this family lives under.</summary>
    public const string RouteBase = "/api/local-node/data-exchange/definitions";

    /// <summary>Maps the read-only data exchange definition routes onto <paramref name="app"/>.</summary>
    /// <param name="app">The route builder — hand this the desktop-plane-only group, never the bare application.</param>
    /// <param name="registry">The pack-projected data exchange definition registry.</param>
    /// <param name="activeTeam">The active-team accessor the tenant is resolved from per request.</param>
    public static void Map(
        IEndpointRouteBuilder app,
        IDataExchangeDefinitionRegistry registry,
        IActiveTeamAccessor activeTeam)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(activeTeam);

        // GET /api/local-node/data-exchange/definitions — one head revision per key.
        app.MapGet(RouteBase, async (CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam).Value;
            var heads = await registry.ListDefinitionsAsync(tenant, ct).ConfigureAwait(false);
            return Results.Ok(heads.Select(DataExchangeDefinitionSummaryDto.From).ToList());
        });

        // GET /api/local-node/data-exchange/definitions/{key} — the head revision's detail.
        app.MapGet($"{RouteBase}/{{key}}", async (string key, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam).Value;
            var history = await registry.ListVersionsAsync(tenant, key, ct).ConfigureAwait(false);
            if (history is null)
            {
                // INV-S1: not-found / cross-tenant are indistinguishable.
                return Results.NotFound(new { code = "data_exchange_definition.not_found" });
            }

            return Results.Ok(DataExchangeDefinitionDto.From(history.Versions[0]));
        });

        // GET /api/local-node/data-exchange/definitions/{key}/versions — full history, newest first.
        app.MapGet($"{RouteBase}/{{key}}/versions", async (string key, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam).Value;
            var history = await registry.ListVersionsAsync(tenant, key, ct).ConfigureAwait(false);
            if (history is null)
            {
                return Results.NotFound(new { code = "data_exchange_definition.not_found" });
            }

            return Results.Ok(DataExchangeDefinitionVersionListDto.From(history));
        });

        // GET /api/local-node/data-exchange/definitions/{key}/versions/{version} — one pinned revision.
        app.MapGet($"{RouteBase}/{{key}}/versions/{{version}}", async (string key, string version, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam).Value;
            var definition = await registry.GetDefinitionAsync(tenant, key, version, ct).ConfigureAwait(false);
            if (definition is null)
            {
                return Results.NotFound(new { code = "data_exchange_definition.version_not_found" });
            }

            return Results.Ok(DataExchangeDefinitionDto.From(definition));
        });
    }
}

// ── Wire shapes (the ticket-085 pattern wire; 086/087 copy these with pillar renames) ──

/// <summary>One list row: a key's head revision.</summary>
public sealed record DataExchangeDefinitionSummaryDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("exchangeKind")] string ExchangeKind,
    [property: JsonPropertyName("cascadeLayer")] string CascadeLayer)
{
    /// <summary>Projects the summary wire shape from the stored definition.</summary>
    public static DataExchangeDefinitionSummaryDto From(DataExchangeDefinition definition) => new(
        definition.Key,
        definition.Version,
        definition.Title,
        definition.ExchangeKind,
        definition.CascadeLayer.ToString());
}

/// <summary>One revision's full detail, envelope metadata included.</summary>
public sealed record DataExchangeDefinitionDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("exchangeKind")] string ExchangeKind,
    [property: JsonPropertyName("cascadeLayer")] string CascadeLayer,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("settings")] JsonElement Settings,
    [property: JsonPropertyName("provenance")] JsonElement Provenance)
{
    /// <summary>Projects the detail wire shape from the stored definition.</summary>
    public static DataExchangeDefinitionDto From(DataExchangeDefinition definition) => new(
        definition.Key,
        definition.Version,
        definition.Title,
        definition.ExchangeKind,
        definition.CascadeLayer.ToString(),
        definition.SchemaVersion,
        definition.Settings,
        definition.Provenance);
}

/// <summary>One key's history plus the ordering rule that produced it (<c>semver</c> or <c>ordinal</c>).</summary>
public sealed record DataExchangeDefinitionVersionListDto(
    [property: JsonPropertyName("ordering")] string Ordering,
    [property: JsonPropertyName("versions")] IReadOnlyList<DataExchangeDefinitionSummaryDto> Versions)
{
    /// <summary>Projects the history wire shape from the registry's ordered result.</summary>
    public static DataExchangeDefinitionVersionListDto From(DataExchangeDefinitionVersionList history) => new(
        history.Ordering,
        history.Versions.Select(DataExchangeDefinitionSummaryDto.From).ToList());
}
