namespace Harborline.Api.LocalNodeHost.Data.Search;

/// <summary>
/// One node of the local-first knowledge-graph "360-view" read-model (ADR 0135 KG-search F3-lift
/// amendment, Slice 0). A node is a projection of an EXISTING domain entity (a Party, Lease, Invoice,
/// JournalEntry, Maintenance ticket, …) — NOT a new source of truth.
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived projection, not a source of truth (Slice 0; G-5 in spirit).</b> Every row mirrors a domain
/// record already owned by another node-local store. The graph node carries only the search-facing fields:
/// the record's identity (<see cref="RecordId"/>), its <see cref="TenantId"/>, a <see cref="NodeType"/>
/// discriminator, and the searchable text (<see cref="Title"/> + <see cref="Body"/>). Slice 0 ships NO
/// embedding vector and NO model — the searchable text is indexed by a real FTS5 virtual table
/// (<c>search_fts</c>, trigram tokenizer), created alongside this table in the same migration.
/// </para>
/// <para>
/// <b>Residency is load-bearing (G-3).</b> <see cref="Residency"/> mirrors the
/// <c>GrantResidency</c>-derived residency of the source record. An <c>OnlineOnly</c> record's text is
/// <b>NEVER</b> written here — the indexer refuses it at insert, and a transition to <c>OnlineOnly</c>
/// deletes any already-indexed row. This is stronger than the read-time clip: the plaintext never enters
/// the local index at all. The column exists so the never-index invariant is enforceable + arch-testable.
/// </para>
/// <para>
/// <b>Encrypted at rest (SC-1).</b> Like every node-local table, this row lives in the SAME
/// SQLCipher-encrypted <c>local-node.db</c> file as the financial store, keyed through the same
/// connection interceptor. There is no plaintext path. The per-tenant encrypted FILE is the cross-tenant
/// isolation boundary; the <see cref="TenantId"/> column + the clip's <c>WHERE</c> are the within-file
/// record-narrowing boundary.
/// </para>
/// </remarks>
public sealed class SearchNodeRow
{
    /// <summary>
    /// The source domain record's identity — the SAME id carried by a <c>/records/&lt;id&gt;</c>
    /// <c>AuthorizedRecordIds</c> clip narrows on. Primary key (one search node per source record).
    /// </summary>
    public required string RecordId { get; set; }

    /// <summary>The active-team-derived data tenant (ADR 0032) the source record belongs to.</summary>
    public required string TenantId { get; set; }

    /// <summary>
    /// The kind of domain entity this node projects (e.g. <c>party</c>, <c>lease</c>, <c>invoice</c>,
    /// <c>journal-entry</c>, <c>maintenance-ticket</c>). A free-text discriminator — Slice 0 does not
    /// constrain it to an enum because the projected entity set grows additively.
    /// </summary>
    public required string NodeType { get; set; }

    /// <summary>The primary display title / name of the record (short searchable text).</summary>
    public required string Title { get; set; }

    /// <summary>The longer searchable body text of the record (description, notes, concatenated fields).</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// Whether the source record's data may live in the local cache (<c>Cache</c>) or is online-only
    /// (<c>OnlineOnly</c>). Mirrors <c>GrantResidency</c>. The indexer NEVER writes an <c>OnlineOnly</c>
    /// node (G-3); the column is stored so the invariant is queryable + arch-testable.
    /// </summary>
    public SearchResidency Residency { get; set; } = SearchResidency.Cache;
}

/// <summary>
/// Residency of an indexed search node — mirrors <c>Harborline.Api.Blocks.AccessGrant.GrantResidency</c>
/// (kept as a local enum so this Data layer carries no compile dependency on the residency VALUE flowing
/// through; the indexer maps the grant residency onto it). <c>OnlineOnly</c> nodes are never indexed (G-3).
/// </summary>
public enum SearchResidency
{
    /// <summary>The source record's data may live in the local cache — indexable.</summary>
    Cache = 0,

    /// <summary>The source record is online-only — NEVER written to the local index (G-3).</summary>
    OnlineOnly = 1,
}
