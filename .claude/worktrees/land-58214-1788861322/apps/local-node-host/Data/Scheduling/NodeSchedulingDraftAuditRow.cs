namespace Harborline.Api.LocalNodeHost.Data.Scheduling;

/// <summary>Immutable evidence for a successful scheduling draft revision write.</summary>
public sealed class NodeSchedulingDraftAuditRow
{
    public required string TenantId { get; set; }
    public required string AuditId { get; set; }
    public required string DefinitionId { get; set; }
    public int Revision { get; set; }
    public required string ActorId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}
