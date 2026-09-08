using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Data.Search;

/// <summary>
/// The write side of the local-first knowledge-graph read-model (ADR 0135 KG-search F3-lift amendment,
/// Slice 0) — upserts node projections + edges into the search index, and is the structural enforcement
/// point for the <b>G-3 residency invariant</b>: an <c>OnlineOnly</c> record is NEVER written to the local
/// index, and the structural delete path for a transition to <c>OnlineOnly</c> exists here. <b>Slice 0 is
/// substrate-only:</b> the insert-refusal is live (every index call runs through it), but the transition /
/// delete-on-revoke / GDPR-shred paths are <b>enforceable but not yet wired</b> to any
/// revocation / void / archive event — connecting them to the event source is a consuming-slice DoD, not
/// part of this slice.
/// </summary>
/// <remarks>
/// <para>
/// <b>G-3 insert-refusal — live HERE (the structural half).</b> <see cref="IndexNodeAsync"/> refuses to
/// write an <see cref="SearchResidency.OnlineOnly"/> node — it is a no-op (or a delete of a pre-existing
/// row), never an insert. Because every index call goes through this method, the refusal is effective the
/// moment the indexer is wired to a projection source. This is stronger than the read-time clip: the
/// plaintext text / projection NEVER enters the local index. The FTS5 content stays in sync via the triggers
/// the migration installs, so a refused / deleted node also leaves no FTS5 row.
/// </para>
/// <para>
/// <b>G-3 delete-on-revoke-to-OnlineOnly (the transition half — enforceable, not yet wired).</b> When a
/// record's residency flips to <c>OnlineOnly</c> (e.g. a grant is revoked + re-issued online-only, or the
/// residency is changed), <see cref="OnResidencyChangedAsync"/> deletes the indexed node (and, by the FTS5
/// delete trigger, its FTS5 row) and its incident edges. This method PROVIDES the structural delete path;
/// Slice 0 does not yet CALL it from a revocation / residency-change event — that wiring is the consuming
/// slice's DoD. So the transition guarantee is enforceable-but-unwired today, not enforced at runtime.
/// </para>
/// <para>
/// <b>FTS5 stays in sync via triggers, not manual writes.</b> The <c>search_fts</c> virtual table is
/// content-synchronised to <c>search_nodes</c> by AFTER INSERT / UPDATE / DELETE triggers installed in the
/// migration, so this indexer writes only the ordinary <c>search_nodes</c> / <c>search_edges</c> rows and
/// the FTS5 index follows automatically. There is no path that writes FTS5 text for an unindexed node.
/// </para>
/// </remarks>
public sealed class NodeSearchIndexer
{
    private readonly IDbContextFactory<NodeLocalSearchDbContext> _contextFactory;

    /// <summary>Construct bound to the node-local search EF context factory.</summary>
    public NodeSearchIndexer(IDbContextFactory<NodeLocalSearchDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new System.ArgumentNullException(nameof(contextFactory));
    }

    /// <summary>
    /// Upserts a node projection into the index — UNLESS its residency is <see cref="SearchResidency.OnlineOnly"/>,
    /// in which case it is NEVER written (G-3) and any pre-existing indexed row for that record is deleted.
    /// </summary>
    /// <param name="node">The node projection to index. Its <see cref="SearchNodeRow.Residency"/> gates the write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the node was indexed; false if it was refused (online-only) — i.e. not in the local index.</returns>
    public async Task<bool> IndexNodeAsync(
        SearchNodeRow node,
        AuthorizationDecision originatingDecision,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        originatingDecision.RequireAllowedReaction(
            AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite),
            new TenantId(node.TenantId),
            "record",
            node.RecordId);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // G-3 structural half: an OnlineOnly node is NEVER inserted. If one is already indexed (residency
        // changed to online-only), remove it. Then return false — it is not in the local index.
        if (node.Residency == SearchResidency.OnlineOnly)
        {
            await DeleteNodeInternalAsync(ctx, node.TenantId, node.RecordId, ct).ConfigureAwait(false);
            return false;
        }

        var existing = await ctx.Nodes
            .FirstOrDefaultAsync(n => n.TenantId == node.TenantId && n.RecordId == node.RecordId, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            ctx.Nodes.Add(node);
        }
        else
        {
            existing.NodeType = node.NodeType;
            existing.Title = node.Title;
            existing.Body = node.Body;
            existing.Residency = node.Residency;
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Indexes (replaces) the set of edges incident on <paramref name="sourceRecordId"/> — the structured
    /// FK/reference relationships derived from the source record's metadata. The incident edges are replaced
    /// wholesale so a re-projection of a record reflects its current references.
    /// </summary>
    /// <param name="sourceRecordId">The record whose outgoing structured references these edges represent.</param>
    /// <param name="edges">The current set of edges for that source.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task IndexEdgesAsync(
        string tenantId,
        string sourceRecordId,
        System.Collections.Generic.IReadOnlyList<SearchEdgeRow> edges,
        AuthorizationDecision originatingDecision,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(sourceRecordId);
        ArgumentNullException.ThrowIfNull(edges);
        originatingDecision.RequireAllowedReaction(
            AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite),
            new TenantId(tenantId),
            "record",
            sourceRecordId);
        if (edges.Any(edge =>
                !string.Equals(edge.TenantId, tenantId, StringComparison.Ordinal)
                || !string.Equals(edge.SourceRecordId, sourceRecordId, StringComparison.Ordinal)))
            throw new ArgumentException(
                "Every edge must belong to the authorized tenant and source record.", nameof(edges));

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var stale = await ctx.Edges
            .Where(e => e.TenantId == tenantId && e.SourceRecordId == sourceRecordId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        ctx.Edges.RemoveRange(stale);
        ctx.Edges.AddRange(edges);

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reacts to a residency change for a record (G-3 transition half). When the new residency is
    /// <see cref="GrantResidency.OnlineOnly"/>, the indexed node + its incident edges are DELETED (the FTS5
    /// row follows via the delete trigger), so an online-only record leaves no plaintext in the local index.
    /// A change back to <c>Cache</c> is a no-op here — the record is re-indexed on its next projection.
    /// </summary>
    /// <param name="recordId">The record whose residency changed.</param>
    /// <param name="newResidency">The new residency.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task OnResidencyChangedAsync(
        string tenantId,
        string recordId,
        GrantResidency newResidency,
        AuthorizationDecision originatingDecision,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(recordId);
        originatingDecision.RequireAllowedReaction(
            AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite),
            new TenantId(tenantId),
            "record",
            recordId);

        if (newResidency != GrantResidency.OnlineOnly)
        {
            return;
        }

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await DeleteNodeInternalAsync(ctx, tenantId, recordId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a record from the index entirely — the node, its incident edges, and (via the FTS5 delete
    /// trigger) its FTS5 row. This is the structural delete path FOR the GDPR-shred / void / archive flows so
    /// a removed record leaves no searchable residue (G-5 freshness; Slice 0 has no embedding so this is a
    /// clean physical delete). Slice 0 provides the path; wiring it to those flows is a consuming-slice DoD.
    /// </summary>
    public async Task DeleteRecordAsync(
        string tenantId,
        string recordId,
        AuthorizationDecision originatingDecision,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(recordId);
        originatingDecision.RequireAllowedReaction(
            AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite),
            new TenantId(tenantId),
            "record",
            recordId);
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await DeleteNodeInternalAsync(ctx, tenantId, recordId, ct).ConfigureAwait(false);
    }

    private static async Task DeleteNodeInternalAsync(
        NodeLocalSearchDbContext ctx, string tenantId, string recordId, CancellationToken ct)
    {
        var node = await ctx.Nodes
            .FirstOrDefaultAsync(n => n.TenantId == tenantId && n.RecordId == recordId, ct)
            .ConfigureAwait(false);
        if (node is not null)
        {
            ctx.Nodes.Remove(node);
        }

        var incident = await ctx.Edges
            .Where(e => e.TenantId == tenantId
                && (e.SourceRecordId == recordId || e.TargetRecordId == recordId))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        ctx.Edges.RemoveRange(incident);

        if (node is not null || incident.Count > 0)
        {
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}
