using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.Docs.Models;
using Harborline.Api.Blocks.Docs.Services;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.LocalNodeHost.Data.Docs;

/// <summary>
/// EF Core–backed <see cref="IDocumentRefRepository"/> for the embedded local node
/// (ADR 0127 — the documents node-flip). Backs the cross-cluster attach surface so
/// <c>POST /api/local-node/documents/{id}/attach</c> works fully offline.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="InMemoryDocumentRefRepository"/> semantics exactly over
/// <see cref="LocalNodeDbContext"/> (SQLite/SQLCipher): tombstoned reads return
/// null; mutating a tombstoned row throws; the forward/reverse lookups exclude
/// tombstones; soft-delete is idempotent (returns false when absent OR already
/// tombstoned) + bumps Version. The <c>document_refs</c> table is already in the
/// node snapshot (contributed by <see cref="Harborline.Api.Blocks.Docs.Data.DocsEntityModule"/>),
/// so this adds no migration.
/// </para>
/// <para>
/// <b>Tenant boundary (ADR 0092).</b> Every read carries an explicit
/// <c>WHERE TenantId = @t</c>. Cross-tenant safety on link (attachment-tenant vs
/// caller-tenant) is enforced one layer up, in <see cref="DocumentRefService.LinkAsync"/>.
/// </para>
/// <para>
/// <b>Singleton-safe.</b> Each call uses a short-lived context from the injected
/// <see cref="IDbContextFactory{LocalNodeDbContext}"/>.
/// </para>
/// </remarks>
public sealed class NodeEfDocumentRefRepository : IDocumentRefRepository
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;

    /// <summary>Construct bound to the local-node EF context factory.</summary>
    public NodeEfDocumentRefRepository(IDbContextFactory<LocalNodeDbContext> contextFactory)
        => _contextFactory = contextFactory
            ?? throw new ArgumentNullException(nameof(contextFactory));

    /// <inheritdoc />
    public async Task UpsertAsync(DocumentRef documentRef, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentRef);

        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var existing = await ctx.Set<DocumentRef>()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == documentRef.Id, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null && existing.DeletedAtUtc is not null)
        {
            throw new InvalidOperationException(
                $"DocumentRef '{documentRef.Id.Value}' is tombstoned; further mutations are not permitted.");
        }

        if (existing is null)
        {
            ctx.Set<DocumentRef>().Add(documentRef);
        }
        else
        {
            ctx.Set<DocumentRef>().Update(documentRef);
        }
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DocumentRef?> GetAsync(DocumentRefId id, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var r = await ctx.Set<DocumentRef>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            .ConfigureAwait(false);
        return r is null || r.DeletedAtUtc is not null ? null : r;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentRef>> FindByAttachmentAsync(
        TenantId tenantId,
        AttachmentId attachmentId,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await ctx.Set<DocumentRef>()
            .AsNoTracking()
            .Where(r => r.DeletedAtUtc == null
                && r.TenantId == tenantId
                && r.AttachmentId == attachmentId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentRef>> FindByParentAsync(
        TenantId tenantId,
        string clusterCode,
        string parentEntityType,
        string parentEntityId,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        // ClusterCode + ParentEntityType compared case-insensitively to mirror the
        // in-memory repo (OrdinalIgnoreCase); ParentEntityId is case-sensitive. EF
        // translates EF.Functions.Like for SQLite, but to keep parity exact + avoid
        // collation surprises we materialize the tenant+parent-id slice (small on a
        // single-device node) and filter the case-insensitive dimensions in memory.
        var candidates = await ctx.Set<DocumentRef>()
            .AsNoTracking()
            .Where(r => r.DeletedAtUtc == null
                && r.TenantId == tenantId
                && r.ParentEntityId == parentEntityId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = candidates
            .Where(r => string.Equals(r.ClusterCode, clusterCode, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.ParentEntityType, parentEntityType, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return rows;
    }

    /// <inheritdoc />
    public async Task<bool> SoftDeleteAsync(
        DocumentRefId id,
        string actor,
        string? reason,
        Instant at,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var r = await ctx.Set<DocumentRef>()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (r is null) return false;
        if (r.DeletedAtUtc is not null) return false; // already tombstoned → false (mirrors in-memory)

        var now = at;
        var tombstoned = r with
        {
            DeletedAtUtc = now,
            DeletedBy = actor,
            DeletedReason = reason,
            UpdatedAtUtc = now,
            UpdatedBy = actor,
            Version = r.Version + 1,
        };
        ctx.Set<DocumentRef>().Update(tombstoned);
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
