using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>
/// Node-exclusive SQLCipher-backed persistence for hosted-web session audiences, revocations, and
/// antiforgery state. Raw bearer, session, and antiforgery tokens are never part of this model.
/// </summary>
/// <remarks>
/// <para>
/// <b>These tables are storage only. Reading a row is NOT an authorization decision.</b> The CHECK
/// constraints below fence what may be WRITTEN — they say nothing about whether a stored row is
/// still good at the moment of use.
/// </para>
/// <para>
/// The effect-time authority is <see cref="WebSelectedSessionPrincipalAuthority"/> together with
/// <see cref="WebSelectedSessionStore"/>; <see cref="WebAntiforgeryPolicy"/> is the antiforgery
/// equivalent. They are the reference implementations — read them before writing a new consumer.
/// Every consumer that turns one of these rows into an effect is bound by three MUSTs, carried
/// forward from the deep review of the schema change that introduced this context:
/// </para>
/// <list type="number">
///   <item>
///     MUST consult <see cref="WebSessionRevocationRecord"/> at effect time. A row's continued
///     presence in an audience table is not evidence that it is live — revocation and supersession
///     are recorded separately, by design, and an absent revocation lookup silently honors a
///     revoked session. Satisfied today at <c>WebSelectedSessionStore.cs:47</c>. NOTE the
///     deliberate divergence: the AccountChallenge audience in <c>WebAntiforgeryPolicy.cs:218-222</c>
///     does NOT consult this table — it relies on that row's own ConsumedAtUtc/RevokedAtUtc columns,
///     unlike the SelectedSession and InstallationSession branches. Audit that choice before copying
///     either shape.
///   </item>
///   <item>
///     MUST enforce BOTH the idle and the absolute expiry against a trusted server clock. The
///     stored instants are the fence; a client-supplied or otherwise untrusted clock reading makes
///     the fence forgeable, and enforcing only the absolute TTL leaves the idle window unfenced.
///     Satisfied today at <c>WebSelectedSessionStore.cs:87-88</c>.
///   </item>
///   <item>
///     MUST re-validate <see cref="WebUserSessionRecord.PinnedGrantOwnerVersions"/> — and the
///     installation audience's equivalent grant owner version — against current authority at effect
///     time (the grant-fence pattern). The pins record what was true at issue; an authority change
///     since issue MUST invalidate the session rather than being read as still-current.
///     Satisfied today at <c>WebSelectedSessionPrincipalAuthority.cs:169-192</c>.
///   </item>
/// </list>
/// </remarks>
public sealed class NodeLocalWebSessionDbContext : DbContext
{
    public const string MigrationsHistoryTableName = "__WebSessionMigrationsHistory";

    public NodeLocalWebSessionDbContext(DbContextOptions<NodeLocalWebSessionDbContext> options)
        : base(options)
    {
    }

    public DbSet<WebAccountAccessChallengeRecord> AccountAccessChallenges =>
        Set<WebAccountAccessChallengeRecord>();

    public DbSet<WebUserSessionRecord> UserSessions => Set<WebUserSessionRecord>();

    public DbSet<WebInstallationSessionRecord> InstallationSessions =>
        Set<WebInstallationSessionRecord>();

    public DbSet<WebSessionRevocationRecord> Revocations => Set<WebSessionRevocationRecord>();

    public DbSet<WebAntiforgeryStateRecord> AntiforgeryStates => Set<WebAntiforgeryStateRecord>();

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
        var pinnedGrantList = new ValueConverter<IReadOnlyList<PinnedGrantOwnerVersion>, string>(
            value => SerializePinnedGrants(value),
            value => DeserializePinnedGrants(value));
        var pinnedGrantComparer = new ValueComparer<IReadOnlyList<PinnedGrantOwnerVersion>>(
            (left, right) => PinnedGrantsEqual(left, right),
            value => PinnedGrantsHash(value),
            value => value.ToArray());

        modelBuilder.Entity<WebAccountAccessChallengeRecord>(entity =>
        {
            entity.ToTable("web_account_access_challenges", table =>
            {
                table.HasCheckConstraint("ck_web_account_challenge_owner_version", "owner_version > 0");
                table.HasCheckConstraint(
                    "ck_web_account_challenge_expiry",
                    "absolute_expires_at_utc > issued_at_utc");
            });
            entity.HasKey(row => row.ChallengeId);
            entity.Property(row => row.ChallengeId).HasColumnName("challenge_id").HasMaxLength(64);
            entity.Property(row => row.AccountId).HasColumnName("account_id").HasMaxLength(64);
            entity.Property(row => row.AccountSecurityVersion).HasColumnName("account_security_version");
            entity.Property(row => row.HandleDigest).HasColumnName("handle_digest").HasMaxLength(128);
            entity.Property(row => row.CoordinationCorrelationId)
                .HasColumnName("coordination_correlation_id").HasMaxLength(128);
            entity.Property(row => row.IssuedAtUtc).HasColumnName("issued_at_utc").HasConversion(instant);
            entity.Property(row => row.AbsoluteExpiresAtUtc)
                .HasColumnName("absolute_expires_at_utc").HasConversion(instant);
            entity.Property(row => row.ConsumedAtUtc)
                .HasColumnName("consumed_at_utc").HasConversion(nullableInstant);
            entity.Property(row => row.RevokedAtUtc)
                .HasColumnName("revoked_at_utc").HasConversion(nullableInstant);
            entity.Property(row => row.OwnerVersion)
                .HasColumnName("owner_version").IsConcurrencyToken();
            entity.HasIndex(row => row.HandleDigest)
                .IsUnique().HasDatabaseName("ux_web_account_challenge_handle_digest");
            entity.HasIndex(row => row.AccountId).HasDatabaseName("ix_web_account_challenge_account");
            entity.HasIndex(row => row.AbsoluteExpiresAtUtc)
                .HasDatabaseName("ix_web_account_challenge_expiry");
        });

        modelBuilder.Entity<WebUserSessionRecord>(entity =>
        {
            entity.ToTable("web_user_sessions", table =>
            {
                table.HasCheckConstraint("ck_web_user_session_owner_version", "owner_version > 0");
                table.HasCheckConstraint(
                    "ck_web_user_session_idle_expiry",
                    "idle_expires_at_utc > issued_at_utc AND idle_expires_at_utc <= absolute_expires_at_utc");
                table.HasCheckConstraint(
                    "ck_web_user_session_absolute_expiry",
                    "absolute_expires_at_utc > issued_at_utc");
            });
            entity.HasKey(row => row.SessionCorrelationId);
            entity.Property(row => row.SessionCorrelationId)
                .HasColumnName("session_correlation_id").HasMaxLength(128);
            entity.Property(row => row.AccountId).HasColumnName("account_id").HasMaxLength(64);
            entity.Property(row => row.AccountSecurityVersion).HasColumnName("account_security_version");
            entity.Property(row => row.TenantId).HasColumnName("tenant_id").HasMaxLength(64);
            entity.Property(row => row.MembershipId).HasColumnName("membership_id").HasMaxLength(64);
            entity.Property(row => row.MembershipOwnerVersion).HasColumnName("membership_owner_version");
            entity.Property(row => row.TenantPrincipalId)
                .HasColumnName("tenant_principal_id").HasMaxLength(256);
            entity.Property(row => row.CanonicalPartyReference)
                .HasColumnName("canonical_party_reference").HasMaxLength(256);
            var pinnedGrants = entity.Property(row => row.PinnedGrantOwnerVersions)
                .HasColumnName("pinned_grant_owner_versions_json")
                .HasColumnType("TEXT")
                .HasConversion(pinnedGrantList);
            pinnedGrants.Metadata.SetValueComparer(pinnedGrantComparer);
            entity.Property(row => row.AuthorizationEpoch).HasColumnName("authorization_epoch");
            entity.Property(row => row.HandleDigest).HasColumnName("handle_digest").HasMaxLength(128);
            entity.Property(row => row.AntiforgeryStateId)
                .HasColumnName("antiforgery_state_id").HasMaxLength(64);
            entity.Property(row => row.CoordinationCorrelationId)
                .HasColumnName("coordination_correlation_id").HasMaxLength(128);
            entity.Property(row => row.IssuedAtUtc).HasColumnName("issued_at_utc").HasConversion(instant);
            entity.Property(row => row.IdleExpiresAtUtc)
                .HasColumnName("idle_expires_at_utc").HasConversion(instant);
            entity.Property(row => row.AbsoluteExpiresAtUtc)
                .HasColumnName("absolute_expires_at_utc").HasConversion(instant);
            entity.Property(row => row.OwnerVersion)
                .HasColumnName("owner_version").IsConcurrencyToken();
            entity.HasIndex(row => row.HandleDigest)
                .IsUnique().HasDatabaseName("ux_web_user_session_handle_digest");
            entity.HasIndex(row => new { row.AccountId, row.TenantId })
                .HasDatabaseName("ix_web_user_session_account_tenant");
            entity.HasIndex(row => new { row.TenantId, row.MembershipId })
                .HasDatabaseName("ix_web_user_session_membership");
            entity.HasIndex(row => row.AntiforgeryStateId)
                .HasDatabaseName("ix_web_user_session_antiforgery");
            entity.HasIndex(row => row.AbsoluteExpiresAtUtc)
                .HasDatabaseName("ix_web_user_session_expiry");
        });

        modelBuilder.Entity<WebInstallationSessionRecord>(entity =>
        {
            entity.ToTable("web_installation_sessions", table =>
            {
                table.HasCheckConstraint("ck_web_installation_session_owner_version", "owner_version > 0");
                table.HasCheckConstraint(
                    "ck_web_installation_session_authority_versions",
                    "account_security_version > 0 AND installation_grant_owner_version > 0 " +
                    "AND authorization_epoch > 0");
                table.HasCheckConstraint(
                    "ck_web_installation_session_handle_digest",
                    "length(handle_digest) = 64 AND handle_digest NOT GLOB '*[^0-9A-F]*'");
                table.HasCheckConstraint(
                    "ck_web_installation_session_expiry",
                    "absolute_expires_at_utc > issued_at_utc " +
                    "AND absolute_expires_at_utc <= issued_at_utc + 900000");
            });
            entity.HasKey(row => row.SessionCorrelationId);
            entity.Property(row => row.SessionCorrelationId)
                .HasColumnName("session_correlation_id").HasMaxLength(128);
            entity.Property(row => row.AccountId).HasColumnName("account_id").HasMaxLength(64);
            entity.Property(row => row.AccountSecurityVersion).HasColumnName("account_security_version");
            entity.Property(row => row.InstallationGrantId)
                .HasColumnName("installation_grant_id").HasMaxLength(64);
            entity.Property(row => row.InstallationGrantOwnerVersion)
                .HasColumnName("installation_grant_owner_version");
            entity.Property(row => row.AuthorizationEpoch).HasColumnName("authorization_epoch");
            entity.Property(row => row.HandleDigest).HasColumnName("handle_digest").HasMaxLength(128);
            entity.Property(row => row.AntiforgeryStateId)
                .HasColumnName("antiforgery_state_id").HasMaxLength(64);
            entity.Property(row => row.CoordinationCorrelationId)
                .HasColumnName("coordination_correlation_id").HasMaxLength(128);
            entity.Property(row => row.IssuedAtUtc).HasColumnName("issued_at_utc").HasConversion(instant);
            entity.Property(row => row.AbsoluteExpiresAtUtc)
                .HasColumnName("absolute_expires_at_utc").HasConversion(instant);
            entity.Property(row => row.OwnerVersion)
                .HasColumnName("owner_version").IsConcurrencyToken();
            entity.HasIndex(row => row.HandleDigest)
                .IsUnique().HasDatabaseName("ux_web_installation_session_handle_digest");
            entity.HasIndex(row => row.AccountId).HasDatabaseName("ix_web_installation_session_account");
            entity.HasIndex(row => row.AntiforgeryStateId)
                .HasDatabaseName("ix_web_installation_session_antiforgery");
            entity.HasIndex(row => row.AbsoluteExpiresAtUtc)
                .HasDatabaseName("ix_web_installation_session_expiry");
        });

        modelBuilder.Entity<WebSessionRevocationRecord>(entity =>
        {
            entity.ToTable("web_session_revocations", table =>
                table.HasCheckConstraint("ck_web_session_revocation_owner_version", "owner_version > 0"));
            entity.HasKey(row => row.RevocationId);
            entity.Property(row => row.RevocationId).HasColumnName("revocation_id").HasMaxLength(64);
            entity.Property(row => row.Audience)
                .HasColumnName("audience").HasConversion<string>().HasMaxLength(32);
            entity.Property(row => row.AccountId).HasColumnName("account_id").HasMaxLength(64);
            entity.Property(row => row.SubjectCorrelationId)
                .HasColumnName("subject_correlation_id").HasMaxLength(128);
            entity.Property(row => row.SupersededByCorrelationId)
                .HasColumnName("superseded_by_correlation_id").HasMaxLength(128);
            entity.Property(row => row.ReasonCode).HasColumnName("reason_code").HasMaxLength(128);
            entity.Property(row => row.CoordinationCorrelationId)
                .HasColumnName("coordination_correlation_id").HasMaxLength(128);
            entity.Property(row => row.RevokedAtUtc).HasColumnName("revoked_at_utc").HasConversion(instant);
            entity.Property(row => row.OwnerVersion)
                .HasColumnName("owner_version").IsConcurrencyToken();
            entity.HasIndex(row => new { row.Audience, row.SubjectCorrelationId })
                .IsUnique().HasDatabaseName("ux_web_session_revocation_subject");
            entity.HasIndex(row => row.AccountId).HasDatabaseName("ix_web_session_revocation_account");
        });

        modelBuilder.Entity<WebAntiforgeryStateRecord>(entity =>
        {
            entity.ToTable("web_antiforgery_states", table =>
            {
                table.HasCheckConstraint("ck_web_antiforgery_state_owner_version", "owner_version > 0");
                table.HasCheckConstraint(
                    "ck_web_antiforgery_state_expiry",
                    "absolute_expires_at_utc > issued_at_utc");
            });
            entity.HasKey(row => row.AntiforgeryStateId);
            entity.Property(row => row.AntiforgeryStateId)
                .HasColumnName("antiforgery_state_id").HasMaxLength(64);
            entity.Property(row => row.Audience)
                .HasColumnName("audience").HasConversion<string>().HasMaxLength(32);
            entity.Property(row => row.AccountId).HasColumnName("account_id").HasMaxLength(64);
            entity.Property(row => row.SubjectCorrelationId)
                .HasColumnName("subject_correlation_id").HasMaxLength(128);
            entity.Property(row => row.TokenDigest).HasColumnName("token_digest").HasMaxLength(128);
            entity.Property(row => row.CoordinationCorrelationId)
                .HasColumnName("coordination_correlation_id").HasMaxLength(128);
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
                .IsUnique().HasDatabaseName("ux_web_antiforgery_state_token_digest");
            entity.HasIndex(row => new { row.Audience, row.SubjectCorrelationId })
                .IsUnique()
                .HasFilter("consumed_at_utc IS NULL AND revoked_at_utc IS NULL")
                .HasDatabaseName("ux_web_antiforgery_state_active_subject");
            entity.HasIndex(row => row.AbsoluteExpiresAtUtc)
                .HasDatabaseName("ix_web_antiforgery_state_expiry");
        });
    }

    private static string SerializePinnedGrants(IReadOnlyList<PinnedGrantOwnerVersion> values) =>
        JsonSerializer.Serialize(values);

    private static IReadOnlyList<PinnedGrantOwnerVersion> DeserializePinnedGrants(string value) =>
        JsonSerializer.Deserialize<PinnedGrantOwnerVersion[]>(value) ?? [];

    private static bool PinnedGrantsEqual(
        IReadOnlyList<PinnedGrantOwnerVersion>? left,
        IReadOnlyList<PinnedGrantOwnerVersion>? right) =>
        ReferenceEquals(left, right) ||
        (left is not null && right is not null && left.SequenceEqual(right));

    private static int PinnedGrantsHash(IReadOnlyList<PinnedGrantOwnerVersion> values)
    {
        var hash = new HashCode();
        foreach (var value in values)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }
}
