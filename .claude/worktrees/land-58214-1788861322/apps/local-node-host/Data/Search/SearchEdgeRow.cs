namespace Harborline.Api.LocalNodeHost.Data.Search;

/// <summary>
/// One directed edge of the local-first knowledge-graph read-model (ADR 0135 KG-search F3-lift amendment,
/// Slice 0) — a <b>structured FK / reference relationship</b> between two domain records (e.g. a Lease
/// references a Party tenant; an Invoice references a Lease; a JournalEntry references an Invoice).
/// </summary>
/// <remarks>
/// <para>
/// <b>Edges come from structured METADATA, never body-text extraction (Slice 0 invariant).</b> Slice 0
/// derives the graph from the FK/reference columns the domain records already carry — there is NO entity
/// extraction, NO model, NO NLP. This keeps Slice 0 extractor-free and deterministic: an edge exists iff a
/// source record names a target record in a structured field.
/// </para>
/// <para>
/// <b>Graph expansion is a clipped recursive CTE.</b> A 1–2 hop neighbourhood around a candidate set is
/// computed with a recursive CTE over this table — but the expansion is itself clipped: a hop only crosses
/// to a target whose <c>record_id</c> is in the authorized set (G-1). An edge to an unauthorized record is
/// never traversed, so the graph walk cannot widen the result past the clip.
/// </para>
/// <para>
/// <b>Tenant-scoped.</b> Every edge carries the <see cref="TenantId"/> of its endpoints (both endpoints are
/// in the same tenant in Slice 0 — cross-tenant edges are out of scope). Queries filter on it; the
/// per-tenant encrypted file is the outer isolation boundary.
/// </para>
/// </remarks>
public sealed class SearchEdgeRow
{
    /// <summary>Surrogate primary key for the edge (a Guid string) — edges are not otherwise uniquely named.</summary>
    public required string Id { get; set; }

    /// <summary>The tenant both endpoints belong to (ADR 0032).</summary>
    public required string TenantId { get; set; }

    /// <summary>The source record id (the "from" endpoint) — a <see cref="SearchNodeRow.RecordId"/>.</summary>
    public required string SourceRecordId { get; set; }

    /// <summary>The target record id (the "to" endpoint) — a <see cref="SearchNodeRow.RecordId"/>.</summary>
    public required string TargetRecordId { get; set; }

    /// <summary>
    /// The relationship kind — the name of the structured reference that produced this edge (e.g.
    /// <c>lease-tenant</c>, <c>invoice-lease</c>, <c>journal-invoice</c>). Free-text; grows additively.
    /// </summary>
    public required string EdgeType { get; set; }
}
