namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>The installation-wide account lifecycle; tenant access is owned by memberships elsewhere.</summary>
public enum InstallationAccountStatus
{
    Active,
    Disabled,
}

/// <summary>The lifecycle of a root-key binding for one stable installation identity.</summary>
public enum InstallationRootEpochStatus
{
    Pending,
    Active,
    Retired,
    Recovered,
}

/// <summary>The lifecycle of the narrow installation-administration grant.</summary>
public enum InstallationAccessGrantStatus
{
    Active,
    Revoked,
}

/// <summary>Durable R3-H decision state owned by the installation authority store.</summary>
public enum InstallationIdentityCoordinatorState
{
    Preparing,
    Committing,
    Finalizing,
    Completed,
    Aborted,
}

/// <summary>The purpose fence for hosted-web setup invitations.</summary>
public enum WebSetupInvitationPurpose
{
    AccountSetup,
    MembershipSetup,
}

/// <summary>
/// Digest-only authority to begin one account-setup ceremony. Issuance reserves no username,
/// account, membership, Party, trust, or roster fact.
/// </summary>
public sealed class AccountSetupInvitationRecord
{
    public required string InvitationId { get; set; }

    public required string TenantId { get; set; }

    public required string InviterAccountId { get; set; }

    public required string InviterPrincipalId { get; set; }

    public required string InviterPartyId { get; set; }

    public required string InviterSessionCorrelationId { get; set; }

    public required string InviterMembershipId { get; set; }

    public long InviterMembershipOwnerVersion { get; set; }

    public required string InviterGrantId { get; set; }

    public long InviterGrantOwnerVersion { get; set; }

    public long InviterAuthorizationEpoch { get; set; }

    public required string RequestedPermissionsJson { get; set; }

    public required string TokenDigest { get; set; }

    public WebSetupInvitationPurpose Purpose { get; set; }

    public required string CommandFingerprint { get; set; }

    public DateTimeOffset IssuedAtUtc { get; set; }

    public DateTimeOffset AbsoluteExpiresAtUtc { get; set; }

    public DateTimeOffset? ConsumedAtUtc { get; set; }

    public DateTimeOffset? RevokedAtUtc { get; set; }

    public long OwnerVersion { get; set; }
}

/// <summary>
/// One human account in the installation namespace. It deliberately contains no tenant, Party,
/// business principal, role, grant, or permission authority.
/// </summary>
public sealed class InstallationAccountRecord
{
    public required string AccountId { get; set; }

    public required string NormalizedUsername { get; set; }

    public required string CredentialHash { get; set; }

    public required string CredentialAlgorithm { get; set; }

    public required string CredentialCeremonyId { get; set; }

    public int CredentialVersion { get; set; }

    public InstallationAccountStatus Status { get; set; }

    public long SecurityVersion { get; set; }

    public long OwnerVersion { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>Stable, random installation identity stored inside the encrypted authority store.</summary>
public sealed class InstallationIdentityRecord
{
    public const string SingletonKeyValue = "installation";
    public const long Revision3AuthorityVersion = 2;

    public required string SingletonKey { get; set; }

    public required string InstallationIdentityId { get; set; }

    public long ActiveRootEpoch { get; set; }

    public long AuthorityVersion { get; set; }

    public long OwnerVersion { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>
/// Monotonic evidence that installation bootstrap was claimed. This row is never removed or reset,
/// even if the ordinary grant it names is later revoked.
/// </summary>
public sealed class BootstrapClaimMarkerRecord
{
    public const string SingletonKeyValue = "bootstrap-claim";

    public required string SingletonKey { get; set; }
    public required string InstallationId { get; set; }
    public required string GrantId { get; set; }
    public BootstrapClaimIssuerKind IssuerKind { get; set; }
    public required string IssuerIdentity { get; set; }
    public required string NonceDigest { get; set; }
    public DateTimeOffset ClaimedAtUtc { get; set; }
}

/// <summary>
/// The installation's one bootstrap-claim window. The first authenticated issuance fixes this
/// deadline permanently; an idempotent founder replay may reuse the remaining time but cannot
/// extend it.
/// </summary>
public sealed class BootstrapClaimWindowRecord
{
    public const string SingletonKeyValue = "bootstrap-window";

    public required string SingletonKey { get; set; }
    public required string InstallationId { get; set; }
    public DateTimeOffset IssuedAtUtc { get; set; }
    public DateTimeOffset DeadlineUtc { get; set; }
}

/// <summary>Versioned binding from the stable installation identity to one root public key.</summary>
public sealed class InstallationRootKeyEpochRecord
{
    public required string InstallationIdentityId { get; set; }

    public long EpochNumber { get; set; }

    public required string RootPublicKeyFingerprint { get; set; }

    public InstallationRootEpochStatus Status { get; set; }

    public required string TransitionCorrelationId { get; set; }

    public string? PreviousRootFingerprint { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? RetiredAtUtc { get; set; }
}

/// <summary>Narrow install-level permissions owned independently of every tenant grant.</summary>
public sealed class InstallationAccessGrantRecord
{
    public required string GrantId { get; set; }

    public required string AccountId { get; set; }

    public required string PermissionsJson { get; set; }

    public InstallationAccessGrantStatus Status { get; set; }

    public required string IssuerKind { get; set; }

    public required string IssuerId { get; set; }

    public long AuthorizationEpoch { get; set; }

    public long OwnerVersion { get; set; }

    public required string AuditCorrelationId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>Hash-chain head for installation-owned identity evidence.</summary>
public sealed class InstallationAuditHeadRecord
{
    public required string InstallationIdentityId { get; set; }

    public long Sequence { get; set; }

    public required string HeadHash { get; set; }

    public long OwnerVersion { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>One classified-safe installation audit envelope.</summary>
public sealed class InstallationAuditEnvelopeRecord
{
    public required string InstallationIdentityId { get; set; }

    public long Sequence { get; set; }

    public required string CorrelationId { get; set; }

    public required string CommandFingerprint { get; set; }

    public required string EventType { get; set; }

    public required string ActorKind { get; set; }

    public required string ActorId { get; set; }

    public long RootEpoch { get; set; }

    public required string RootPublicKeyFingerprint { get; set; }

    public required string PreviousHash { get; set; }

    public required string EnvelopeHash { get; set; }

    public required string PayloadDigest { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }
}

/// <summary>
/// Installation-home record for one cross-store identity command. Tenant authority remains in the
/// tenant stores; this row owns only the durable decision, replay evidence, and recovery cursor.
/// </summary>
public sealed class InstallationIdentityCoordinatorRecord
{
    public required string CorrelationId { get; set; }

    public required string CommandType { get; set; }

    public required string CommandFingerprint { get; set; }

    public int PayloadSchemaVersion { get; set; }

    public required string AccountId { get; set; }

    public required string ActorAccountId { get; set; }

    public required string AuthorityEvidenceDigest { get; set; }

    public long ExpectedAccountOwnerVersion { get; set; }

    public long ExpectedAccountSecurityVersion { get; set; }

    public long ExpectedActorOwnerVersion { get; set; }

    public long ExpectedActorSecurityVersion { get; set; }

    public required string TenantIdsJson { get; set; }

    public required string IntentPayloadJson { get; set; }

    public required string FinalReceiptsJson { get; set; }

    public InstallationIdentityCoordinatorState State { get; set; }

    public string? FailureCode { get; set; }

    public long OwnerVersion { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>Durable phases for the dormant v1-to-v2 installation-authority cutover.</summary>
public enum InstallationIdentityCutoverStage
{
    LegacyV1Authoritative,
    WriteBarrierActive,
    Copying,
    Verified,
    V2Authoritative,
}

/// <summary>Lifecycle of a classified-safe install-scope collision preview.</summary>
public enum InstallationIdentityCollisionStatus
{
    Unresolved,
    Renamed,
}

/// <summary>
/// Singleton lease shape for one restart-safe installation migration. The cutover orchestrator
/// acquires and reclaims this row by holder fingerprint plus monotonically increasing generation.
/// </summary>
public sealed class InstallationIdentityMigrationLeaseRecord
{
    public const string SingletonKeyValue = "installation-identity-migration";

    public required string SingletonKey { get; set; }

    public required string LeaseId { get; set; }

    public required string HolderFingerprint { get; set; }

    public long LeaseGeneration { get; set; }

    public long OwnerVersion { get; set; }

    public DateTimeOffset AcquiredAtUtc { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }

    public DateTimeOffset? ReleasedAtUtc { get; set; }
}

/// <summary>Repeatable version/watermark evidence for one classified legacy source partition.</summary>
public sealed class InstallationIdentityMigrationSourceWatermarkRecord
{
    public required string SourceKind { get; set; }

    public required string SourcePartition { get; set; }

    public required string MigrationRunId { get; set; }

    public long SourceVersion { get; set; }

    public required string HighWatermark { get; set; }

    public required string SnapshotDigest { get; set; }

    public long OwnerVersion { get; set; }

    public DateTimeOffset CapturedAtUtc { get; set; }
}

/// <summary>
/// Classified-safe collision evidence. It stores a digest and count, never a username or account
/// payload, and cannot itself merge or link identities.
/// </summary>
public sealed class InstallationIdentityMigrationCollisionRecord
{
    public required string CollisionId { get; set; }

    public required string CollisionKeyDigest { get; set; }

    public int CandidateCount { get; set; }

    public long ExpectedSourceVersion { get; set; }

    public InstallationIdentityCollisionStatus Status { get; set; }

    public string? RepairIdempotencyKeyDigest { get; set; }

    public string? AuditCorrelationId { get; set; }

    public string? RepairCommandFingerprint { get; set; }

    public string? RepairSourceKeyDigest { get; set; }

    public string? RepairTargetDigest { get; set; }

    public long? ExpectedOwnerVersion { get; set; }

    public string? RecoveryActorId { get; set; }

    public long? RecoveryRootEpoch { get; set; }

    public string? RecoveryRootPublicKeyFingerprint { get; set; }

    public long OwnerVersion { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>Audit-provenance tombstone for a legacy projection that must not fabricate v2 authority.</summary>
public sealed class InstallationIdentityMigrationTombstoneRecord
{
    public required string TombstoneId { get; set; }

    public required string SourceKind { get; set; }

    public required string SourceKeyDigest { get; set; }

    public long SourceVersion { get; set; }

    public required string ReasonCode { get; set; }

    public required string AuditCorrelationId { get; set; }

    public required string ProjectionDigest { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>Owner-versioned and idempotent initial installation-root designation evidence.</summary>
public sealed class InstallationIdentityRootDesignationRecord
{
    public const string SingletonKeyValue = "initial-installation-root";

    public required string SingletonKey { get; set; }

    public required string DesignationId { get; set; }

    public required string SourceCompositeKeyDigest { get; set; }

    public string? AccountId { get; set; }

    public long ExpectedSourceVersion { get; set; }

    public required string IdempotencyKeyDigest { get; set; }

    public required string AuditCorrelationId { get; set; }

    public long OwnerVersion { get; set; }

    public DateTimeOffset DesignatedAtUtc { get; set; }

    public DateTimeOffset? VerifiedAtUtc { get; set; }
}

/// <summary>
/// Singleton cutover state. The migration seeds v1 authority; the cutover orchestrator is the sole
/// writer allowed to advance this marker and never provides a downgrade path.
/// </summary>
public sealed class InstallationIdentityCutoverStateRecord
{
    public const string SingletonKeyValue = "installation-identity-authority";
    public const long LegacyV1AuthorityVersion = 1;
    public const long Revision3V2AuthorityVersion = 2;

    public required string SingletonKey { get; set; }

    public InstallationIdentityCutoverStage Stage { get; set; }

    public long AuthorityVersion { get; set; }

    public string? MigrationRunId { get; set; }

    public long V1WriteBarrierVersion { get; set; }

    public string? SourceWatermarkDigest { get; set; }

    public string? FinalVerificationDigest { get; set; }

    public string? LegacyBearerRevocationDigest { get; set; }

    public long OwnerVersion { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset? CommittedAtUtc { get; set; }
}
