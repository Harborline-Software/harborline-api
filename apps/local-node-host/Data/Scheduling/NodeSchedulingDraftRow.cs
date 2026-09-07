namespace Harborline.Api.LocalNodeHost.Data.Scheduling;

/// <summary>One immutable revision of a tenant-owned scheduling definition draft.</summary>
public sealed class NodeSchedulingDraftRow
{
    public required string TenantId { get; set; }
    public required string DefinitionId { get; set; }
    public int Revision { get; set; }
    public required string DefinitionJson { get; set; }
    public required string UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
