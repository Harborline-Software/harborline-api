using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Blocks.Calendar.Services;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.LocalNodeHost.Data.Calendar;

/// <summary>
/// The durable EF/SQLite <see cref="ICalendarStore"/> (calendar productization #149, slice C1) — replaces
/// the block's in-memory default in the node host. Persists each <see cref="OwnedCalendar"/> in the SAME
/// SQLCipher <c>local-node.db</c> file (via <see cref="NodeLocalCalendarDbContext"/>) as the calendar
/// events, tenant-scoped + cross-tenant isolated.
/// </summary>
/// <remarks>
/// Mirrors <see cref="NodeEfCalendarEventStore"/> exactly: serializes the block's
/// <see cref="CalendarSnapshot"/> with the SAME <see cref="JsonSerializerOptions"/>
/// (<see cref="JsonSerializerDefaults.General"/>), so a saved calendar re-loads identically; the only
/// difference from the in-memory store is the durable sink (a SQLite row vs a dictionary entry).
/// </remarks>
public sealed class NodeEfCalendarStore : ICalendarStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    private readonly IDbContextFactory<NodeLocalCalendarDbContext> _contextFactory;

    /// <summary>Construct bound to the calendar context factory (the SQLCipher file the store lives in).</summary>
    public NodeEfCalendarStore(IDbContextFactory<NodeLocalCalendarDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    /// <inheritdoc />
    public async Task SaveAsync(OwnedCalendar calendar, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var json = JsonSerializer.Serialize(CalendarSnapshot.FromEntity(calendar), JsonOptions);
        var tenant = calendar.TenantId.Value;
        var id = calendar.Id.Value.ToString();

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Insert-or-replace on the composite (tenant, id) key — matching the in-memory upsert semantics.
        var existing = await ctx.Calendars
            .FirstOrDefaultAsync(r => r.TenantId == tenant && r.Id == id, ct)
            .ConfigureAwait(false);
        if (existing is null)
        {
            ctx.Calendars.Add(new NodeCalendarRow
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
    public async Task<OwnedCalendar?> GetAsync(TenantId tenantId, CalendarId id, CancellationToken ct = default)
    {
        var tenant = tenantId.Value;
        var idValue = id.Value.ToString();

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await ctx.Calendars.AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == tenant && r.Id == idValue, ct)
            .ConfigureAwait(false);
        return row is null ? null : Deserialize(row.SnapshotJson).ToEntity();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OwnedCalendar>> ListAsync(TenantId tenantId, CancellationToken ct = default)
    {
        var tenant = tenantId.Value;

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await ctx.Calendars.AsNoTracking()
            .Where(r => r.TenantId == tenant)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Order the default first, then by created time (matching the in-memory store; the read window
        // is small, so post-materialization sorting keeps the JSON-on-master design simple).
        return rows
            .Select(r => Deserialize(r.SnapshotJson).ToEntity())
            .OrderByDescending(c => c.IsDefault)
            .ThenBy(c => c.CreatedAt)
            .ThenBy(c => c.Id.Value)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(TenantId tenantId, CalendarId id, CancellationToken ct = default)
    {
        var tenant = tenantId.Value;
        var idValue = id.Value.ToString();

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await ctx.Calendars
            .FirstOrDefaultAsync(r => r.TenantId == tenant && r.Id == idValue, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }
        ctx.Calendars.Remove(row);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static CalendarSnapshot Deserialize(string json)
        => JsonSerializer.Deserialize<CalendarSnapshot>(json, JsonOptions)
           ?? throw new InvalidOperationException("Corrupt calendar snapshot in the node-local store.");
}
