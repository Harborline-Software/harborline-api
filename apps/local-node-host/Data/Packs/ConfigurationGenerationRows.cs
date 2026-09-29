namespace Harborline.Api.LocalNodeHost.Data.Packs;

/// <summary>
/// The one effective configuration generation per tenant (T-644, DES-0044 governance-ck-7). The row is the
/// pointer; <see cref="ReferencesJson"/> is the canonical reference document the platform derived the digest
/// from, so the generation is reconstructed and re-hashed rather than trusted from the stored digest.
/// </summary>
internal sealed class ConfigurationEffectiveGenerationRow
{
    public required string Tenant { get; set; }
    public required string Digest { get; set; }
    public required string ReferencesJson { get; set; }
    public required string Principal { get; set; }
    public DateTimeOffset ActivatedAt { get; set; }
    public required string DecisionId { get; set; }
    public required string EvidenceIntentId { get; set; }
}

/// <summary>
/// An isolated, immutable candidate projection written by preparation. Preparation may create this artifact
/// but never moves the pointer; the switch re-reads it by key and revision and re-hashes its bytes.
/// </summary>
internal sealed class ConfigurationPreparedProjectionRow
{
    public required string Tenant { get; set; }
    public required string CandidateDigest { get; set; }
    public required string Revision { get; set; }
    public required string ProjectionDigest { get; set; }
    public required string ReferencesJson { get; set; }
    public required string BaselineDigest { get; set; }
    public required string DestinationDigest { get; set; }
    public DateTimeOffset PreparedAt { get; set; }
}

/// <summary>
/// The evidence outbox: the reconstructable evidence intent committed with the governed mutation and published
/// afterwards. A row with no <see cref="PublishedAt"/> is committed but unpublished. Bringing one left by a
/// crash to a terminal state is the kernel's (T-587); publication is the api's.
/// </summary>
internal sealed class ConfigurationEvidenceOutboxRow
{
    public required string Tenant { get; set; }
    public required string IntentId { get; set; }
    public required string Reason { get; set; }
    public required string InputsDigest { get; set; }
    public required string DecisionId { get; set; }
    public required string DecisionJson { get; set; }
    public required string PriorDigest { get; set; }
    public required string NewDigest { get; set; }
    public required string Principal { get; set; }
    public DateTimeOffset CommittedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    /// <summary>The authority the admitting decision captured at commit (DES-0029 ck-6); null on rows committed before it.</summary>
    public string? AuthoritySnapshotJson { get; set; }
    /// <summary>The last delivery failure, cleared when the row is published.</summary>
    public string? LastError { get; set; }
}

/// <summary>
/// The one record an offline configuration recovery commits (T-587, T-909 slice 6): each residue's terminal
/// state with the reason and authority snapshot behind it, committed in one transaction with its audit row.
/// </summary>
internal sealed class ConfigurationRecoveryRow
{
    public required string Tenant { get; set; }
    public required string RecoveryId { get; set; }
    public required string EffectiveDigest { get; set; }
    public required string Reason { get; set; }
    public required string AuthoritySnapshot { get; set; }
    public required string RecordJson { get; set; }
}

/// <summary>The kernel audit evidence for one configuration recovery; never written without its record.</summary>
internal sealed class ConfigurationRecoveryAuditRow
{
    public required string AuditId { get; set; }
    public required string Tenant { get; set; }
    public required string ActorId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public required string PayloadJson { get; set; }
}
