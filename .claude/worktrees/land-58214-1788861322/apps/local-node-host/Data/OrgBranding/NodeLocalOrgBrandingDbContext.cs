using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Harborline.Api.LocalNodeHost.Data.OrgBranding;

/// <summary>
/// Node-exclusive EF Core <see cref="DbContext"/> for the local-authoritative <c>org_branding</c> table — the
/// durable home of the tenant's <c>OrgBrandingProfile</c> (tenant-branding slice T1). Mirrors
/// <c>NodeLocalRosterDbContext</c> / <c>NodeLocalCalendarDbContext</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate context, same encrypted file.</b> This context is deliberately NOT <c>LocalNodeDbContext</c>
/// and <see cref="OrgBrandingRow"/> is NOT a shared <c>IHarborlineEntityModule</c>: branding is node-local
/// presentation config with no Bridge EF persistence, so it stays out of the council C2 both-provider parity
/// check. It opens the SAME SQLCipher-encrypted database file as the financial store, keyed through the same
/// connection interceptor — encrypted at rest (SC-1), no plaintext path.
/// </para>
/// <para>
/// <b>Distinct migration-history table.</b> Several EF contexts each call <c>MigrateAsync</c> against one
/// SQLite file; they must not share the default <c>__EFMigrationsHistory</c> table. This context records its
/// history in <c>__OrgBrandingMigrationsHistory</c>.
/// </para>
/// </remarks>
public sealed class NodeLocalOrgBrandingDbContext : DbContext
{
    /// <summary>
    /// Dedicated migration-history table name for this context — keeps its applied-migration records out of
    /// the financial store's default <c>__EFMigrationsHistory</c> table (the contexts share one file).
    /// </summary>
    public const string MigrationsHistoryTableName = "__OrgBrandingMigrationsHistory";

    /// <summary>Initialises a new instance of <see cref="NodeLocalOrgBrandingDbContext"/>.</summary>
    public NodeLocalOrgBrandingDbContext(DbContextOptions<NodeLocalOrgBrandingDbContext> options)
        : base(options)
    {
    }

    /// <summary>The node-local org-branding profiles (one row per tenant).</summary>
    public DbSet<OrgBrandingRow> OrgBranding => Set<OrgBrandingRow>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<OrgBrandingRow>(e =>
        {
            e.ToTable("org_branding");
            // One profile per tenant — the tenant id IS the key (a foreign-tenant get returns null).
            e.HasKey(r => r.TenantId);
            e.Property(r => r.TenantId).HasColumnName("tenant_id").HasMaxLength(128);
            e.Property(r => r.DisplayName).HasColumnName("display_name").HasMaxLength(256);
            e.Property(r => r.LogoRef).HasColumnName("logo_ref").HasMaxLength(256);
            e.Property(r => r.LogoDarkRef).HasColumnName("logo_dark_ref").HasMaxLength(256);
            e.Property(r => r.AccentColor).HasColumnName("accent_color").HasMaxLength(9);
            e.Property(r => r.AccentForeground).HasColumnName("accent_foreground").HasMaxLength(9);
            e.Property(r => r.UpdatedBy).HasColumnName("updated_by").HasMaxLength(256);
            // Store the write instant as Unix epoch-MILLISECONDS (INTEGER): SQLite cannot round-trip a
            // DateTimeOffset natively; epoch-ms is lossless to ms and integer-orderable (roster precedent).
            e.Property(r => r.UpdatedAt)
                .HasColumnName("updated_at")
                .HasConversion(new ValueConverter<DateTimeOffset, long>(
                    v => v.ToUnixTimeMilliseconds(),
                    v => DateTimeOffset.FromUnixTimeMilliseconds(v)));
        });
    }
}
