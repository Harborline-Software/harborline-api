namespace Harborline.Api.LocalNodeHost.Data.Audit;

/// <summary>
/// Read-side projection of a node audit row, carrying the exact wire shape <c>audit-events.ts</c>
/// consumes plus the offline-computed <c>signature_state</c> (ADR 0126 §D3/§D4). Returned by
/// <see cref="NodeAuditEventReader"/>.
/// </summary>
/// <param name="AuditId">Stable identifier (GUID string).</param>
/// <param name="OccurredAt">ISO-8601 timestamp.</param>
/// <param name="EventType">The audit event-type string.</param>
/// <param name="Actor">The acting principal (node operator), or null.</param>
/// <param name="CorrelationId">Correlation id from the payload, or null.</param>
/// <param name="TenantId">The tenant the record is scoped to.</param>
/// <param name="PayloadSummary">The payload body (JSON-deserialised to a dictionary).</param>
/// <param name="SignatureState">Offline integrity/signature verdict: Verified | VerificationFailed | NotSigned.</param>
public sealed record NodeAuditEventView(
    string AuditId,
    string OccurredAt,
    string EventType,
    string? Actor,
    string? CorrelationId,
    string TenantId,
    IReadOnlyDictionary<string, object?> PayloadSummary,
    string SignatureState);

/// <summary>A page of <see cref="NodeAuditEventView"/> with an opaque continuation cursor.</summary>
/// <param name="Events">The records in this page (reverse-chronological).</param>
/// <param name="NextCursor">Opaque continuation token; null when no more pages.</param>
/// <param name="HasMore">True when <paramref name="NextCursor"/> is non-null.</param>
public sealed record NodeAuditEventPage(
    IReadOnlyList<NodeAuditEventView> Events,
    string? NextCursor,
    bool HasMore);

/// <summary>
/// Filter + pagination parameters for <see cref="NodeAuditEventReader.ListAsync"/>. Mirrors the Bridge
/// <c>AuditEventReaderQuery</c> fields the frontend sends.
/// </summary>
/// <param name="EventType">Optional exact event-type match.</param>
/// <param name="From">Optional inclusive lower bound on OccurredAt.</param>
/// <param name="To">Optional inclusive upper bound on OccurredAt.</param>
/// <param name="CorrelationId">Optional correlation-id match.</param>
/// <param name="PageSize">Page size (clamped 1..200).</param>
/// <param name="Cursor">Opaque continuation cursor from a prior page.</param>
public sealed record NodeAuditEventReaderQuery(
    string? EventType = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? CorrelationId = null,
    int PageSize = 50,
    NodeAuditCursor? Cursor = null);

/// <summary>
/// Opaque pagination continuation point: the (OccurredAt, AuditId) tuple to resume strictly after,
/// plus the tenant the cursor was issued to (mid-page tenant-switch rejection — preserved from the
/// Bridge cursor discipline, though structurally moot on a single-device node).
/// </summary>
/// <param name="OccurredAt">Resume-after point on OccurredAt.</param>
/// <param name="AuditId">Tie-breaker on AuditId for records sharing OccurredAt.</param>
/// <param name="TenantId">The tenant this cursor was issued to.</param>
public sealed record NodeAuditCursor(
    DateTimeOffset OccurredAt,
    string AuditId,
    string TenantId);
