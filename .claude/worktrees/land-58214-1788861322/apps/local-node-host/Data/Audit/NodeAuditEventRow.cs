namespace Harborline.Api.LocalNodeHost.Data.Audit;

/// <summary>
/// Append-only node audit-event row, the audit system-of-record for the embedded local node
/// (ADR 0126, Option C). Mapped into <see cref="LocalNodeDbContext"/> via
/// <see cref="AuditEventEntityModule"/> (Pattern A, forced by the OQ2 = atomic ruling) so its
/// append commits in the SAME <c>local-node.db</c> transaction as the journal-entry write.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a dedicated row type, not the kernel/foundation <c>AuditRecord</c>.</b> The kernel
/// <c>Harborline.Api.Kernel.Audit.AuditRecord</c> carries a <c>SignedOperation&lt;AuditPayload&gt;</c>
/// envelope (Ed25519-locked, seed-derived) that is awkward to map relationally and is exactly the
/// keyed-signature shape ADR 0126's OQ1 epoch ruling works around. The foundation
/// <c>Harborline.Api.Foundation.Assets.Audit.AuditRecord</c> is a different (long-keyed) shape used by the
/// foundation <c>HashChain</c>. This row mirrors the WIRE shape the frontend already consumes
/// (<c>audit-events.ts</c>) per ADR 0126 §D1: <c>audit_id</c>, <c>occurred_at</c>, <c>event_type</c>,
/// <c>actor</c>, <c>correlation_id</c>, <c>tenant_id</c>, <c>payload</c> (canonical JSON),
/// <c>prev_hash</c>, <c>hash</c>, <c>signature</c> (nullable). The <see cref="NodeAuditEventReader"/>
/// projects rows → the kernel <c>IAuditEventReader</c> contract type and computes
/// <c>signature_state</c> offline.
/// </para>
/// <para>
/// <b>Strictly append-only (ADR 0126 §D1 / §No-hard-DELETE).</b> No UPDATE, no DELETE — immutability
/// is the audit invariant; the node is the single writer. Corrections are new compensating records.
/// </para>
/// <para>
/// <b>SC-4-safe by construction.</b> The row lives in the recoverable, Store-DEK-enveloped
/// <c>local-node.db</c>, so a passphrase reseed (which mints a new root seed and orphans the
/// per-team seed-keyed <c>IEventLog</c>) preserves the audit history with everything else in
/// <c>local-node.db</c>.
/// </para>
/// </remarks>
public sealed class NodeAuditEventRow
{
    /// <summary>Stable record identifier (canonical GUID string). The reader projects this to the
    /// kernel <c>AuditRecord.AuditId</c>.</summary>
    public required string AuditId { get; init; }

    /// <summary>The tenant this record is scoped to — the active-team-derived tenant
    /// (<c>ActiveTeamTenantContext</c> / <c>NodeTenant.Resolve</c>; ADR 0032 identity layer), not a fixed
    /// <c>"local"</c> sentinel. The explicit column + WHERE filter is the node's defence-in-depth,
    /// per-org tenant boundary (ADR 0092 — no ambient query filter on the node).</summary>
    public required string TenantId { get; init; }

    /// <summary>The <c>AuditEventType.Value</c> string (e.g. <c>"Financial.JournalPosted"</c>,
    /// <c>"TenantBoundaryViolation"</c>).</summary>
    public required string EventType { get; init; }

    /// <summary>Wall-clock time at which the audited event occurred (the reverse-chronological
    /// cursor key: <c>OccurredAt DESC, AuditId DESC</c>).</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>The acting principal (user / system identity). On the node this is the install
    /// operator. Surfaces as the wire <c>actor</c> field.</summary>
    public string? Actor { get; init; }

    /// <summary>Correlation id linking this audit event to its originating action. Sourced from the
    /// payload; nullable for events without one. Filterable.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Canonical JSON of the audit payload body (the wire <c>payload_summary</c>).</summary>
    public required string Payload { get; init; }

    /// <summary>The predecessor record's <see cref="Hash"/> in this tenant's chain (null for the
    /// first record). Part of the offline <c>HashChain</c> integrity verification.</summary>
    public string? PrevHash { get; init; }

    /// <summary>SHA-256 content hash chaining this record to its predecessor (hex, lowercase).
    /// Recomputed offline at read time to produce the integrity verdict.</summary>
    public required string Hash { get; init; }

    /// <summary>Optional Ed25519 signature bytes over the payload (seed-derived; verified against
    /// the sealed pre-reseed epoch key per ADR 0126 §D4 / OQ1). Null when the event was not signed.</summary>
    public byte[]? Signature { get; init; }
}
