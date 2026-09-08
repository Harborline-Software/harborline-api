using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Scheduling;

/// <summary>Node-exclusive scheduling authoring context over the SQLCipher local-node store.</summary>
public sealed class NodeLocalSchedulingDbContext(DbContextOptions<NodeLocalSchedulingDbContext> options)
    : DbContext(options)
{
    public const string MigrationsHistoryTableName = "__SchedulingMigrationsHistory";

    public DbSet<NodeSchedulingDraftRow> Drafts => Set<NodeSchedulingDraftRow>();
    public DbSet<NodeSchedulingDraftAuditRow> DraftAudit => Set<NodeSchedulingDraftAuditRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NodeSchedulingDraftRow>(entity =>
        {
            entity.ToTable("scheduling_definition_drafts");
            entity.HasKey(row => new { row.TenantId, row.DefinitionId, row.Revision });
            entity.Property(row => row.TenantId).HasColumnName("tenant_id").HasMaxLength(128);
            entity.Property(row => row.DefinitionId).HasColumnName("definition_id").HasMaxLength(160);
            entity.Property(row => row.Revision).HasColumnName("revision");
            entity.Property(row => row.DefinitionJson).HasColumnName("definition_json");
            entity.Property(row => row.UpdatedBy).HasColumnName("updated_by").HasMaxLength(200);
            entity.Property(row => row.UpdatedAtUtc).HasColumnName("updated_at_utc");
            entity.HasIndex(row => new { row.TenantId, row.DefinitionId, row.Revision });
        });
        modelBuilder.Entity<NodeSchedulingDraftAuditRow>(entity =>
        {
            entity.ToTable("scheduling_definition_draft_audit");
            entity.HasKey(row => new { row.TenantId, row.AuditId });
            entity.Property(row => row.TenantId).HasColumnName("tenant_id").HasMaxLength(128);
            entity.Property(row => row.AuditId).HasColumnName("audit_id").HasMaxLength(36);
            entity.Property(row => row.DefinitionId).HasColumnName("definition_id").HasMaxLength(160);
            entity.Property(row => row.Revision).HasColumnName("revision");
            entity.Property(row => row.ActorId).HasColumnName("actor_id").HasMaxLength(200);
            entity.Property(row => row.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.HasIndex(row => new { row.TenantId, row.DefinitionId, row.Revision }).IsUnique();
        });
    }
}
