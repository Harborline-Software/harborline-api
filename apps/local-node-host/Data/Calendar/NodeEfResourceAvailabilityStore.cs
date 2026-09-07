using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Blocks.Calendar.Services;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.LocalNodeHost.Data.Calendar;

/// <summary>
/// The durable EF/SQLite <see cref="IResourceAvailabilityStore"/> (ONR app-calendar survey
/// 2026-06-24, inc-0) — replaces the block's in-memory default in the node host. free/busy needs BOTH
/// events AND availability, so the durable store persists both. Persists each availability record in
/// the SAME SQLCipher <c>local-node.db</c> file (via <see cref="NodeLocalCalendarDbContext"/>),
/// tenant-scoped + cross-tenant isolated.
/// </summary>
/// <remarks>
/// Mirrors <see cref="InMemoryResourceAvailabilityStore"/>: it serializes the block's
/// <see cref="ResourceAvailabilitySnapshot"/> with the SAME <see cref="JsonSerializerOptions"/>, keyed
/// by composite <c>(TenantId, ResourceKind, ResourceValue)</c> (one record per resource — re-saving
/// replaces it), so the windows + exception dates/spans genuinely round-trip.
/// </remarks>
public sealed class NodeEfResourceAvailabilityStore : IResourceAvailabilityStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    private readonly IDbContextFactory<NodeLocalCalendarDbContext> _contextFactory;

    /// <summary>Construct bound to the calendar context factory (the SQLCipher file the store lives in).</summary>
    public NodeEfResourceAvailabilityStore(IDbContextFactory<NodeLocalCalendarDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    /// <inheritdoc />
    public async Task SaveAsync(ResourceAvailability availability, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(availability);
        var snapshot = ResourceAvailabilitySnapshot.FromEntity(availability);
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        var tenant = availability.TenantId.Value;
        var kind = (int)availability.ResourceRef.Kind;
        var value = availability.ResourceRef.Value;

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var existing = await ctx.ResourceAvailability
            .FirstOrDefaultAsync(
                r => r.TenantId == tenant && r.ResourceKind == kind && r.ResourceValue == value, ct)
            .ConfigureAwait(false);
        if (existing is null)
        {
            ctx.ResourceAvailability.Add(new NodeResourceAvailabilityRow
            {
                TenantId = tenant,
                ResourceKind = kind,
                ResourceValue = value,
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
    public async Task<ResourceAvailability?> GetAsync(
        TenantId tenantId, ParticipantRef resourceRef, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resourceRef);
        var tenant = tenantId.Value;
        var kind = (int)resourceRef.Kind;
        var value = resourceRef.Value;

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await ctx.ResourceAvailability.AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.TenantId == tenant && r.ResourceKind == kind && r.ResourceValue == value, ct)
            .ConfigureAwait(false);
        return row is null ? null : Deserialize(row.SnapshotJson).ToEntity();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ResourceAvailability>> ListAsync(TenantId tenantId, CancellationToken ct = default)
    {
        var tenant = tenantId.Value;

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await ctx.ResourceAvailability.AsNoTracking()
            .Where(r => r.TenantId == tenant)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .Select(r => Deserialize(r.SnapshotJson).ToEntity())
            .OrderBy(r => r.ResourceRef.Kind)
            .ThenBy(r => r.ResourceRef.Value)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(
        TenantId tenantId, ParticipantRef resourceRef, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resourceRef);
        var tenant = tenantId.Value;
        var kind = (int)resourceRef.Kind;
        var value = resourceRef.Value;

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await ctx.ResourceAvailability
            .FirstOrDefaultAsync(
                r => r.TenantId == tenant && r.ResourceKind == kind && r.ResourceValue == value, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }
        ctx.ResourceAvailability.Remove(row);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static ResourceAvailabilitySnapshot Deserialize(string json)
        => JsonSerializer.Deserialize<ResourceAvailabilitySnapshot>(json, JsonOptions)
           ?? throw new InvalidOperationException("Corrupt resource-availability snapshot in the node-local store.");
}
