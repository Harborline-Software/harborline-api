using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Kernel.Audit;

/// <summary>
/// Read-side audit substrate primitive. Counterpart to the write-side
/// <see cref="IAuditTrail"/> interface (ADR 0049). Optimized for paginated
/// UI surfaces (the cohort-4 audit-trail viewer per W#78), drill-down
/// detail pages, and streaming CSV export. Tenant-scoping discipline
/// inherits ADR 0091's <c>ITenantContext</c> resolution + ADR 0092's
/// EXPLICIT-first-positional-parameter + uniform-empty-cross-tenant +
/// audit-emission-on-cross-tenant-probe substrate norms.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layering.</b> Per ADR 0094 (this ADR), <c>IAuditEventReader</c> is
/// the canonical read-side surface for the audit substrate. The
/// production implementation (<see cref="EventLogBackedAuditTrail"/>
/// counterpart) layers over the SAME kernel <c>IEventLog</c> substrate as
/// <see cref="EventLogBackedAuditTrail"/> — read and write share storage;
/// the separation is contractual, not physical.
/// </para>
///
/// <para>
/// <b>Read-write separation rationale.</b> ADR 0049's <see cref="IAuditTrail.QueryAsync"/>
/// remains the kernel-internal subscription-style stream for compliance
/// projections and retention reporters. <c>IAuditEventReader</c> is the
/// UI-adjacent paginated-list + get-by-id + export-stream contract for
/// Bridge handlers and future audit-trail viewers. The two contracts
/// coexist; new read-side consumers SHOULD prefer this interface.
/// </para>
///
/// <para>
/// <b>Tenant scoping (ADR 0091 + ADR 0092).</b> Every method accepts
/// <see cref="TenantId"/> as the FIRST positional parameter. Sourcing the
/// tenant value is the caller's responsibility — at the Bridge layer the
/// canonical pattern is
/// <c>var tenantId = new TenantId(tenantContext.TenantId)</c>. Cross-tenant
/// reads return uniform-empty (null for <see cref="GetByIdAsync"/>; empty
/// page for <see cref="ListAsync"/> and <see cref="StreamAsync"/>) per
/// ADR 0092 §A3 — same code path as not-found, no diagnostic leak.
/// </para>
///
/// <para>
/// <b>Audit emission on cross-tenant probes (ADR 0092 §A6).</b> When
/// <see cref="GetByIdAsync"/> finds an <c>AuditRecord</c> whose
/// <c>TenantId</c> does not equal the caller's <c>tenantId</c>, the
/// implementation SHALL emit
/// <c>AuditEventType.TenantBoundaryViolation</c> before returning null.
/// Emission goes through the WRITE-side <see cref="IAuditTrail"/> — the
/// reader does not append to itself, avoiding recursion. The canonical
/// 5-field payload (<c>entity_type</c>, <c>entity_id</c>,
/// <c>requested_tenant</c>, <c>actual_tenant</c>, <c>correlation_id</c>)
/// is constructed inline per the net-architect verdict on the Bridge audit
/// emitter PR (class-private helper pattern). <see cref="ListAsync"/> +
/// <see cref="StreamAsync"/> filter by tenant at the query boundary and
/// do NOT emit per-result; ADR 0092 §A6 explicitly carves out
/// list-time per-row emission as out-of-scope for the substrate norm.
/// </para>
///
/// <para>
/// <b>Not an <c>ITenantScopedRepository&lt;AuditRecord, Guid&gt;</c>.</b>
/// The marker interface (ADR 0092) is for block-cluster CRUD repositories
/// (Invoice, Bill, Payment, Lease, etc.). <see cref="IAuditEventReader"/>
/// is a kernel-tier read-side primitive that INHERITS the substrate's
/// tenant-scoping discipline (EXPLICIT parameter, uniform-empty,
/// audit-emission) without claiming the marker itself. The Step 4a/4b/4c
/// analyzers do not currently scan kernel-audit; if a future analyzer
/// extension covers kernel-tier reads, this interface joins the surveyed
/// set. Per ADR 0094 driver: kernel-tier reads are marker-free pending
/// Q3 resolution (ADR 0094 Open Question 3).
/// </para>
/// </remarks>
public interface IAuditEventReader
{
    /// <summary>
    /// Fetch a single audit record by <paramref name="auditId"/>, scoped to
    /// <paramref name="tenantId"/>. Returns null if the record does not exist
    /// OR belongs to another tenant (uniform-empty per ADR 0092 §A3 — no
    /// diagnostic leak distinguishing "not found" from "belongs to other
    /// tenant"). Implementations SHALL emit
    /// <c>AuditEventType.TenantBoundaryViolation</c> on the cross-tenant path
    /// before returning null (audit emission via the write-side
    /// <see cref="IAuditTrail"/>; see interface remarks).
    /// </summary>
    /// <param name="tenantId">
    /// The calling tenant (server-derived from
    /// <c>Harborline.Api.Foundation.MultiTenancy.ITenantContext</c> per ADR 0091).
    /// </param>
    /// <param name="auditId">The record's stable identifier.</param>
    /// <param name="admittedAt">The instant admitted by the read act; reused if the probe is refused.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AuditRecord?> GetByIdAsync(
        TenantId tenantId,
        Guid auditId,
        DateTimeOffset admittedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieve a bounded page of audit records for <paramref name="tenantId"/>
    /// matching <paramref name="query"/>, in reverse-chronological order
    /// (OccurredAt DESC, AuditId DESC). Returns an empty page (no records, no
    /// cursor) when the query matches nothing or when the cursor belongs to a
    /// different tenant than <paramref name="tenantId"/> (uniform-empty per
    /// ADR 0092 §A3). Does NOT emit <c>TenantBoundaryViolation</c> per-row;
    /// tenant filtering happens at the query boundary (ADR 0092 §A6).
    /// </summary>
    /// <param name="tenantId">The calling tenant.</param>
    /// <param name="query">Filter + pagination parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AuditEventPage> ListAsync(
        TenantId tenantId,
        AuditEventReaderQuery query,
        CancellationToken ct = default);

    /// <summary>
    /// Stream all matching audit records for <paramref name="tenantId"/>
    /// without pagination — suitable for CSV export and bulk-analysis
    /// consumers. The <see cref="AuditEventReaderQuery.PageSize"/> and
    /// <see cref="AuditEventReaderQuery.Cursor"/> fields are ignored (streaming
    /// bypasses pagination per ADR 0094 §"Performance posture"). Records are
    /// yielded in reverse-chronological order. Does NOT emit
    /// <c>TenantBoundaryViolation</c> per-row (ADR 0092 §A6). Callers are
    /// responsible for enforcing hard-record-count caps (e.g., the 10M-row
    /// limit in the cohort-4 CSV-export endpoint).
    /// </summary>
    /// <param name="tenantId">The calling tenant.</param>
    /// <param name="query">Filter parameters (PageSize and Cursor are ignored).</param>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<AuditRecord> StreamAsync(
        TenantId tenantId,
        AuditEventReaderQuery query,
        CancellationToken ct = default);
}

/// <summary>
/// Read-side query shape for <see cref="IAuditEventReader"/>. Distinct from
/// <see cref="AuditQuery"/> (the kernel-internal stream filter for
/// <see cref="IAuditTrail.QueryAsync"/>) because the read-side surface
/// targets UI / Bridge call shapes (pagination, cursor, correlation-id
/// lookup) rather than the kernel-internal compliance-projection stream
/// pattern.
/// </summary>
/// <remarks>
/// <para>
/// Defined alongside <see cref="IAuditEventReader"/> in the same file for
/// structural locality (all four types introduced by ADR 0094 are in the
/// <c>Harborline.Api.Kernel.Audit</c> namespace). See ADR 0094, ADR 0091 (tenant
/// context), ADR 0092 (tenant-keyed repository contract), and ADR 0049
/// (audit-trail substrate write side) for the full substrate context.
/// </para>
/// </remarks>
/// <param name="EventType">
/// Optional. Match a single event type (e.g.,
/// <c>AuditEventType.TenantBoundaryViolation</c>). Combine multiple queries
/// to OR across types.
/// </param>
/// <param name="From">
/// Optional. Inclusive lower bound on <see cref="AuditRecord.OccurredAt"/>.
/// </param>
/// <param name="To">
/// Optional. Inclusive upper bound on <see cref="AuditRecord.OccurredAt"/>.
/// </param>
/// <param name="CorrelationId">
/// Optional. Match records whose payload carries this correlation-id
/// (drill-down from a downstream entity to its originating audit events).
/// </param>
/// <param name="PageSize">
/// Page size for <see cref="IAuditEventReader.ListAsync"/>. Capped at 200;
/// defaults to 50 if omitted. Ignored by
/// <see cref="IAuditEventReader.StreamAsync"/>.
/// </param>
/// <param name="Cursor">
/// Opaque continuation token from a prior
/// <see cref="IAuditEventReader.ListAsync"/> response's
/// <see cref="AuditEventPage.NextCursor"/>. Tenant-bound — see
/// <see cref="AuditEventCursor"/>.
/// </param>
public sealed record AuditEventReaderQuery(
    AuditEventType? EventType = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? CorrelationId = null,
    int PageSize = 50,
    AuditEventCursor? Cursor = null);

/// <summary>
/// A single page of audit records returned by
/// <see cref="IAuditEventReader.ListAsync"/>.
/// </summary>
/// <remarks>
/// Per ADR 0094, ADR 0091 (tenant context), ADR 0092 (tenant-keyed
/// repository contract), and ADR 0049 (audit-trail substrate write side).
/// </remarks>
/// <param name="Records">
/// The records in this page (reverse-chronological order). Empty when no
/// records match.
/// </param>
/// <param name="NextCursor">
/// Opaque continuation token; pass back to
/// <see cref="IAuditEventReader.ListAsync"/> via
/// <see cref="AuditEventReaderQuery.Cursor"/> to fetch the next page. Null
/// when there are no more pages.
/// </param>
/// <param name="HasMore">
/// True when <paramref name="NextCursor"/> is non-null (convenience for UI
/// consumers that prefer a boolean over a null check).
/// </param>
public sealed record AuditEventPage(
    IReadOnlyList<AuditRecord> Records,
    AuditEventCursor? NextCursor,
    bool HasMore);

/// <summary>
/// Opaque pagination continuation token. Carries the (OccurredAt, AuditId)
/// point to resume after, plus a tenant-id signature so the implementation
/// can reject cross-tenant cursor reuse per cohort-4 hand-off Adversarial
/// Brief Decision 2 and Decision 5.
/// </summary>
/// <remarks>
/// <para>
/// Per ADR 0094, ADR 0091 (tenant context), ADR 0092 (tenant-keyed
/// repository contract), and ADR 0049 (audit-trail substrate write side).
/// </para>
///
/// <para>
/// <b>Wire format.</b> The Bridge layer (cohort-4 Engineer PR 0) is
/// responsible for base64-encoding + signing the cursor for HTTP transport;
/// the substrate primitive carries the structured value-object. Bridge
/// serialization signs the tuple via <c>IOperationSigner</c> per the
/// hand-off's Decision 2 + Decision 5 mitigations. The substrate
/// itself does NOT mandate the wire format — only the structural shape.
/// </para>
///
/// <para>
/// <b>Tenant binding (Decision 5 — mid-page tenant-switch).</b> Decoded
/// cursors arriving from a different tenant than the caller's current
/// <see cref="TenantId"/> SHALL be rejected at the Bridge handler (400
/// "tenant_changed_reload_page" per the hand-off §4.2). The substrate
/// implementation rejects via uniform-empty on the resulting list call
/// (cross-tenant probe path); the Bridge handler's signature-check fires
/// first.
/// </para>
///
/// <para>
/// <b>Tuple-compare walking predicate (cursor advancement).</b> The cursor
/// compares as a tuple <c>(OccurredAt, AuditId)</c> strictly descending.
/// Given a cursor <c>C = (C.OccurredAt, C.AuditId)</c>, the
/// <see cref="IAuditEventReader.ListAsync"/> implementation MUST include a
/// record <c>R = (R.OccurredAt, R.AuditId)</c> in the next page iff:
/// <code>
/// R.OccurredAt &lt; C.OccurredAt
///     OR (R.OccurredAt == C.OccurredAt AND R.AuditId &lt; C.AuditId)
/// </code>
/// The <see cref="AuditId"/> tie-breaker is load-bearing: when multiple
/// audit events share a sub-millisecond <see cref="OccurredAt"/> (entirely
/// possible under burst-write workloads), comparing on
/// <see cref="OccurredAt"/> alone would silently drop records sharing the
/// cursor's timestamp. The tie-breaker on <see cref="AuditId"/> (a
/// <see cref="Guid"/> — total order under byte-lex compare) ensures every
/// record is walked exactly once across page boundaries. Reference
/// implementations SHALL implement this predicate; the test case
/// <c>ListAsync_TieOccurredAt_CursorWalksBoth</c> asserts the behavior
/// under shared-timestamp conditions (per ADR 0094 Amendment 2.3).
/// </para>
/// </remarks>
/// <param name="OccurredAt">
/// Resume-after point on <see cref="AuditRecord.OccurredAt"/> (records
/// strictly older are included; records sharing this timestamp are included
/// iff their <see cref="AuditId"/> sorts strictly less than
/// <paramref name="AuditId"/>).
/// </param>
/// <param name="AuditId">
/// Tie-breaker on <see cref="AuditRecord.AuditId"/> for records sharing
/// <see cref="OccurredAt"/>. Compared as a <see cref="Guid"/> total order;
/// records with <c>R.AuditId &lt; cursor.AuditId</c> are included when
/// timestamps match.
/// </param>
/// <param name="TenantId">
/// The tenant this cursor was issued to. Bridge signature verification
/// rejects cross-tenant reuse before the cursor reaches the substrate.
/// </param>
public sealed record AuditEventCursor(
    DateTimeOffset OccurredAt,
    Guid AuditId,
    TenantId TenantId);
