using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.LocalNodeHost.Data.Maintenance;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// ADR 0115 D8 Stage 2 — single source of truth for the node-local
/// <c>maintenance_tickets</c> route mapping. Both
/// <see cref="HostedMaintenanceApiEndpoint"/> (production, on the shared Kestrel
/// listener) and the route tests call <see cref="Map"/> so the wire contract has
/// no test/prod drift.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire contract.</b> Serves the FLAT <c>MaintenanceTicket</c> shape the
/// Harborline frontend consumes today (<c>name, subject, property, status,
/// priority, assigned_to, cost</c> — the earlier desktop app's <c>src/api/erpnext.ts</c>),
/// previously served by the Bridge <c>/api/v1/erpnext/maintenance</c> proxy. The
/// rich <c>WorkOrder</c> aggregate is intentionally NOT used — this is the
/// node-local doctype the Stage-2 PM pilot flips (Admiral ruling 2026-06-13
/// Option B).
/// </para>
/// <para>
/// <b>Routes:</b>
/// <list type="bullet">
///   <item><c>GET  /api/local-node/maintenance</c> — list (newest first).</item>
///   <item><c>GET  /api/local-node/maintenance/{name}</c> — fetch one; 404 if absent.</item>
///   <item><c>POST /api/local-node/maintenance</c> — create; server-assigns
///     <c>name</c> + timestamps; 400 on missing subject / invalid priority.</item>
///   <item><c>PATCH /api/local-node/maintenance/{name}</c> — partial update
///     (status, assigned_to, cost, resolution); 404 if absent; 400 on invalid status.</item>
/// </list>
/// </para>
/// </remarks>
public static class MaintenanceRoutes
{
    /// <summary>Canonical route base for the node-local maintenance surface.</summary>
    public const string RouteBase = "/api/local-node/maintenance";

    private static readonly HashSet<string> ValidStatuses =
        new(StringComparer.Ordinal) { "Open", "In Progress", "Resolved", "Closed" };

    private static readonly HashSet<string> ValidPriorities =
        new(StringComparer.Ordinal) { "Low", "Medium", "High", "Critical" };

    /// <summary>
    /// Maps the maintenance routes onto <paramref name="app"/>, closing over the
    /// supplied keyed <see cref="NodeLocalMaintenanceDbContext"/>
    /// <paramref name="factory"/>.
    /// </summary>
    /// <remarks>
    /// The factory is passed explicitly (captured from the OUTER host container
    /// by the caller) rather than resolved per-request via <c>[FromServices]</c>.
    /// On the production path the routes are mapped onto
    /// <see cref="SharedHostedWebApp"/>'s inner <c>WebApplication</c>, whose
    /// service provider is a SEPARATE container that does NOT have the SC-1
    /// maintenance factory registered — so a per-request <c>[FromServices]</c>
    /// resolution there throws "No service for type". Closing over the
    /// outer-container factory (the same pattern <see cref="HostedLocalNodeApiEndpoint"/>
    /// uses for the status endpoint) is the correct, container-agnostic wiring.
    /// </remarks>
    internal static void Map(
        IEndpointRouteBuilder app,
        IDbContextFactory<NodeLocalMaintenanceDbContext> factory,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(factory);

        // GET /api/local-node/maintenance — list.
        app.MapGet(RouteBase, async (CancellationToken ct) =>
        {
            await using var ctx = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            // Materialize then sort in memory: SQLite has no native DateTimeOffset
            // ordering, and the node-local table is small (single-tenant), so the
            // client-side sort is cheap and avoids the provider translation gap.
            var rows = await ctx.MaintenanceTickets
                .AsNoTracking()
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return Results.Ok(new MaintenanceListResponse(
                rows.OrderByDescending(t => t.CreatedAt)
                    .Select(MaintenanceTicketDto.From)
                    .ToList()));
        });

        // GET /api/local-node/maintenance/{name} — fetch one.
        app.MapGet($"{RouteBase}/{{name}}", async (
            string name,
            CancellationToken ct) =>
        {
            await using var ctx = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await ctx.MaintenanceTickets
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Name == name, ct)
                .ConfigureAwait(false);

            return row is null
                ? Results.NotFound()
                : Results.Ok(MaintenanceTicketDto.From(row));
        });

        // POST /api/local-node/maintenance — create.
        app.MapPost(RouteBase, async (
            CreateMaintenanceRequest body,
            CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Subject))
            {
                return Results.BadRequest(new { error = "subject is required." });
            }

            var priority = string.IsNullOrWhiteSpace(body.Priority) ? "Medium" : body.Priority;
            if (!ValidPriorities.Contains(priority))
            {
                return Results.BadRequest(new
                {
                    error = $"priority must be one of {string.Join(", ", ValidPriorities)}.",
                });
            }

            await using var ctx = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);

            var now = timeProvider.GetUtcNow();
            var record = new MaintenanceTicketRecord
            {
                Name = await NextTicketNameAsync(ctx, ct).ConfigureAwait(false),
                Subject = body.Subject,
                Property = body.Property ?? string.Empty,
                Status = "Open",
                Priority = priority,
                AssignedTo = body.AssignedTo,
                Description = body.Description,
                CreatedAt = now,
                ModifiedAt = now,
            };

            ctx.MaintenanceTickets.Add(record);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

            return Results.Created(
                $"{RouteBase}/{Uri.EscapeDataString(record.Name)}",
                new MaintenanceItemResponse(MaintenanceTicketDto.From(record)));
        });

        // PATCH /api/local-node/maintenance/{name} — partial update.
        app.MapMethods($"{RouteBase}/{{name}}", ["PATCH"], async (
            string name,
            UpdateMaintenanceRequest body,
            CancellationToken ct) =>
        {
            if (body is null)
            {
                return Results.BadRequest(new { error = "request body is required." });
            }
            if (body.Status is not null && !ValidStatuses.Contains(body.Status))
            {
                return Results.BadRequest(new
                {
                    error = $"status must be one of {string.Join(", ", ValidStatuses)}.",
                });
            }

            await using var ctx = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var record = await ctx.MaintenanceTickets
                .FirstOrDefaultAsync(t => t.Name == name, ct)
                .ConfigureAwait(false);

            if (record is null)
            {
                return Results.NotFound();
            }

            if (body.Status is not null) record.Status = body.Status;
            if (body.AssignedTo is not null) record.AssignedTo = body.AssignedTo;
            if (body.Cost is not null) record.Cost = body.Cost;
            if (body.Resolution is not null) record.Resolution = body.Resolution;
            record.ModifiedAt = timeProvider.GetUtcNow();

            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

            return Results.Ok(new MaintenanceItemResponse(MaintenanceTicketDto.From(record)));
        });
    }

    /// <summary>
    /// Generates the next ERPNext-style ticket name (<c>TKT-{n}</c>) by counting
    /// existing rows. Single-writer node, so a count-based monotonic id is safe
    /// (no concurrent inserts across processes — the node owns this table).
    /// </summary>
    private static async Task<string> NextTicketNameAsync(
        NodeLocalMaintenanceDbContext ctx, CancellationToken ct)
    {
        var count = await ctx.MaintenanceTickets.CountAsync(ct).ConfigureAwait(false);
        return $"TKT-{count + 1:D5}";
    }
}

/// <summary>Flat wire DTO matching the frontend <c>MaintenanceTicket</c> shape.</summary>
public sealed record MaintenanceTicketDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("property")] string Property,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("priority")] string Priority,
    [property: JsonPropertyName("assigned_to")] string? AssignedTo,
    [property: JsonPropertyName("cost")] decimal? Cost)
{
    /// <summary>Projects a stored record onto the flat wire DTO.</summary>
    public static MaintenanceTicketDto From(MaintenanceTicketRecord r) => new(
        r.Name, r.Subject, r.Property, r.Status, r.Priority, r.AssignedTo, r.Cost);
}

/// <summary>List response envelope: <c>{ "data": [...] }</c> (matches the proxy shape).</summary>
public sealed record MaintenanceListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<MaintenanceTicketDto> Data);

/// <summary>Single-item response envelope: <c>{ "data": {...} }</c>.</summary>
public sealed record MaintenanceItemResponse(
    [property: JsonPropertyName("data")] MaintenanceTicketDto Data);

/// <summary>Create request — mirrors the frontend <c>CreateMaintenanceInput</c>.</summary>
public sealed record CreateMaintenanceRequest(
    [property: JsonPropertyName("Subject")] string Subject,
    [property: JsonPropertyName("Property")] string? Property,
    [property: JsonPropertyName("Priority")] string? Priority,
    [property: JsonPropertyName("AssignedTo")] string? AssignedTo,
    [property: JsonPropertyName("Description")] string? Description);

/// <summary>Update request — mirrors the frontend <c>UpdateMaintenanceInput</c>.</summary>
public sealed record UpdateMaintenanceRequest(
    [property: JsonPropertyName("Status")] string? Status,
    [property: JsonPropertyName("AssignedTo")] string? AssignedTo,
    [property: JsonPropertyName("Cost")] decimal? Cost,
    [property: JsonPropertyName("Resolution")] string? Resolution);
