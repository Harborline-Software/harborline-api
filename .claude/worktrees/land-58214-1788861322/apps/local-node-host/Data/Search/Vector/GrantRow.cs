namespace Harborline.Api.LocalNodeHost.Data.Search.Vector;

public sealed class GrantRow
{
    public string GrantId { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string SubjectId { get; set; } = "";
    public string RoleVocabulary { get; set; } = "";
    public string RoleName { get; set; } = "";
    public int ScopeType { get; set; }
    public string ScopeValue { get; set; } = "/";
    public int Residency { get; set; }
    public long ValidityFromUnixMs { get; set; }
    public long? ValidityUntilUnixMs { get; set; }
    public int Status { get; set; }
    public int GranterKind { get; set; }
    public string GrantedBy { get; set; } = "";
    public long GrantedAtUnixMs { get; set; }
    public int Source { get; set; }
    public string ReasonCode { get; set; } = "manual";
    public string? ReasonReference { get; set; }
    public string Approver { get; set; } = "";
    public long LastReviewedAtUnixMs { get; set; }
    public string? LastReviewedBy { get; set; }
    public string? ValidityChangedBy { get; set; }
    public string? ValidityChangeReasonCode { get; set; }
    public string? ValidityChangeReasonReference { get; set; }
    public string? RevokedBy { get; set; }
    public long? RevokedAtUnixMs { get; set; }
    public string? RevocationReasonCode { get; set; }
    public string? RevocationReasonReference { get; set; }
    public string? SourceReference { get; set; }
    public long OwnerVersion { get; set; } = 1;
}
