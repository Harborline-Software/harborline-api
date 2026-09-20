using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Harborline.Blocks.Calendar.Models;
using Harborline.Blocks.Calendar.Services;
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
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await ctx.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await UpsertAsync(ctx, calendarEvent, ct).ConfigureAwait(false);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        // The write changes this event's resources' occupancy, so their epochs move with it: a claim
        // that read capacity before this save must re-read rather than commit against it (T-659).
        await BumpEpochsAsync(ctx, calendarEvent, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> GetCapacityEpochAsync(TenantId tenantId, ParticipantRef resource, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var tenant = tenantId.Value;
        var key = EpochKey(resource);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await ctx.CalendarCapacityEpochs.AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == tenant && r.Resource == key, ct)
            .ConfigureAwait(false);
        return row?.Epoch ?? 0;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The compare and the write are ONE step: a single conditional upsert moves the epoch only while
    /// it still equals <paramref name="expectedEpoch"/>, and the event row is written in the same
    /// transaction. SQLite applies that statement atomically against every writer of the database
    /// file, so the fence does not depend on a lock held by this host process (T-659).
    /// </remarks>
    public async Task<bool> SaveIfCapacityUnchangedAsync(
        CalendarEvent calendarEvent, ParticipantRef resource, long expectedEpoch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        ArgumentNullException.ThrowIfNull(resource);
        var tenant = calendarEvent.TenantId.Value;
        var key = EpochKey(resource);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await ctx.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // The compare-and-set, one statement. The row is selected for insert when the resource has no
        // epoch row yet and the claim expected zero (the absent row IS zero), or whenever a row does
        // exist — in which case the conflict arm advances it only on an exact match. A claim that
        // expects a non-zero epoch for a resource with no row selects nothing and loses, as it must.
        // "One row affected" therefore means this claim owns the transition and nobody took it first.
        var advanced = await ctx.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO calendar_capacity_epochs (tenant_id, resource, epoch)
             SELECT {tenant}, {key}, 1
               WHERE {expectedEpoch} = 0
                  OR EXISTS (SELECT 1 FROM calendar_capacity_epochs
                             WHERE tenant_id = {tenant} AND resource = {key})
             ON CONFLICT(tenant_id, resource) DO UPDATE
               SET epoch = calendar_capacity_epochs.epoch + 1
               WHERE calendar_capacity_epochs.epoch = {expectedEpoch}
             """, ct).ConfigureAwait(false);
        if (advanced != 1)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

        await UpsertAsync(ctx, calendarEvent, ct).ConfigureAwait(false);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        // Any OTHER resource on the claim (a participant beside the claimed one) moves too.
        await BumpEpochsAsync(ctx, calendarEvent, ct, except: key).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
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
        await using var tx = await ctx.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await ctx.CalendarEvents
            .FirstOrDefaultAsync(r => r.TenantId == tenant && r.Id == idValue, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }
        var released = Deserialize(row.SnapshotJson).ToEntity();
        ctx.CalendarEvents.Remove(row);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        // A release frees capacity, so it moves the epoch exactly as a claim does.
        await BumpEpochsAsync(ctx, released, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Insert-or-replace the series master on the composite (tenant, id) key — the in-memory store's
    /// upsert semantics (a re-save replaces the master + its occurrence edits).
    /// </summary>
    private static async Task UpsertAsync(NodeLocalCalendarDbContext ctx, CalendarEvent calendarEvent, CancellationToken ct)
    {
        var tenant = calendarEvent.TenantId.Value;
        var id = calendarEvent.Id.Value.ToString();
        var json = JsonSerializer.Serialize(CalendarEventSnapshot.FromEntity(calendarEvent), JsonOptions);

        var existing = await ctx.CalendarEvents
            .FirstOrDefaultAsync(r => r.TenantId == tenant && r.Id == id, ct)
            .ConfigureAwait(false);
        if (existing is null)
        {
            ctx.CalendarEvents.Add(new NodeCalendarEventRow { TenantId = tenant, Id = id, SnapshotJson = json });
        }
        else
        {
            existing.SnapshotJson = json;
        }
    }

    /// <summary>
    /// Move the epoch of every resource the event occupies — the headline resource and every
    /// participant, which is the set the free/busy occupancy gather treats as "on the event" —
    /// skipping <paramref name="except"/>, whose epoch a conditional commit has already advanced.
    /// </summary>
    private static async Task BumpEpochsAsync(
        NodeLocalCalendarDbContext ctx, CalendarEvent calendarEvent, CancellationToken ct, string? except = null)
    {
        var tenant = calendarEvent.TenantId.Value;
        foreach (var key in Occupied(calendarEvent).Select(EpochKey).Distinct(StringComparer.Ordinal))
        {
            if (string.Equals(key, except, StringComparison.Ordinal)) continue;
            await ctx.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO calendar_capacity_epochs (tenant_id, resource, epoch) VALUES ({tenant}, {key}, 1)
                 ON CONFLICT(tenant_id, resource) DO UPDATE SET epoch = calendar_capacity_epochs.epoch + 1
                 """, ct).ConfigureAwait(false);
        }
    }

    private static IEnumerable<ParticipantRef> Occupied(CalendarEvent calendarEvent)
    {
        if (calendarEvent.ResourceRef is { } headline) yield return headline;
        foreach (var participation in calendarEvent.Participations)
            if (participation.Participant != calendarEvent.ResourceRef) yield return participation.Participant;
    }

    private static string EpochKey(ParticipantRef resource) => $"{resource.Kind}:{resource.Value}";

    private static CalendarEventSnapshot Deserialize(string json)
        => JsonSerializer.Deserialize<CalendarEventSnapshot>(json, JsonOptions)
           ?? throw new InvalidOperationException("Corrupt calendar-event snapshot in the node-local store.");
}
