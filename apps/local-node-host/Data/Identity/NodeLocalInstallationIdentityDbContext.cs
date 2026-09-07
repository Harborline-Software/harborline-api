using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>
/// Node-exclusive encrypted authority for installation accounts, root binding, narrow installation
/// grants, and installation audit evidence. Tenant memberships and tenant audit remain tenant-owned.
/// </summary>
public sealed class NodeLocalInstallationIdentityDbContext : DbContext
{
    public const string MigrationsHistoryTableName = "__InstallationIdentityMigrationsHistory";

    public NodeLocalInstallationIdentityDbContext(
        DbContextOptions<NodeLocalInstallationIdentityDbContext> options)
        : base(options)
    {
    }

    public DbSet<InstallationAccountRecord> Accounts => Set<InstallationAccountRecord>();

    public DbSet<InstallationIdentityRecord> InstallationIdentities => Set<InstallationIdentityRecord>();

    public DbSet<BootstrapClaimMarkerRecord> BootstrapClaimMarkers => Set<BootstrapClaimMarkerRecord>();

    public DbSet<BootstrapClaimWindowRecord> BootstrapClaimWindows => Set<BootstrapClaimWindowRecord>();

    public DbSet<InstallationRootKeyEpochRecord> RootKeyEpochs => Set<InstallationRootKeyEpochRecord>();

    public DbSet<InstallationAccessGrantRecord> InstallationAccessGrants => Set<InstallationAccessGrantRecord>();

    public DbSet<InstallationAuditHeadRecord> AuditHeads => Set<InstallationAuditHeadRecord>();

    public DbSet<InstallationAuditEnvelopeRecord> AuditEnvelopes => Set<InstallationAuditEnvelopeRecord>();

    public DbSet<InstallationIdentityCoordinatorRecord> Coordinators =>
        Set<InstallationIdentityCoordinatorRecord>();

    public DbSet<AccountSetupInvitationRecord> AccountSetupInvitations =>
        Set<AccountSetupInvitationRecord>();

    public DbSet<RecoveryInvitationRecord> RecoveryInvitations =>
        Set<RecoveryInvitationRecord>();

    public DbSet<InstallationIdentityMigrationLeaseRecord> MigrationLeases =>
        Set<InstallationIdentityMigrationLeaseRecord>();

    public DbSet<InstallationIdentityMigrationSourceWatermarkRecord> MigrationSourceWatermarks =>
        Set<InstallationIdentityMigrationSourceWatermarkRecord>();

    public DbSet<InstallationIdentityMigrationCollisionRecord> MigrationCollisions =>
        Set<InstallationIdentityMigrationCollisionRecord>();

    public DbSet<InstallationIdentityMigrationTombstoneRecord> MigrationTombstones =>
        Set<InstallationIdentityMigrationTombstoneRecord>();

    public DbSet<InstallationIdentityRootDesignationRecord> RootDesignations =>
        Set<InstallationIdentityRootDesignationRecord>();

    public DbSet<InstallationIdentityCutoverStateRecord> CutoverStates =>
        Set<InstallationIdentityCutoverStateRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        var instant = new ValueConverter<DateTimeOffset, long>(
            value => value.ToUnixTimeMilliseconds(),
            value => DateTimeOffset.FromUnixTimeMilliseconds(value));
        var nullableInstant = new ValueConverter<DateTimeOffset?, long?>(
            value => value.HasValue ? value.Value.ToUnixTimeMilliseconds() : null,
            value => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null);

        modelBuilder.Entity<InstallationAccountRecord>(entity =>
        {
            entity.ToTable("installation_accounts");
            entity.HasKey(row => row.AccountId);
            entity.Property(row => row.AccountId).HasColumnName("account_id").HasMaxLength(64);
            entity.Property(row => row.NormalizedUsername).HasColumnName("normalized_username").HasMaxLength(320);
            entity.Property(row => row.CredentialHash).HasColumnName("credential_hash");
            entity.Property(row => row.CredentialAlgorithm).HasColumnName("credential_algorithm").HasMaxLength(64);
            entity.Property(row => row.CredentialCeremonyId)
                .HasColumnName("credential_ceremony_id")
                .HasMaxLength(32);
            entity.Property(row => row.CredentialVersion).HasColumnName("credential_version");
            entity.Property(row => row.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            entity.Property(row => row.SecurityVersion).HasColumnName("security_version");
            entity.Property(row => row.OwnerVersion).HasColumnName("owner_version").IsConcurrencyToken();
            entity.Property(row => row.CreatedAtUtc).HasColumnName("created_at_utc").HasConversion(instant);
            entity.Property(row => row.UpdatedAtUtc).HasColumnName("updated_at_utc").HasConversion(instant);
            entity.HasIndex(row => row.NormalizedUsername)
                .IsUnique()
                .HasDatabaseName("ux_installation_accounts_normalized_username");
        });

        modelBuilder.Entity<InstallationIdentityRecord>(entity =>
        {
            entity.ToTable("installation_identity");
            entity.HasKey(row => row.SingletonKey);
            entity.Property(row => row.SingletonKey).HasColumnName("singleton_key").HasMaxLength(32);
            entity.Property(row => row.InstallationIdentityId)
                .HasColumnName("installation_identity_id")
                .HasMaxLength(64);
            entity.Property(row => row.ActiveRootEpoch).HasColumnName("active_root_epoch");
            entity.Property(row => row.AuthorityVersion).HasColumnName("authority_version");
            entity.Property(row => row.OwnerVersion).HasColumnName("owner_version").IsConcurrencyToken();
            entity.Property(row => row.CreatedAtUtc).HasColumnName("created_at_utc").HasConversion(instant);
            entity.Property(row => row.UpdatedAtUtc).HasColumnName("updated_at_utc").HasConversion(instant);
            entity.HasAlternateKey(row => row.InstallationIdentityId)
                .HasName("ak_installation_identity_id");
        });

        modelBuilder.Entity<BootstrapClaimMarkerRecord>(entity =>
        {
            entity.ToTable("bootstrap_claim_marker");
            entity.HasKey(row => row.SingletonKey);
            entity.Property(row => row.SingletonKey).HasColumnName("singleton_key").HasMaxLength(32);
            entity.Property(row => row.InstallationId).HasColumnName("installation_id").HasMaxLength(64);
            entity.Property(row => row.GrantId).HasColumnName("grant_id").HasMaxLength(64);
            entity.Property(row => row.IssuerKind)
                .HasColumnName("issuer_kind").HasConversion<string>().HasMaxLength(64);
            entity.Property(row => row.IssuerIdentity)
                .HasColumnName("issuer_identity").HasMaxLength(128);
            entity.Property(row => row.NonceDigest).HasColumnName("nonce_digest").HasMaxLength(64);
            entity.Property(row => row.ClaimedAtUtc).HasColumnName("claimed_at_utc").HasConversion(instant);
            entity.HasIndex(row => row.GrantId).IsUnique().HasDatabaseName("ux_bootstrap_claim_marker_grant");
            entity.HasIndex(row => row.NonceDigest).IsUnique().HasDatabaseName("ux_bootstrap_claim_marker_nonce");
        });

        modelBuilder.Entity<BootstrapClaimWindowRecord>(entity =>
        {
            entity.ToTable("bootstrap_claim_window");
            entity.HasKey(row => row.SingletonKey);
            entity.Property(row => row.SingletonKey).HasColumnName("singleton_key").HasMaxLength(32);
            entity.Property(row => row.InstallationId).HasColumnName("installation_id").HasMaxLength(64);
            entity.Property(row => row.IssuedAtUtc).HasColumnName("issued_at_utc").HasConversion(instant);
            entity.Property(row => row.DeadlineUtc).HasColumnName("deadline_utc").HasConversion(instant);
        });

        modelBuilder.Entity<InstallationRootKeyEpochRecord>(entity =>
        {
            entity.ToTable("installation_root_key_epochs");
            entity.HasKey(row => new { row.InstallationIdentityId, row.EpochNumber });
            entity.Property(row => row.InstallationIdentityId)
                .HasColumnName("installation_identity_id")
                .HasMaxLength(64);
            entity.Property(row => row.EpochNumber).HasColumnName("epoch_number");
            entity.Property(row => row.RootPublicKeyFingerprint)
                .HasColumnName("root_public_key_fingerprint")
                .HasMaxLength(128);
            entity.Property(row => row.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            entity.Property(row => row.TransitionCorrelationId)
                .HasColumnName("transition_correlation_id")
                .HasMaxLength(128);
            entity.Property(row => row.PreviousRootFingerprint)
                .HasColumnName("previous_root_fingerprint")
                .HasMaxLength(128);
            entity.Property(row => row.CreatedAtUtc).HasColumnName("created_at_utc").HasConversion(instant);
            entity.Property(row => row.RetiredAtUtc).HasColumnName("retired_at_utc").HasConversion(nullableInstant);
            entity.HasIndex(row => row.RootPublicKeyFingerprint)
                .IsUnique()
                .HasDatabaseName("ux_installation_root_key_epochs_fingerprint");
            entity.HasIndex(row => row.TransitionCorrelationId)
                .IsUnique()
                .HasDatabaseName("ux_installation_root_key_epochs_correlation");
            entity.HasIndex(row => row.Status)
                .IsUnique()
                .HasFilter("status = 'Active'")
                .HasDatabaseName("ux_installation_root_key_epochs_one_active");
            entity.HasOne<InstallationIdentityRecord>()
                .WithMany()
                .HasForeignKey(row => row.InstallationIdentityId)
                .HasPrincipalKey(row => row.InstallationIdentityId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InstallationAccessGrantRecord>(entity =>
        {
            entity.ToTable("installation_access_grants");
            entity.HasKey(row => row.AccountId);
            entity.Property(row => row.GrantId).HasColumnName("grant_id").HasMaxLength(64);
            entity.Property(row => row.AccountId).HasColumnName("account_id").HasMaxLength(64);
            entity.Property(row => row.PermissionsJson).HasColumnName("permissions_json");
            entity.Property(row => row.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            entity.Property(row => row.IssuerKind).HasColumnName("issuer_kind").HasMaxLength(64);
            entity.Property(row => row.IssuerId).HasColumnName("issuer_id").HasMaxLength(128);
            entity.Property(row => row.AuthorizationEpoch).HasColumnName("authorization_epoch");
            entity.Property(row => row.OwnerVersion).HasColumnName("owner_version").IsConcurrencyToken();
            entity.Property(row => row.AuditCorrelationId)
                .HasColumnName("audit_correlation_id")
                .HasMaxLength(128);
            entity.Property(row => row.CreatedAtUtc).HasColumnName("created_at_utc").HasConversion(instant);
            entity.Property(row => row.UpdatedAtUtc).HasColumnName("updated_at_utc").HasConversion(instant);
            entity.HasAlternateKey(row => row.GrantId).HasName("ak_installation_access_grants_grant_id");
            entity.HasIndex(row => row.AuditCorrelationId)
                .IsUnique()
                .HasDatabaseName("ux_installation_access_grants_audit_correlation");
            entity.HasOne<InstallationAccountRecord>()
                .WithOne()
                .HasForeignKey<InstallationAccessGrantRecord>(row => row.AccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InstallationAuditHeadRecord>(entity =>
        {
            entity.ToTable("installation_audit_heads");
            entity.HasKey(row => row.InstallationIdentityId);
            entity.Property(row => row.InstallationIdentityId)
                .HasColumnName("installation_identity_id")
                .HasMaxLength(64);
            entity.Property(row => row.Sequence).HasColumnName("sequence");
            entity.Property(row => row.HeadHash).HasColumnName("head_hash").HasMaxLength(128);
            entity.Property(row => row.OwnerVersion).HasColumnName("owner_version").IsConcurrencyToken();
            entity.Property(row => row.UpdatedAtUtc).HasColumnName("updated_at_utc").HasConversion(instant);
            entity.HasOne<InstallationIdentityRecord>()
                .WithOne()
                .HasForeignKey<InstallationAuditHeadRecord>(row => row.InstallationIdentityId)
                .HasPrincipalKey<InstallationIdentityRecord>(row => row.InstallationIdentityId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InstallationAuditEnvelopeRecord>(entity =>
        {
            entity.ToTable("installation_audit_envelopes");
            entity.HasKey(row => new { row.InstallationIdentityId, row.Sequence });
            entity.Property(row => row.InstallationIdentityId)
                .HasColumnName("installation_identity_id")
                .HasMaxLength(64);
            entity.Property(row => row.Sequence).HasColumnName("sequence");
            entity.Property(row => row.CorrelationId).HasColumnName("correlation_id").HasMaxLength(128);
            entity.Property(row => row.CommandFingerprint)
                .HasColumnName("command_fingerprint")
                .HasMaxLength(128);
            entity.Property(row => row.EventType).HasColumnName("event_type").HasMaxLength(128);
            entity.Property(row => row.ActorKind).HasColumnName("actor_kind").HasMaxLength(64);
            entity.Property(row => row.ActorId).HasColumnName("actor_id").HasMaxLength(128);
            entity.Property(row => row.RootEpoch).HasColumnName("root_epoch");
            entity.Property(row => row.RootPublicKeyFingerprint)
                .HasColumnName("root_public_key_fingerprint")
                .HasMaxLength(128);
            entity.Property(row => row.PreviousHash).HasColumnName("previous_hash").HasMaxLength(128);
            entity.Property(row => row.EnvelopeHash).HasColumnName("envelope_hash").HasMaxLength(128);
            entity.Property(row => row.PayloadDigest).HasColumnName("payload_digest").HasMaxLength(128);
            entity.Property(row => row.OccurredAtUtc).HasColumnName("occurred_at_utc").HasConversion(instant);
            entity.HasIndex(row => row.CorrelationId)
                .IsUnique()
                .HasDatabaseName("ux_installation_audit_envelopes_correlation");
            entity.HasOne<InstallationIdentityRecord>()
                .WithMany()
                .HasForeignKey(row => row.InstallationIdentityId)
                .HasPrincipalKey(row => row.InstallationIdentityId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InstallationIdentityCoordinatorRecord>(entity =>
        {
            entity.ToTable("installation_identity_coordinators");
            entity.HasKey(row => row.CorrelationId);
            entity.Property(row => row.CorrelationId).HasColumnName("correlation_id").HasMaxLength(128);
            entity.Property(row => row.CommandType).HasColumnName("command_type").HasMaxLength(128);
            entity.Property(row => row.CommandFingerprint)
                .HasColumnName("command_fingerprint")
                .HasMaxLength(128);
            entity.Property(row => row.PayloadSchemaVersion).HasColumnName("payload_schema_version");
            entity.Property(row => row.AccountId).HasColumnName("account_id").HasMaxLength(64);
            entity.Property(row => row.ActorAccountId).HasColumnName("actor_account_id").HasMaxLength(64);
            entity.Property(row => row.AuthorityEvidenceDigest)
                .HasColumnName("authority_evidence_digest").HasMaxLength(64);
            entity.Property(row => row.ExpectedAccountOwnerVersion)
                .HasColumnName("expected_account_owner_version");
            entity.Property(row => row.ExpectedAccountSecurityVersion)
                .HasColumnName("expected_account_security_version");
            entity.Property(row => row.ExpectedActorOwnerVersion)
                .HasColumnName("expected_actor_owner_version");
            entity.Property(row => row.ExpectedActorSecurityVersion)
                .HasColumnName("expected_actor_security_version");
            entity.Property(row => row.TenantIdsJson).HasColumnName("tenant_ids_json");
            entity.Property(row => row.IntentPayloadJson).HasColumnName("intent_payload_json");
            entity.Property(row => row.FinalReceiptsJson).HasColumnName("final_receipts_json");
            entity.Property(row => row.State).HasColumnName("state").HasConversion<string>().HasMaxLength(32);
            entity.Property(row => row.FailureCode).HasColumnName("failure_code").HasMaxLength(128);
            entity.Property(row => row.OwnerVersion).HasColumnName("owner_version").IsConcurrencyToken();
            entity.Property(row => row.CreatedAtUtc).HasColumnName("created_at_utc").HasConversion(instant);
            entity.Property(row => row.UpdatedAtUtc).HasColumnName("updated_at_utc").HasConversion(instant);
            entity.HasIndex(row => new { row.AccountId, row.State })
                .HasDatabaseName("ix_installation_identity_coordinators_account_state");
            entity.HasOne<InstallationAccountRecord>()
                .WithMany()
                .HasForeignKey(row => row.AccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AccountSetupInvitationRecord>(entity =>
        {
            entity.ToTable("account_setup_invitations", table =>
            {
                table.HasCheckConstraint(
                    "ck_account_setup_invitation_owner_version",
                    "owner_version > 0");
                table.HasCheckConstraint(
                    "ck_account_setup_invitation_expiry",
                    "absolute_expires_at_utc > issued_at_utc");
                table.HasCheckConstraint(
                    "ck_account_setup_invitation_digest",
                    "length(token_digest) = 64 AND token_digest NOT GLOB '*[^0-9A-F]*'");
                table.HasCheckConstraint(
                    "ck_account_setup_invitation_purpose",
                    "purpose = 'AccountSetup'");
            });
            entity.HasKey(row => row.InvitationId);
            entity.Property(row => row.InvitationId).HasColumnName("invitation_id").HasMaxLength(64);
            entity.Property(row => row.TenantId).HasColumnName("tenant_id").HasMaxLength(64);
            entity.Property(row => row.InviterAccountId)
                .HasColumnName("inviter_account_id").HasMaxLength(64);
            entity.Property(row => row.InviterPrincipalId)
                .HasColumnName("inviter_principal_id").HasMaxLength(256);
            entity.Property(row => row.InviterPartyId)
                .HasColumnName("inviter_party_id").HasMaxLength(256);
            entity.Property(row => row.InviterSessionCorrelationId)
                .HasColumnName("inviter_session_correlation_id").HasMaxLength(128);
            entity.Property(row => row.InviterMembershipId)
                .HasColumnName("inviter_membership_id").HasMaxLength(64);
            entity.Property(row => row.InviterMembershipOwnerVersion)
                .HasColumnName("inviter_membership_owner_version");
            entity.Property(row => row.InviterGrantId)
                .HasColumnName("inviter_grant_id").HasMaxLength(64);
            entity.Property(row => row.InviterGrantOwnerVersion)
                .HasColumnName("inviter_grant_owner_version");
            entity.Property(row => row.InviterAuthorizationEpoch)
                .HasColumnName("inviter_authorization_epoch");
            entity.Property(row => row.RequestedPermissionsJson)
                .HasColumnName("requested_permissions_json").HasColumnType("TEXT");
            entity.Property(row => row.TokenDigest).HasColumnName("token_digest").HasMaxLength(64);
            entity.Property(row => row.Purpose)
                .HasColumnName("purpose").HasConversion<string>().HasMaxLength(32);
            entity.Property(row => row.CommandFingerprint)
                .HasColumnName("command_fingerprint").HasMaxLength(64);
            entity.Property(row => row.IssuedAtUtc).HasColumnName("issued_at_utc").HasConversion(instant);
            entity.Property(row => row.AbsoluteExpiresAtUtc)
                .HasColumnName("absolute_expires_at_utc").HasConversion(instant);
            entity.Property(row => row.ConsumedAtUtc)
                .HasColumnName("consumed_at_utc").HasConversion(nullableInstant);
            entity.Property(row => row.RevokedAtUtc)
                .HasColumnName("revoked_at_utc").HasConversion(nullableInstant);
            entity.Property(row => row.OwnerVersion)
                .HasColumnName("owner_version").IsConcurrencyToken();
            entity.HasIndex(row => row.TokenDigest)
                .IsUnique().HasDatabaseName("ux_account_setup_invitation_token_digest");
            entity.HasIndex(row => new { row.TenantId, row.CommandFingerprint })
                .IsUnique().HasDatabaseName("ux_account_setup_invitation_command");
            entity.HasIndex(row => new { row.TenantId, row.AbsoluteExpiresAtUtc })
                .HasDatabaseName("ix_account_setup_invitation_expiry");
        });

        modelBuilder.Entity<RecoveryInvitationRecord>(entity =>
        {
            entity.ToTable("recovery_invitations", table =>
            {
                table.HasCheckConstraint(
                    "ck_recovery_invitation_owner_version",
                    "owner_version > 0");
                table.HasCheckConstraint(
                    "ck_recovery_invitation_expiry",
                    "absolute_expires_at_utc > issued_at_utc");
                table.HasCheckConstraint(
                    "ck_recovery_invitation_digest",
                    "length(token_digest) = 64 AND token_digest NOT GLOB '*[^0-9A-F]*'");
            });
            entity.HasKey(row => row.RecoveryInvitationId);
            entity.Property(row => row.RecoveryInvitationId)
                .HasColumnName("recovery_invitation_id").HasMaxLength(64);
            entity.Property(row => row.TenantId).HasColumnName("tenant_id").HasMaxLength(64);
            entity.Property(row => row.IssuerAccountId)
                .HasColumnName("issuer_account_id").HasMaxLength(64);
            entity.Property(row => row.IssuerPrincipalId)
                .HasColumnName("issuer_principal_id").HasMaxLength(256);
            entity.Property(row => row.TargetAccountId)
                .HasColumnName("target_account_id").HasMaxLength(64);
            entity.Property(row => row.TargetNormalizedUsername)
                .HasColumnName("target_normalized_username").HasMaxLength(320);
            entity.Property(row => row.TokenDigest).HasColumnName("token_digest").HasMaxLength(64);
            entity.Property(row => row.CommandFingerprint)
                .HasColumnName("command_fingerprint").HasMaxLength(64);
            entity.Property(row => row.IssuedAtUtc).HasColumnName("issued_at_utc").HasConversion(instant);
            entity.Property(row => row.AbsoluteExpiresAtUtc)
                .HasColumnName("absolute_expires_at_utc").HasConversion(instant);
            entity.Property(row => row.ConsumedAtUtc)
                .HasColumnName("consumed_at_utc").HasConversion(nullableInstant);
            entity.Property(row => row.CredentialCommitmentDigest)
                .HasColumnName("credential_commitment_digest").HasMaxLength(64);
            entity.Property(row => row.CompletedAtUtc)
                .HasColumnName("completed_at_utc").HasConversion(nullableInstant);
            entity.Property(row => row.RevokedAtUtc)
                .HasColumnName("revoked_at_utc").HasConversion(nullableInstant);
            entity.Property(row => row.OwnerVersion)
                .HasColumnName("owner_version").IsConcurrencyToken();
            entity.HasIndex(row => row.TokenDigest)
                .IsUnique().HasDatabaseName("ux_recovery_invitation_token_digest");
            entity.HasIndex(row => new { row.TenantId, row.CommandFingerprint })
                .IsUnique().HasDatabaseName("ux_recovery_invitation_command");
            entity.HasIndex(row => new { row.TargetAccountId, row.AbsoluteExpiresAtUtc })
                .HasDatabaseName("ix_recovery_invitation_target_expiry");
            entity.HasOne<InstallationAccountRecord>()
                .WithMany()
                .HasForeignKey(row => row.TargetAccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InstallationIdentityMigrationLeaseRecord>(entity =>
        {
            entity.ToTable("installation_identity_migration_lease", table =>
            {
                table.HasCheckConstraint("ck_identity_migration_lease_generation", "lease_generation > 0");
                table.HasCheckConstraint("ck_identity_migration_lease_owner_version", "owner_version > 0");
                table.HasCheckConstraint(
                    "ck_identity_migration_lease_expiry",
                    "expires_at_utc > acquired_at_utc");
            });
            entity.HasKey(row => row.SingletonKey);
            entity.Property(row => row.SingletonKey).HasColumnName("singleton_key").HasMaxLength(64);
            entity.Property(row => row.LeaseId).HasColumnName("lease_id").HasMaxLength(64);
            entity.Property(row => row.HolderFingerprint).HasColumnName("holder_fingerprint").HasMaxLength(128);
            entity.Property(row => row.LeaseGeneration).HasColumnName("lease_generation");
            entity.Property(row => row.OwnerVersion).HasColumnName("owner_version").IsConcurrencyToken();
            entity.Property(row => row.AcquiredAtUtc).HasColumnName("acquired_at_utc").HasConversion(instant);
            entity.Property(row => row.ExpiresAtUtc).HasColumnName("expires_at_utc").HasConversion(instant);
            entity.Property(row => row.ReleasedAtUtc).HasColumnName("released_at_utc").HasConversion(nullableInstant);
            entity.HasIndex(row => row.LeaseId).IsUnique().HasDatabaseName("ux_identity_migration_lease_id");
        });

        modelBuilder.Entity<InstallationIdentityMigrationSourceWatermarkRecord>(entity =>
        {
            entity.ToTable("installation_identity_migration_source_watermarks", table =>
            {
                table.HasCheckConstraint("ck_identity_source_watermark_version", "source_version >= 0");
                table.HasCheckConstraint("ck_identity_source_watermark_owner_version", "owner_version > 0");
            });
            entity.HasKey(row => new { row.SourceKind, row.SourcePartition });
            entity.Property(row => row.SourceKind).HasColumnName("source_kind").HasMaxLength(64);
            entity.Property(row => row.SourcePartition).HasColumnName("source_partition").HasMaxLength(128);
            entity.Property(row => row.MigrationRunId).HasColumnName("migration_run_id").HasMaxLength(64);
            entity.Property(row => row.SourceVersion).HasColumnName("source_version");
            entity.Property(row => row.HighWatermark).HasColumnName("high_watermark").HasMaxLength(256);
            entity.Property(row => row.SnapshotDigest).HasColumnName("snapshot_digest").HasMaxLength(128);
            entity.Property(row => row.OwnerVersion).HasColumnName("owner_version").IsConcurrencyToken();
            entity.Property(row => row.CapturedAtUtc).HasColumnName("captured_at_utc").HasConversion(instant);
            entity.HasIndex(row => new { row.MigrationRunId, row.SourceKind })
                .HasDatabaseName("ix_identity_source_watermarks_run_kind");
        });

        modelBuilder.Entity<InstallationIdentityMigrationCollisionRecord>(entity =>
        {
            entity.ToTable("installation_identity_migration_collisions", table =>
            {
                table.HasCheckConstraint("ck_identity_collision_candidate_count", "candidate_count > 1");
                table.HasCheckConstraint("ck_identity_collision_owner_version", "owner_version > 0");
            });
            entity.HasKey(row => row.CollisionId);
            entity.Property(row => row.CollisionId).HasColumnName("collision_id").HasMaxLength(64);
            entity.Property(row => row.CollisionKeyDigest).HasColumnName("collision_key_digest").HasMaxLength(128);
            entity.Property(row => row.CandidateCount).HasColumnName("candidate_count");
            entity.Property(row => row.ExpectedSourceVersion).HasColumnName("expected_source_version");
            entity.Property(row => row.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            entity.Property(row => row.RepairIdempotencyKeyDigest)
                .HasColumnName("repair_idempotency_key_digest").HasMaxLength(128);
            entity.Property(row => row.AuditCorrelationId).HasColumnName("audit_correlation_id").HasMaxLength(128);
            entity.Property(row => row.RepairCommandFingerprint)
                .HasColumnName("repair_command_fingerprint").HasMaxLength(128);
            entity.Property(row => row.RepairSourceKeyDigest)
                .HasColumnName("repair_source_key_digest").HasMaxLength(128);
            entity.Property(row => row.RepairTargetDigest)
                .HasColumnName("repair_target_digest").HasMaxLength(128);
            entity.Property(row => row.ExpectedOwnerVersion).HasColumnName("expected_owner_version");
            entity.Property(row => row.RecoveryActorId)
                .HasColumnName("recovery_actor_id").HasMaxLength(128);
            entity.Property(row => row.RecoveryRootEpoch).HasColumnName("recovery_root_epoch");
            entity.Property(row => row.RecoveryRootPublicKeyFingerprint)
                .HasColumnName("recovery_root_public_key_fingerprint").HasMaxLength(128);
            entity.Property(row => row.OwnerVersion).HasColumnName("owner_version").IsConcurrencyToken();
            entity.Property(row => row.CreatedAtUtc).HasColumnName("created_at_utc").HasConversion(instant);
            entity.Property(row => row.UpdatedAtUtc).HasColumnName("updated_at_utc").HasConversion(instant);
            entity.HasIndex(row => row.CollisionKeyDigest)
                .IsUnique().HasDatabaseName("ux_identity_migration_collision_digest");
        });

        modelBuilder.Entity<InstallationIdentityMigrationTombstoneRecord>(entity =>
        {
            entity.ToTable("installation_identity_migration_tombstones");
            entity.HasKey(row => row.TombstoneId);
            entity.Property(row => row.TombstoneId).HasColumnName("tombstone_id").HasMaxLength(64);
            entity.Property(row => row.SourceKind).HasColumnName("source_kind").HasMaxLength(64);
            entity.Property(row => row.SourceKeyDigest).HasColumnName("source_key_digest").HasMaxLength(128);
            entity.Property(row => row.SourceVersion).HasColumnName("source_version");
            entity.Property(row => row.ReasonCode).HasColumnName("reason_code").HasMaxLength(128);
            entity.Property(row => row.AuditCorrelationId).HasColumnName("audit_correlation_id").HasMaxLength(128);
            entity.Property(row => row.ProjectionDigest).HasColumnName("projection_digest").HasMaxLength(128);
            entity.Property(row => row.CreatedAtUtc).HasColumnName("created_at_utc").HasConversion(instant);
            entity.HasIndex(row => new { row.SourceKind, row.SourceKeyDigest })
                .IsUnique().HasDatabaseName("ux_identity_migration_tombstone_source");
            entity.HasIndex(row => row.AuditCorrelationId)
                .IsUnique().HasDatabaseName("ux_identity_migration_tombstone_audit");
        });

        modelBuilder.Entity<InstallationIdentityRootDesignationRecord>(entity =>
        {
            entity.ToTable("installation_identity_root_designation", table =>
                table.HasCheckConstraint("ck_identity_root_designation_owner_version", "owner_version > 0"));
            entity.HasKey(row => row.SingletonKey);
            entity.Property(row => row.SingletonKey).HasColumnName("singleton_key").HasMaxLength(64);
            entity.Property(row => row.DesignationId).HasColumnName("designation_id").HasMaxLength(64);
            entity.Property(row => row.SourceCompositeKeyDigest)
                .HasColumnName("source_composite_key_digest").HasMaxLength(128);
            entity.Property(row => row.AccountId).HasColumnName("account_id").HasMaxLength(64);
            entity.Property(row => row.ExpectedSourceVersion).HasColumnName("expected_source_version");
            entity.Property(row => row.IdempotencyKeyDigest)
                .HasColumnName("idempotency_key_digest").HasMaxLength(128);
            entity.Property(row => row.AuditCorrelationId).HasColumnName("audit_correlation_id").HasMaxLength(128);
            entity.Property(row => row.OwnerVersion).HasColumnName("owner_version").IsConcurrencyToken();
            entity.Property(row => row.DesignatedAtUtc).HasColumnName("designated_at_utc").HasConversion(instant);
            entity.Property(row => row.VerifiedAtUtc).HasColumnName("verified_at_utc").HasConversion(nullableInstant);
            entity.HasIndex(row => row.DesignationId).IsUnique().HasDatabaseName("ux_identity_root_designation_id");
            entity.HasIndex(row => row.IdempotencyKeyDigest)
                .IsUnique().HasDatabaseName("ux_identity_root_designation_idempotency");
            entity.HasIndex(row => row.AuditCorrelationId)
                .IsUnique().HasDatabaseName("ux_identity_root_designation_audit");
            entity.HasOne<InstallationAccountRecord>()
                .WithOne()
                .HasForeignKey<InstallationIdentityRootDesignationRecord>(row => row.AccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InstallationIdentityCutoverStateRecord>(entity =>
        {
            entity.ToTable("installation_identity_cutover_state", table =>
            {
                table.HasCheckConstraint("ck_identity_cutover_authority_version", "authority_version >= 1");
                table.HasCheckConstraint("ck_identity_cutover_barrier_version", "v1_write_barrier_version >= 0");
                table.HasCheckConstraint("ck_identity_cutover_owner_version", "owner_version > 0");
            });
            entity.HasKey(row => row.SingletonKey);
            entity.Property(row => row.SingletonKey).HasColumnName("singleton_key").HasMaxLength(64);
            entity.Property(row => row.Stage).HasColumnName("stage").HasConversion<string>().HasMaxLength(32);
            entity.Property(row => row.AuthorityVersion).HasColumnName("authority_version");
            entity.Property(row => row.MigrationRunId).HasColumnName("migration_run_id").HasMaxLength(64);
            entity.Property(row => row.V1WriteBarrierVersion).HasColumnName("v1_write_barrier_version");
            entity.Property(row => row.SourceWatermarkDigest)
                .HasColumnName("source_watermark_digest").HasMaxLength(128);
            entity.Property(row => row.FinalVerificationDigest)
                .HasColumnName("final_verification_digest").HasMaxLength(128);
            entity.Property(row => row.LegacyBearerRevocationDigest)
                .HasColumnName("legacy_bearer_revocation_digest").HasMaxLength(128);
            entity.Property(row => row.OwnerVersion).HasColumnName("owner_version").IsConcurrencyToken();
            entity.Property(row => row.UpdatedAtUtc).HasColumnName("updated_at_utc").HasConversion(instant);
            entity.Property(row => row.CommittedAtUtc).HasColumnName("committed_at_utc").HasConversion(nullableInstant);
            entity.HasData(new InstallationIdentityCutoverStateRecord
            {
                SingletonKey = InstallationIdentityCutoverStateRecord.SingletonKeyValue,
                Stage = InstallationIdentityCutoverStage.LegacyV1Authoritative,
                AuthorityVersion = InstallationIdentityCutoverStateRecord.LegacyV1AuthorityVersion,
                MigrationRunId = null,
                V1WriteBarrierVersion = 0,
                SourceWatermarkDigest = null,
                FinalVerificationDigest = null,
                LegacyBearerRevocationDigest = null,
                OwnerVersion = 1,
                UpdatedAtUtc = DateTimeOffset.UnixEpoch,
                CommittedAtUtc = null,
            });
        });
    }
}
