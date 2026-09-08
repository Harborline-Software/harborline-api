using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Drafts;

/// <summary>
/// Node-exclusive EF Core <see cref="DbContext"/> for the D2 save-and-resume
/// submission-draft store (ADR 0135 amendment 2026-07-01). Maps the flat
/// <see cref="SubmissionDraftRow"/> onto the <c>form_drafts</c> table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate context, same encrypted file (Pattern B).</b> Like
/// <c>NodeLocalPayrollDbContext</c> / <c>NodeLocalCommsDbContext</c>, this is a
/// node-exclusive context that opens the SAME SQLCipher-encrypted <c>local-node.db</c>
/// file (keyed by the shared <c>SqlCipherConnectionInterceptor</c>, SC-1) but records its
/// migrations in its OWN history table (<see cref="MigrationsHistoryTableName"/>) so its
/// <c>MigrateAsync</c> never clobbers another context's applied-migration records. A draft
/// is node-local working state with no Bridge/Postgres counterpart, so it is deliberately
/// not a shared <c>IHarborlineEntityModule</c> (the council C2 both-provider parity check
/// does not apply).
/// </para>
/// <para>
/// <b>Restart-survival.</b> Because the drafts live in this durable SQLite file (not the
/// process-volatile in-memory store), a saved draft reloads by <c>(tenant, case, party)</c>
/// after a host restart / on any device the same file reaches — the D2 cross-device-resume
/// requirement.
/// </para>
/// </remarks>
public sealed class NodeLocalDraftsDbContext : DbContext
{
    /// <summary>Dedicated migration-history table name for this context.</summary>
    public const string MigrationsHistoryTableName = "__DraftsMigrationsHistory";

    /// <summary>Initialises a new instance of <see cref="NodeLocalDraftsDbContext"/>.</summary>
    public NodeLocalDraftsDbContext(DbContextOptions<NodeLocalDraftsDbContext> options)
        : base(options)
    {
    }

    /// <summary>The node-local submission-drafts.</summary>
    public DbSet<SubmissionDraftRow> Drafts => Set<SubmissionDraftRow>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<SubmissionDraftRow>(e =>
        {
            e.ToTable("form_drafts");
            // The D2 keying tuple IS the primary key.
            e.HasKey(r => new { r.TenantId, r.CaseId, r.PartyId });
            e.Property(r => r.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(r => r.CaseId).HasColumnName("case_id").IsRequired();
            e.Property(r => r.PartyId).HasColumnName("party_id").IsRequired();
            e.Property(r => r.FormId).HasColumnName("form_id").IsRequired();
            e.Property(r => r.SchemaRef).HasColumnName("schema_ref");
            e.Property(r => r.DefinitionId).HasColumnName("definition_id");
            e.Property(r => r.DefinitionVersion).HasColumnName("definition_version");
            e.Property(r => r.EngineVersion).HasColumnName("engine_version");
            e.Property(r => r.LocaleChainJson).HasColumnName("locale_chain_json");
            e.Property(r => r.Body).HasColumnName("body");
            e.Property(r => r.SubjectId).HasColumnName("subject_id");
            e.Property(r => r.CreatedAtUtc).HasColumnName("created_at_utc");
            e.Property(r => r.UpdatedAtUtc).HasColumnName("updated_at_utc");
            e.Property(r => r.ExpiresAtUtc).HasColumnName("expires_at_utc");
            // Resume "my in-progress cases" — party-scoped list (the ADR 0135 §2.8.2 surface, by PARTY).
            e.HasIndex(r => new { r.TenantId, r.PartyId }).HasDatabaseName("ix_form_drafts_tenant_party");
            // Retention sweep scans by expiry.
            e.HasIndex(r => r.ExpiresAtUtc).HasDatabaseName("ix_form_drafts_expires_at");
            // Crypto-shred / legal-hold visibility scans by subject.
            e.HasIndex(r => new { r.TenantId, r.SubjectId }).HasDatabaseName("ix_form_drafts_tenant_subject");
        });
    }
}
