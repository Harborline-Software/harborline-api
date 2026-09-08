using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Blocks.Calendar.Services;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.LocalNodeHost.Data.Calendar;

/// <summary>
/// The durable EF/SQLite <see cref="ICalendarEventStore"/> (ONR app-calendar survey 2026-06-24,
/// inc-0) — replaces the block's in-memory default in the node host. Persists each series master in
/// the SAME SQLCipher <c>local-node.db</c> file (via <see cref="NodeLocalCalendarDbContext"/>) as the
/// other node-local doctypes, tenant-scoped + cross-tenant isolated.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the <see cref="InMemoryCalendarEventStore"/> persistence discipline exactly: it serializes
/// the block's <see cref="CalendarEventSnapshot"/> with the SAME <see cref="JsonSerializerOptions"/>
/// (<see cref="JsonSerializerDefaults.General"/>), so the EXDATE set + RECURRENCE-ID overrides +
/// participations genuinely round-trip through (de)serialization — a saved series re-loads with its
/// occurrence-level edits intact. The only difference from the in-memory store is the durable sink (a
/// SQLite row vs a dictionary entry); the snapshot bytes are identical.
/// </para>
/// <para>
/// Mirrors the <c>NodeEfGrantStore : IGrantStore</c> precedent: a <c>NodeEf</c> store implementing a
/// domain-block interface and overriding the block's in-memory default, bound to the node context
/// factory.
/// </para>
/// </remarks>
public sealed class NodeEfCalendarEventStore : ICalendarEventStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    private readonly IDbContextFactory<NodeLocalCalendarDbContext> _contextFactory;

    /// <summary>Construct bound to the calendar context factory (the SQLCipher file the store lives in).</summary>
    public NodeEfCalendarEventStore(IDbContextFactory<NodeLocalCalendarDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    /// <inheritdoc />
    public async Task SaveAsync(CalendarEvent calendarEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        var snapshot = CalendarEventSnapshot.FromEntity(calendarEvent);
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        var tenant = calendarEvent.TenantId.Value;
        var id = calendarEvent.Id.Value.ToString();

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Insert-or-replace on the composite (tenant, id) key — matching the in-memory store's
        // upsert semantics (a re-save replaces the master + its occurrence edits).
        var existing = await ctx.CalendarEvents
            .FirstOrDefaultAsync(r => r.TenantId == tenant && r.Id == id, ct)
            .ConfigureAwait(false);
        if (existing is null)
        {
            ctx.CalendarEvents.Add(new NodeCalendarEventRow
            {
                TenantId = tenant,
                Id = id,
                SnapshotJson = json,
            });
        }
        else
        {
            existing.SnapshotJson = json;
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CalendarEvent?> GetAsync(TenantId tenantId, CalendarEventId id, CancellationToken ct = default)
    {
        var tenant = tenantId.Value;
        var idValue = id.Value.ToString();

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await ctx.CalendarEvents.AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == tenant && r.Id == idValue, ct)
            .ConfigureAwait(false);
        return row is null ? null : Deserialize(row.SnapshotJson).ToEntity();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CalendarEvent>> ListAsync(TenantId tenantId, CancellationToken ct = default)
    {
        var tenant = tenantId.Value;

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await ctx.CalendarEvents.AsNoTracking()
            .Where(r => r.TenantId == tenant)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Order by Start then Id to match the in-memory store (the snapshot carries Start; sorting
        // post-materialization keeps the JSON-on-master design simple — the thin read window is small).
        return rows
            .Select(r => Deserialize(r.SnapshotJson).ToEntity())
            .OrderBy(e => e.Start)
            .ThenBy(e => e.Id.Value)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(TenantId tenantId, CalendarEventId id, CancellationToken ct = default)
    {
        var tenant = tenantId.Value;
        var idValue = id.Value.ToString();

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await ctx.CalendarEvents
            .FirstOrDefaultAsync(r => r.TenantId == tenant && r.Id == idValue, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }
        ctx.CalendarEvents.Remove(row);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static CalendarEventSnapshot Deserialize(string json)
        => JsonSerializer.Deserialize<CalendarEventSnapshot>(json, JsonOptions)
           ?? throw new InvalidOperationException("Corrupt calendar-event snapshot in the node-local store.");
}
