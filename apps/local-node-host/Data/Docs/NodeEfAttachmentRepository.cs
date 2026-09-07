using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.Docs.Models;
using Harborline.Api.Blocks.Docs.Services;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.LocalNodeHost.Data.Docs;

/// <summary>
/// EF Core–backed <see cref="IAttachmentRepository"/> for the embedded local node
/// (ADR 0127 — the documents node-flip; ADR 0113 ABSOLUTE local-first).
/// </summary>
/// <remarks>
/// <para>
/// <b>The node now owns documents data.</b> Before this flip the node carried the
/// <c>attachments</c> + <c>document_refs</c> SCHEMA (contributed by
/// <see cref="Harborline.Api.Blocks.Docs.Data.DocsEntityModule"/> into
/// <see cref="LocalNodeDbContext"/>, <c>StorageRef</c> as JSON) but had NO
/// <see cref="IAttachmentRepository"/> registration and no document writes — so
/// documents were Bridge-dependent. This gives the node a read/write surface over
/// the SAME SQLCipher store (<c>local-node.db</c>) the financial/people clusters
/// use, so a single-device install no longer needs signal-bridge for documents.
/// </para>
/// <para>
/// <b>Inline bytes ride inside the SQLCipher store (SC-1 by construction).</b>
/// <c>StorageRef</c> is persisted as JSON in the <c>storage_ref_json</c> column
/// (the post-config sweep rewrites Postgres <c>jsonb</c> → SQLite <c>TEXT</c>);
/// <c>StorageRef.ForInline(bytes)</c> serializes its <c>ReadOnlyMemory&lt;byte&gt;</c>
/// payload as a base64 string inside that JSON. So the file bytes round-trip
/// through a column that ALREADY exists in the node snapshot — no migration (ADR
/// 0127 §"Migration posture"). The page cipher encrypts the BLOB at rest.
/// </para>
/// <para>
/// <b>Mirrors <see cref="InMemoryAttachmentRepository"/> semantics exactly</b>, with
/// the backing store being <see cref="LocalNodeDbContext"/> (SQLite/SQLCipher)
/// instead of a concurrent dictionary: tombstoned reads return null; mutating a
/// tombstoned row throws; <c>ListByTenantAsync</c> + <c>FindByContentHashAsync</c>
/// exclude tombstones; quota sums Active rows only; soft-delete is idempotent +
/// bumps Version. Audit emission is the durable-layer concern (ADR 0104 §7
/// X-AUDIT) — deferred, matching the node JE/bill stores (the row's presence in the
/// keyed SQLCipher store is the audit record).
/// </para>
/// <para>
/// <b>Tenant boundary (ADR 0092).</b> <see cref="LocalNodeDbContext"/> applies NO
/// ambient tenant query filter, so every read here carries an explicit
/// <c>WHERE TenantId = @t</c> (defence-in-depth). The routes pass the active-team-derived
/// tenant (<c>NodeTenant.Resolve(activeTeam)</c> / <c>ActiveTeamTenantContext</c>; ADR 0032
/// identity layer), so the explicit <c>WHERE TenantId</c> is the per-org isolation
/// predicate — switching the active org switches the rows, no fixed <c>"local"</c> sentinel.
/// </para>
/// <para>
/// <b>Singleton-safe.</b> Each call uses a short-lived context from the injected
/// <see cref="IDbContextFactory{LocalNodeDbContext}"/> (mirrors
/// <see cref="Financial.NodeEfBillRepository"/> / <see cref="Financial.NodeEfJournalStore"/>)
/// — the ambient scoped context may already be disposed at call time, so the repo
/// never holds one.
/// </para>
/// </remarks>
public sealed class NodeEfAttachmentRepository : IAttachmentRepository
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;

    /// <summary>Construct bound to the local-node EF context factory.</summary>
    public NodeEfAttachmentRepository(IDbContextFactory<LocalNodeDbContext> contextFactory)
        => _contextFactory = contextFactory
            ?? throw new ArgumentNullException(nameof(contextFactory));

    /// <inheritdoc />
    public async Task UpsertAsync(Attachment attachment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var existing = await ctx.Set<Attachment>()
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == attachment.Id, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null && existing.DeletedAtUtc is not null)
        {
            throw new InvalidOperationException(
                $"Attachment '{attachment.Id.Value}' is tombstoned; further mutations are not permitted.");
        }

        if (existing is null)
        {
            ctx.Set<Attachment>().Add(attachment);
        }
        else
        {
            ctx.Set<Attachment>().Update(attachment);
        }
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Attachment?> GetAsync(AttachmentId id, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var a = await ctx.Set<Attachment>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            .ConfigureAwait(false);
        return a is null || a.DeletedAtUtc is not null ? null : a;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Attachment>> FindByContentHashAsync(
        TenantId tenantId,
        string contentHash,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await ctx.Set<Attachment>()
            .AsNoTracking()
            .Where(a => a.DeletedAtUtc == null
                && a.TenantId == tenantId
                && a.ContentHash == contentHash)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Attachment>> ListByTenantAsync(TenantId tenantId, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await ctx.Set<Attachment>()
            .AsNoTracking()
            .Where(a => a.DeletedAtUtc == null && a.TenantId == tenantId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows;
    }

    /// <inheritdoc />
    public async Task<long> GetTenantTotalSizeBytesAsync(TenantId tenantId, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        // Active rows only — Superseded/Tombstoned don't count toward the tenant's
        // current footprint (mirrors InMemoryAttachmentRepository + the quota gate).
        var total = await ctx.Set<Attachment>()
            .AsNoTracking()
            .Where(a => a.DeletedAtUtc == null
                && a.TenantId == tenantId
                && a.Status == AttachmentStatus.Active)
            .SumAsync(a => a.SizeBytes, cancellationToken)
            .ConfigureAwait(false);
        return total;
    }

    /// <inheritdoc />
    public async Task<bool> SoftDeleteAsync(AttachmentId id, string actor, string? reason, Instant at, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var a = await ctx.Set<Attachment>()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (a is null) return false;
        if (a.DeletedAtUtc is not null) return true; // idempotent

        var now = at;
        var tombstoned = a with
        {
            Status = AttachmentStatus.Tombstoned,
            DeletedAtUtc = now,
            DeletedBy = actor,
            DeletedReason = reason,
            UpdatedAtUtc = now,
            UpdatedBy = actor,
            Version = a.Version + 1,
        };
        ctx.Set<Attachment>().Update(tombstoned);
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
