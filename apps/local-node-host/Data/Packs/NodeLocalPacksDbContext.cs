using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Packs;

/// <summary>
/// Node-exclusive EF Core <see cref="DbContext"/> for the DURABLE pack-install store (F5;
/// migration-update-architecture D5.2 / D5.3). Persists — per tenant — the installed pack seed layers, the S-8
/// monotonic watermark, the tenant override rows, and the per-key owning-pack choices, so an installed + activated
/// pack SURVIVES a node restart / <c>deploy-dogfood</c> redeploy without a from-empty re-seed (the DOGFOOD.md
/// caveat this closes). Mirrors <see cref="Admission.NodeLocalAdmissionDbContext"/> / <c>NodeLocalRosterDbContext</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate context, same encrypted file.</b> This context is deliberately NOT <c>LocalNodeDbContext</c> and its
/// rows are NOT shared <c>IHarborlineEntityModule</c> entities: installed-pack state is admitter-local install
/// bookkeeping that never syncs, so it stays out of the council C2 both-provider parity check. It opens the SAME
/// SQLCipher-encrypted <c>local-node.db</c> file, keyed through the same connection interceptor — there is no
/// plaintext path (SC-1).
/// </para>
/// <para>
/// <b>Distinct migration-history table.</b> Several EF contexts each call <c>MigrateAsync</c> against one SQLite
/// file; they must not share the default <c>__EFMigrationsHistory</c> table. This context records its history in
/// <c>__PacksMigrationsHistory</c>.
/// </para>
/// </remarks>
public sealed class NodeLocalPacksDbContext : DbContext
{
    /// <summary>
    /// Dedicated migration-history table name — keeps this context's applied-migration records out of the
    /// financial store's default <c>__EFMigrationsHistory</c> table (the contexts share one file).
    /// </summary>
    public const string MigrationsHistoryTableName = "__PacksMigrationsHistory";

    /// <summary>Initialises a new instance of <see cref="NodeLocalPacksDbContext"/>.</summary>
    public NodeLocalPacksDbContext(DbContextOptions<NodeLocalPacksDbContext> options)
        : base(options)
    {
    }

    /// <summary>The installed pack version rows (immutable seed layers, all lifecycles).</summary>
    internal DbSet<PackInstalledVersionRow> InstalledVersions => Set<PackInstalledVersionRow>();

    /// <summary>The S-8 monotonic watermark rows (one per tenant + pack key).</summary>
    internal DbSet<PackWatermarkRow> Watermarks => Set<PackWatermarkRow>();

    /// <summary>The tenant override rows (RFC-7396 overlay patches, the S-10 re-attach source).</summary>
    internal DbSet<PackTenantOverrideRow> Overrides => Set<PackTenantOverrideRow>();

    /// <summary>The per-key owning-pack choices (ADR 0129 D8 cross-pack collision resolution).</summary>
    internal DbSet<PackKeyOwnershipRow> KeyOwnership => Set<PackKeyOwnershipRow>();

    /// <summary>Admitted lifecycle transitions awaiting or recording projection completion.</summary>
    internal DbSet<PackProjectionAdmissionRow> ProjectionAdmissions => Set<PackProjectionAdmissionRow>();

    /// <summary>The update-feed per-channel anti-rollback high-water rows (F2 — update-feed §7.2).</summary>
    internal DbSet<FeedChannelSequenceRow> FeedChannelSequences => Set<FeedChannelSequenceRow>();

    /// <summary>The effective configuration-generation pointer per tenant (T-644).</summary>
    internal DbSet<ConfigurationEffectiveGenerationRow> EffectiveGenerations => Set<ConfigurationEffectiveGenerationRow>();

    /// <summary>Isolated prepared candidate projections (T-644); inert until a switch commits.</summary>
    internal DbSet<ConfigurationPreparedProjectionRow> PreparedProjections => Set<ConfigurationPreparedProjectionRow>();

    /// <summary>The evidence outbox committed with every activation (T-644).</summary>
    internal DbSet<ConfigurationEvidenceOutboxRow> EvidenceOutbox => Set<ConfigurationEvidenceOutboxRow>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<PackInstalledVersionRow>(e =>
        {
            e.ToTable("pack_installed_versions");
            e.HasKey(r => new { r.Tenant, r.PackKey, r.Version });
            e.Property(r => r.Tenant).HasColumnName("tenant").HasMaxLength(256);
            e.Property(r => r.PackKey).HasColumnName("pack_key").HasMaxLength(256);
            e.Property(r => r.Version).HasColumnName("version").HasMaxLength(64);
            e.Property(r => r.Lifecycle).HasColumnName("lifecycle");
            e.Property(r => r.PayloadJson).HasColumnName("payload_json");
            // The read hot-path: "the ACTIVE version for a (tenant, pack key)". A partial-ish index on the
            // lifecycle discriminator keeps GetActive from scanning every version of a key.
            e.HasIndex(r => new { r.Tenant, r.PackKey, r.Lifecycle });
        });

        modelBuilder.Entity<PackWatermarkRow>(e =>
        {
            e.ToTable("pack_watermarks");
            e.HasKey(r => new { r.Tenant, r.PackKey });
            e.Property(r => r.Tenant).HasColumnName("tenant").HasMaxLength(256);
            e.Property(r => r.PackKey).HasColumnName("pack_key").HasMaxLength(256);
            e.Property(r => r.Version).HasColumnName("version").HasMaxLength(64);
            e.Property(r => r.FloorsJson).HasColumnName("floors_json");
        });

        modelBuilder.Entity<PackTenantOverrideRow>(e =>
        {
            e.ToTable("pack_overrides");
            e.HasKey(r => new { r.Tenant, r.PackKey, r.ContentKey });
            e.Property(r => r.Tenant).HasColumnName("tenant").HasMaxLength(256);
            e.Property(r => r.PackKey).HasColumnName("pack_key").HasMaxLength(256);
            e.Property(r => r.ContentKey).HasColumnName("content_key").HasMaxLength(512);
            e.Property(r => r.OverlayJson).HasColumnName("overlay_json");
        });

        modelBuilder.Entity<PackKeyOwnershipRow>(e =>
        {
            e.ToTable("pack_key_ownership");
            e.HasKey(r => new { r.Tenant, r.ContentKey });
            e.Property(r => r.Tenant).HasColumnName("tenant").HasMaxLength(256);
            e.Property(r => r.ContentKey).HasColumnName("content_key").HasMaxLength(512);
            e.Property(r => r.OwningPackKey).HasColumnName("owning_pack_key").HasMaxLength(256);
        });

        modelBuilder.Entity<PackProjectionAdmissionRow>(e =>
        {
            e.ToTable("pack_projection_admissions");
            e.HasKey(r => r.AdmissionId);
            e.Property(r => r.AdmissionId).HasColumnName("admission_id");
            e.Property(r => r.Tenant).HasColumnName("tenant").HasMaxLength(256);
            e.Property(r => r.PackId).HasColumnName("pack_id").HasMaxLength(256);
            e.Property(r => r.PackVersion).HasColumnName("pack_version").HasMaxLength(64);
            e.Property(r => r.Principal).HasColumnName("principal").HasMaxLength(512);
            e.Property(r => r.Instant).HasColumnName("instant");
            e.Property(r => r.DerivationIdsJson).HasColumnName("derivation_ids_json");
            e.Property(r => r.Projected).HasColumnName("projected");
            e.HasIndex(r => new { r.Tenant, r.Projected });
        });

        modelBuilder.Entity<FeedChannelSequenceRow>(e =>
        {
            e.ToTable("feed_channel_sequences");
            e.HasKey(r => new { r.Tenant, r.ChannelId });
            e.Property(r => r.Tenant).HasColumnName("tenant").HasMaxLength(256);
            e.Property(r => r.ChannelId).HasColumnName("channel_id").HasMaxLength(256);
            e.Property(r => r.HighWaterSequence).HasColumnName("high_water_sequence");
        });

        modelBuilder.Entity<ConfigurationEffectiveGenerationRow>(e =>
        {
            e.ToTable("configuration_effective_generations");
            e.HasKey(r => r.Tenant);
            e.Property(r => r.Tenant).HasColumnName("tenant").HasMaxLength(256);
            e.Property(r => r.Digest).HasColumnName("digest").HasMaxLength(64);
            e.Property(r => r.ReferencesJson).HasColumnName("references_json");
            e.Property(r => r.Principal).HasColumnName("principal").HasMaxLength(512);
            e.Property(r => r.ActivatedAt).HasColumnName("activated_at");
            e.Property(r => r.DecisionId).HasColumnName("decision_id").HasMaxLength(64);
            e.Property(r => r.EvidenceIntentId).HasColumnName("evidence_intent_id").HasMaxLength(256);
        });

        modelBuilder.Entity<ConfigurationPreparedProjectionRow>(e =>
        {
            e.ToTable("configuration_prepared_projections");
            e.HasKey(r => new { r.Tenant, r.CandidateDigest });
            e.Property(r => r.Tenant).HasColumnName("tenant").HasMaxLength(256);
            e.Property(r => r.CandidateDigest).HasColumnName("candidate_digest").HasMaxLength(64);
            e.Property(r => r.Revision).HasColumnName("revision").HasMaxLength(64);
            e.Property(r => r.ProjectionDigest).HasColumnName("projection_digest").HasMaxLength(64);
            e.Property(r => r.ReferencesJson).HasColumnName("references_json");
            e.Property(r => r.BaselineDigest).HasColumnName("baseline_digest").HasMaxLength(64);
            e.Property(r => r.DestinationDigest).HasColumnName("destination_digest").HasMaxLength(64);
            e.Property(r => r.PreparedAt).HasColumnName("prepared_at");
        });

        modelBuilder.Entity<ConfigurationEvidenceOutboxRow>(e =>
        {
            e.ToTable("configuration_evidence_outbox");
            e.HasKey(r => new { r.Tenant, r.IntentId });
            e.Property(r => r.Tenant).HasColumnName("tenant").HasMaxLength(256);
            e.Property(r => r.IntentId).HasColumnName("intent_id").HasMaxLength(256);
            e.Property(r => r.Reason).HasColumnName("reason");
            e.Property(r => r.InputsDigest).HasColumnName("inputs_digest").HasMaxLength(64);
            e.Property(r => r.DecisionId).HasColumnName("decision_id").HasMaxLength(64);
            e.Property(r => r.DecisionJson).HasColumnName("decision_json");
            e.Property(r => r.PriorDigest).HasColumnName("prior_digest").HasMaxLength(64);
            e.Property(r => r.NewDigest).HasColumnName("new_digest").HasMaxLength(64);
            e.Property(r => r.Principal).HasColumnName("principal").HasMaxLength(512);
            e.Property(r => r.CommittedAt).HasColumnName("committed_at");
            e.Property(r => r.PublishedAt).HasColumnName("published_at");
            e.HasIndex(r => new { r.Tenant, r.PublishedAt });
        });
    }
}
