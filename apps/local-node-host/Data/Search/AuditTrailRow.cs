namespace Harborline.Api.LocalNodeHost.Data.Search;

/// <summary>
/// T-986: one kernel audit record in the host's durable trail in <c>local-node.db</c>. The filter columns are
/// copies of what <see cref="RecordJson"/> holds; the JSON is the record.
/// </summary>
public sealed class AuditTrailRow
{
    /// <summary>The audit id (the record's identity; one row per id).</summary>
    public required string AuditId { get; set; }

    /// <summary>The record's tenant.</summary>
    public required string TenantId { get; set; }

    /// <summary>The kernel audit event type.</summary>
    public required string EventType { get; set; }

    /// <summary>The record's instant.</summary>
    public required DateTimeOffset OccurredAt { get; set; }

    /// <summary>The record, as <c>NodeAuditRecordJson</c> writes it.</summary>
    public required string RecordJson { get; set; }
}
