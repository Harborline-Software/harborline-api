using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

using Harborline.Api.LocalNodeHost.Data.Leases;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// ADR 0115 D8 Stage 2 Cohort C — single source of truth for the node-local
/// <c>leases</c> route mapping. Both <see cref="HostedLeaseApiEndpoint"/>
/// (production) and the route tests call <see cref="Map"/> so the wire contract
/// has no test/prod drift.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire contract.</b> Serves the FLAT <c>Lease</c> shape the Harborline frontend
/// consumes today (<c>name, tenant, property, unit, start_date, end_date,
/// monthly_rent, status, company, termCadence, autoRenew</c> —
/// the earlier desktop app's <c>src/api/erpnext.ts</c>), previously served by the
/// Bridge <c>/api/v1/erpnext/leases</c> proxy and the retired Rust SQLite cache.
/// Mirrors <see cref="MaintenanceRoutes"/>.
/// </para>
/// <para>
/// <b>Routes:</b>
/// <list type="bullet">
///   <item><c>GET  /api/local-node/leases</c> — list (newest first).</item>
///   <item><c>GET  /api/local-node/leases/{name}</c> — fetch one; 404 if absent.</item>
///   <item><c>POST /api/local-node/leases</c> — create offline; server-assigns
///     <c>name</c> + timestamps; 400 on invalid status / termCadence.</item>
/// </list>
/// The POST exists so the node-local store can be populated fully offline.
/// </para>
/// </remarks>
public static class LeaseRoutes
{
    /// <summary>Canonical route base for the node-local lease surface.</summary>
    public const string RouteBase = "/api/local-node/leases";

    private static readonly HashSet<string> ValidStatuses =
        new(StringComparer.Ordinal) { "Active", "Expired", "Terminated" };

    private static readonly HashSet<string> ValidCadences =
        new(StringComparer.Ordinal) { "daily", "weekly", "monthly", "multi-month", "yearly", "fixed" };

    /// <summary>
    /// Maps the lease routes onto <paramref name="app"/>, closing over the
    /// supplied keyed <see cref="NodeLocalLeaseDbContext"/>
    /// <paramref name="factory"/>.
    /// </summary>
    internal static void Map(
        IEndpointRouteBuilder app,
        IDbContextFactory<NodeLocalLeaseDbContext> factory,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(factory);

        // GET /api/local-node/leases — list.
        app.MapGet(RouteBase, async (CancellationToken ct) =>
        {
            await using var ctx = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var rows = await ctx.Leases
                .AsNoTracking()
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return Results.Ok(new LeaseListResponse(
                rows.OrderByDescending(l => l.CreatedAt)
                    .Select(LeaseDto.From)
                    .ToList()));
        });

        // GET /api/local-node/leases/{name} — fetch one.
        app.MapGet($"{RouteBase}/{{name}}", async (
            string name,
            CancellationToken ct) =>
        {
            await using var ctx = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await ctx.Leases
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.Name == name, ct)
                .ConfigureAwait(false);

            return row is null
                ? Results.NotFound()
                : Results.Ok(new LeaseItemResponse(LeaseDto.From(row)));
        });

        // POST /api/local-node/leases — create offline.
        app.MapPost(RouteBase, async (
            CreateLeaseRequest body,
            CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Tenant))
            {
                return Results.BadRequest(new { error = "tenant is required." });
            }

            var status = string.IsNullOrWhiteSpace(body.Status) ? "Active" : body.Status;
            if (!ValidStatuses.Contains(status))
            {
                return Results.BadRequest(new
                {
                    error = $"status must be one of {string.Join(", ", ValidStatuses)}.",
                });
            }

            var cadence = string.IsNullOrWhiteSpace(body.TermCadence) ? "fixed" : body.TermCadence;
            if (!ValidCadences.Contains(cadence))
            {
                return Results.BadRequest(new
                {
                    error = $"termCadence must be one of {string.Join(", ", ValidCadences)}.",
                });
            }

            await using var ctx = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);

            var now = timeProvider.GetUtcNow();
            var record = new LeaseRecord
            {
                Name = await NextLeaseNameAsync(ctx, ct).ConfigureAwait(false),
                Tenant = body.Tenant,
                Property = body.Property ?? string.Empty,
                Unit = body.Unit ?? string.Empty,
                StartDate = body.StartDate ?? string.Empty,
                EndDate = body.EndDate ?? string.Empty,
                MonthlyRent = body.MonthlyRent ?? 0m,
                Status = status,
                Company = body.Company ?? string.Empty,
                TermCadence = cadence,
                AutoRenew = body.AutoRenew ?? false,
                CreatedAt = now,
                ModifiedAt = now,
            };

            ctx.Leases.Add(record);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

            return Results.Created(
                $"{RouteBase}/{Uri.EscapeDataString(record.Name)}",
                new LeaseItemResponse(LeaseDto.From(record)));
        });
    }

    /// <summary>
    /// Generates the next ERPNext-style lease name (<c>LEASE-{n}</c>) by counting
    /// existing rows. Single-writer node, so a count-based monotonic id is safe.
    /// </summary>
    private static async Task<string> NextLeaseNameAsync(
        NodeLocalLeaseDbContext ctx, CancellationToken ct)
    {
        var count = await ctx.Leases.CountAsync(ct).ConfigureAwait(false);
        return $"LEASE-{count + 1:D4}";
    }
}

/// <summary>Flat wire DTO matching the frontend <c>Lease</c> shape.</summary>
public sealed record LeaseDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("tenant")] string Tenant,
    [property: JsonPropertyName("property")] string Property,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("start_date")] string StartDate,
    [property: JsonPropertyName("end_date")] string EndDate,
    [property: JsonPropertyName("monthly_rent")] decimal MonthlyRent,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("company")] string Company,
    [property: JsonPropertyName("termCadence")] string TermCadence,
    [property: JsonPropertyName("autoRenew")] bool AutoRenew)
{
    /// <summary>Projects a stored record onto the flat wire DTO.</summary>
    public static LeaseDto From(LeaseRecord r) => new(
        r.Name, r.Tenant, r.Property, r.Unit, r.StartDate, r.EndDate,
        r.MonthlyRent, r.Status, r.Company, r.TermCadence, r.AutoRenew);
}

/// <summary>List response envelope: <c>{ "data": [...] }</c>.</summary>
public sealed record LeaseListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<LeaseDto> Data);

/// <summary>Single-item response envelope: <c>{ "data": {...} }</c>.</summary>
public sealed record LeaseItemResponse(
    [property: JsonPropertyName("data")] LeaseDto Data);

/// <summary>Create request — flat lease shape (server assigns name + timestamps).</summary>
public sealed record CreateLeaseRequest(
    [property: JsonPropertyName("tenant")] string Tenant,
    [property: JsonPropertyName("property")] string? Property,
    [property: JsonPropertyName("unit")] string? Unit,
    [property: JsonPropertyName("start_date")] string? StartDate,
    [property: JsonPropertyName("end_date")] string? EndDate,
    [property: JsonPropertyName("monthly_rent")] decimal? MonthlyRent,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("company")] string? Company,
    [property: JsonPropertyName("termCadence")] string? TermCadence,
    [property: JsonPropertyName("autoRenew")] bool? AutoRenew);
