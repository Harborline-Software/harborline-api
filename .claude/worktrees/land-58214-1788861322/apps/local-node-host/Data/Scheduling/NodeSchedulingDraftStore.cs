using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Scheduling;

/// <summary>Durable, tenant-scoped immutable revision history for scheduling drafts.</summary>
public sealed class NodeSchedulingDraftStore(
    IDbContextFactory<NodeLocalSchedulingDbContext> contextFactory,
    TimeProvider timeProvider)
{
    public const int MaxDefinitionBytes = 256 * 1024;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public async Task<SchedulingDraftView> SaveAsync(
        string tenantId, string definitionId, JsonElement definition, int expectedRevision,
        string actorId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (definition.ValueKind != JsonValueKind.Object)
            throw new SchedulingDraftShapeException("scheduling.draft.object_required");
        if (definitionId.Length > 160)
            throw new SchedulingDraftShapeException("scheduling.draft.id_too_long");
        if (Encoding.UTF8.GetByteCount(definition.GetRawText()) > MaxDefinitionBytes)
            throw new SchedulingDraftShapeException("scheduling.draft.payload_too_large");
        if (expectedRevision < 0)
            throw new SchedulingDraftShapeException("scheduling.draft.expected_revision_invalid");

        var gate = _gates.GetOrAdd($"{tenantId}\n{definitionId}", static _ => new(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var current = await db.Drafts
                .Where(x => x.TenantId == tenantId && x.DefinitionId == definitionId)
                .MaxAsync(x => (int?)x.Revision, ct).ConfigureAwait(false) ?? 0;
            if (current != expectedRevision)
                throw new SchedulingDraftConflictException(current);

            var occurredAt = timeProvider.GetUtcNow();
            var row = new NodeSchedulingDraftRow
            {
                TenantId = tenantId,
                DefinitionId = definitionId,
                Revision = current + 1,
                DefinitionJson = definition.GetRawText(),
                UpdatedBy = actorId,
                UpdatedAtUtc = occurredAt,
            };
            db.Drafts.Add(row);
            db.DraftAudit.Add(new NodeSchedulingDraftAuditRow
            {
                TenantId = tenantId,
                AuditId = Guid.NewGuid().ToString("D"),
                DefinitionId = definitionId,
                Revision = row.Revision,
                ActorId = actorId,
                OccurredAtUtc = occurredAt,
            });
            try
            {
                // The composite draft PK and unique audit revision are the cross-process fence;
                // the in-process gate only reduces avoidable local contention.
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (
                ex.InnerException is SqliteException { SqliteErrorCode: 19 })
            {
                var persisted = await CurrentRevisionAsync(tenantId, definitionId, ct).ConfigureAwait(false);
                throw new SchedulingDraftConflictException(persisted);
            }
            return View(row);
        }
        finally { gate.Release(); }
    }

    public async Task<SchedulingDraftView?> GetAsync(string tenantId, string definitionId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Drafts.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.DefinitionId == definitionId)
            .OrderByDescending(x => x.Revision).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null ? null : View(row);
    }

    /// <summary>Lists every retained revision of one definition, newest first (empty when unknown).</summary>
    public async Task<IReadOnlyList<SchedulingDraftSummary>> ListRevisionsAsync(
        string tenantId, string definitionId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Drafts.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.DefinitionId == definitionId)
            .OrderByDescending(x => x.Revision)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(x => new SchedulingDraftSummary(x.DefinitionId, x.Revision, Title(x), x.UpdatedAtUtc, x.UpdatedBy)).ToArray();
    }

    /// <summary>Reads one exact retained revision, or null when absent.</summary>
    public async Task<SchedulingDraftView?> GetRevisionAsync(
        string tenantId, string definitionId, int revision, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Drafts.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.DefinitionId == definitionId && x.Revision == revision)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null ? null : View(row);
    }

    public async Task<IReadOnlyList<SchedulingDraftSummary>> ListAsync(string tenantId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Drafts.AsNoTracking().Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.DefinitionId).ThenByDescending(x => x.Revision)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.GroupBy(x => x.DefinitionId, StringComparer.Ordinal).Select(g => g.First())
            .Select(x => new SchedulingDraftSummary(x.DefinitionId, x.Revision, Title(x), x.UpdatedAtUtc, x.UpdatedBy))
            .ToArray();
    }

    private static SchedulingDraftView View(NodeSchedulingDraftRow row)
    {
        using var doc = JsonDocument.Parse(row.DefinitionJson);
        return new(row.DefinitionId, row.Revision, doc.RootElement.Clone(), row.UpdatedAtUtc, row.UpdatedBy);
    }

    private async Task<int> CurrentRevisionAsync(string tenantId, string definitionId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Drafts.Where(x => x.TenantId == tenantId && x.DefinitionId == definitionId)
            .MaxAsync(x => (int?)x.Revision, ct).ConfigureAwait(false) ?? 0;
    }

    private static string Title(NodeSchedulingDraftRow row)
    {
        using var doc = JsonDocument.Parse(row.DefinitionJson);
        return doc.RootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
            ? title.GetString() ?? row.DefinitionId : row.DefinitionId;
    }
}

public sealed record SchedulingDraftView(string Id, int Revision, JsonElement Definition, DateTimeOffset UpdatedAt, string UpdatedBy);
public sealed record SchedulingDraftSummary(string Id, int Revision, string Title, DateTimeOffset UpdatedAt, string UpdatedBy);
public sealed class SchedulingDraftConflictException(int currentRevision) : Exception("Scheduling draft revision conflict.")
{ public int CurrentRevision { get; } = currentRevision; }
public sealed class SchedulingDraftShapeException(string code) : Exception("Scheduling draft request is malformed.")
{ public string Code { get; } = code; }
