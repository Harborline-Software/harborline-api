using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Leases;

/// <summary>
/// Node-exclusive EF Core <see cref="DbContext"/> for the local-authoritative
/// <c>leases</c> table (ADR 0115 D8 Stage 2 Cohort C). Maps the flat
/// <see cref="LeaseRecord"/> doctype the Harborline frontend consumes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate context, same encrypted file.</b> Deliberately NOT
/// <see cref="LocalNodeDbContext"/> and NOT a shared <c>IHarborlineEntityModule</c>
/// (kept out of the council C2 parity check; leases have no Bridge EF
/// persistence). Opens the SAME SQLCipher-encrypted file keyed through the same
/// <see cref="SqlCipherConnectionInterceptor"/> (SC-1; no plaintext path).
/// Mirrors <c>NodeLocalMaintenanceDbContext</c>.
/// </para>
/// <para>
/// <b>Distinct migration-history table.</b> Records its history in
/// <c>__LeaseMigrationsHistory</c> so the financial store's default
/// <c>__EFMigrationsHistory</c> table is untouched.
/// </para>
/// </remarks>
public sealed class NodeLocalLeaseDbContext : DbContext
{
    /// <summary>Dedicated migration-history table name for this context.</summary>
    public const string MigrationsHistoryTableName = "__LeaseMigrationsHistory";

    /// <summary>Initialises a new instance of <see cref="NodeLocalLeaseDbContext"/>.</summary>
    public NodeLocalLeaseDbContext(DbContextOptions<NodeLocalLeaseDbContext> options)
        : base(options)
    {
    }

    /// <summary>The node-local leases.</summary>
    public DbSet<LeaseRecord> Leases => Set<LeaseRecord>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<LeaseRecord>(e =>
        {
            e.ToTable("leases");
            e.HasKey(l => l.Name);
            e.Property(l => l.Name).HasColumnName("name");
            e.Property(l => l.Tenant).HasColumnName("tenant");
            e.Property(l => l.Property).HasColumnName("property");
            e.Property(l => l.Unit).HasColumnName("unit");
            e.Property(l => l.StartDate).HasColumnName("start_date");
            e.Property(l => l.EndDate).HasColumnName("end_date");
            // Stored as TEXT — SQLite has no decimal affinity; EF round-trips via
            // the invariant decimal<->string converter. The flat wire contract
            // surfaces monthly_rent as a JSON number at the route boundary.
            e.Property(l => l.MonthlyRent).HasColumnName("monthly_rent");
            e.Property(l => l.Status).HasColumnName("status");
            e.Property(l => l.Company).HasColumnName("company");
            e.Property(l => l.TermCadence).HasColumnName("term_cadence");
            e.Property(l => l.AutoRenew).HasColumnName("auto_renew");
            e.Property(l => l.CreatedAt).HasColumnName("created_at");
            e.Property(l => l.ModifiedAt).HasColumnName("modified_at");
            e.HasIndex(l => l.Status);
            e.HasIndex(l => l.Property);
        });
    }
}
