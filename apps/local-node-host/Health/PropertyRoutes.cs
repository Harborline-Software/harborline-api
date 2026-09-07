using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

using Harborline.Api.LocalNodeHost.Data.Properties;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// ADR 0115 D8 Stage 2 Cohort C — single source of truth for the node-local
/// <c>properties</c> route mapping. Both
/// <see cref="HostedPropertyApiEndpoint"/> (production, on the shared Kestrel
/// listener) and the route tests call <see cref="Map"/> so the wire contract has
/// no test/prod drift.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire contract.</b> Serves the FLAT <c>Property</c> shape the Harborline
/// frontend consumes today (<c>name, property_name, address_line_1, city, state,
/// postal_code, units, status, company</c> —
/// the earlier desktop app's <c>src/api/erpnext.ts</c>), previously served by the
/// Bridge <c>/api/v1/erpnext/properties</c> proxy and the retired Rust SQLite
/// cache. Mirrors <see cref="MaintenanceRoutes"/>.
/// </para>
/// <para>
/// <b>Routes:</b>
/// <list type="bullet">
///   <item><c>GET  /api/local-node/properties</c> — list (newest first).</item>
///   <item><c>GET  /api/local-node/properties/{name}</c> — fetch one; 404 if absent.</item>
///   <item><c>POST /api/local-node/properties</c> — create offline; server-assigns
///     <c>name</c> + timestamps; 400 on missing property_name / invalid status.</item>
/// </list>
/// The POST exists so the node-local store can be populated fully offline (no
/// Bridge required) — the absolute-local-first posture. There is no PATCH this
/// cohort (the property UX is read-only today).
/// </para>
/// <para>
/// The factory is injected from the OUTER host container (bug-2849 trap: do NOT
/// use <c>[FromServices]</c> — the routes map onto <see cref="SharedHostedWebApp"/>'s
/// inner <c>WebApplication</c> whose service provider is a SEPARATE container).
/// </para>
/// </remarks>
public static class PropertyRoutes
{
    /// <summary>Canonical route base for the node-local property surface.</summary>
    public const string RouteBase = "/api/local-node/properties";

    private static readonly HashSet<string> ValidStatuses =
        new(StringComparer.Ordinal) { "Active", "Vacant", "Maintenance", "Sold" };

    /// <summary>
    /// Maps the property routes onto <paramref name="app"/>, closing over the
    /// supplied keyed <see cref="NodeLocalPropertyDbContext"/>
    /// <paramref name="factory"/>.
    /// </summary>
    internal static void Map(
        IEndpointRouteBuilder app,
        IDbContextFactory<NodeLocalPropertyDbContext> factory,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(factory);

        // GET /api/local-node/properties — list.
        app.MapGet(RouteBase, async (CancellationToken ct) =>
        {
            await using var ctx = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            // Materialize then sort in memory: SQLite has no native DateTimeOffset
            // ordering, and the node-local table is small (single-tenant), so the
            // client-side sort is cheap and avoids the provider translation gap.
            var rows = await ctx.Properties
                .AsNoTracking()
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return Results.Ok(new PropertyListResponse(
                rows.OrderByDescending(p => p.CreatedAt)
                    .Select(PropertyDto.From)
                    .ToList()));
        });

        // GET /api/local-node/properties/{name} — fetch one.
        app.MapGet($"{RouteBase}/{{name}}", async (
            string name,
            CancellationToken ct) =>
        {
            await using var ctx = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await ctx.Properties
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Name == name, ct)
                .ConfigureAwait(false);

            return row is null
                ? Results.NotFound()
                : Results.Ok(new PropertyItemResponse(PropertyDto.From(row)));
        });

        // POST /api/local-node/properties — create offline.
        app.MapPost(RouteBase, async (
            CreatePropertyRequest body,
            CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.PropertyName))
            {
                return Results.BadRequest(new { error = "property_name is required." });
            }

            var status = string.IsNullOrWhiteSpace(body.Status) ? "Active" : body.Status;
            if (!ValidStatuses.Contains(status))
            {
                return Results.BadRequest(new
                {
                    error = $"status must be one of {string.Join(", ", ValidStatuses)}.",
                });
            }

            await using var ctx = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);

            var now = timeProvider.GetUtcNow();
            var record = new PropertyRecord
            {
                Name = await NextPropertyNameAsync(ctx, ct).ConfigureAwait(false),
                PropertyName = body.PropertyName,
                AddressLine1 = body.AddressLine1 ?? string.Empty,
                City = body.City ?? string.Empty,
                State = body.State ?? string.Empty,
                PostalCode = body.PostalCode ?? string.Empty,
                Units = body.Units ?? 0,
                Status = status,
                Company = body.Company ?? string.Empty,
                CreatedAt = now,
                ModifiedAt = now,
            };

            ctx.Properties.Add(record);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

            return Results.Created(
                $"{RouteBase}/{Uri.EscapeDataString(record.Name)}",
                new PropertyItemResponse(PropertyDto.From(record)));
        });
    }

    /// <summary>
    /// Generates the next ERPNext-style property name (<c>PROP-{n}</c>) by
    /// counting existing rows. Single-writer node, so a count-based monotonic id
    /// is safe (the node owns this table).
    /// </summary>
    private static async Task<string> NextPropertyNameAsync(
        NodeLocalPropertyDbContext ctx, CancellationToken ct)
    {
        var count = await ctx.Properties.CountAsync(ct).ConfigureAwait(false);
        return $"PROP-{count + 1:D4}";
    }
}

/// <summary>Flat wire DTO matching the frontend <c>Property</c> shape.</summary>
public sealed record PropertyDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("property_name")] string PropertyName,
    [property: JsonPropertyName("address_line_1")] string AddressLine1,
    [property: JsonPropertyName("city")] string City,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("postal_code")] string PostalCode,
    [property: JsonPropertyName("units")] int Units,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("company")] string Company)
{
    /// <summary>Projects a stored record onto the flat wire DTO.</summary>
    public static PropertyDto From(PropertyRecord r) => new(
        r.Name, r.PropertyName, r.AddressLine1, r.City, r.State,
        r.PostalCode, r.Units, r.Status, r.Company);
}

/// <summary>List response envelope: <c>{ "data": [...] }</c> (matches the proxy shape).</summary>
public sealed record PropertyListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<PropertyDto> Data);

/// <summary>Single-item response envelope: <c>{ "data": {...} }</c>.</summary>
public sealed record PropertyItemResponse(
    [property: JsonPropertyName("data")] PropertyDto Data);

/// <summary>Create request — flat property shape (server assigns name + timestamps).</summary>
public sealed record CreatePropertyRequest(
    [property: JsonPropertyName("property_name")] string PropertyName,
    [property: JsonPropertyName("address_line_1")] string? AddressLine1,
    [property: JsonPropertyName("city")] string? City,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("postal_code")] string? PostalCode,
    [property: JsonPropertyName("units")] int? Units,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("company")] string? Company);
