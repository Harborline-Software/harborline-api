using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Maintenance;

/// <summary>
/// Node-exclusive EF Core <see cref="DbContext"/> for the local-authoritative
/// <c>maintenance_tickets</c> table (ADR 0115 D8 Stage 2; Admiral ruling
/// 2026-06-13 Option B). Maps the flat <see cref="MaintenanceTicketRecord"/>
/// doctype the Harborline frontend consumes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate context, same encrypted file.</b> This context is deliberately
/// NOT <see cref="LocalNodeDbContext"/> and its single entity is NOT a shared
/// <c>IHarborlineEntityModule</c>: maintenance is node-local-authoritative and has
/// no Bridge EF persistence, so it must stay out of the council C2 both-provider
/// parity check (which inspects only <see cref="LocalNodeDbContext"/>'s injected
/// module set). It opens the SAME SQLCipher-encrypted database file as the
/// financial store, keyed through the same
/// <see cref="SqlCipherConnectionInterceptor"/> on the connection — there is no
/// plaintext path (SC-1).
/// </para>
/// <para>
/// <b>Distinct migration-history table.</b> Two EF contexts that each call
/// <c>MigrateAsync</c> against one SQLite file must not share the default
/// <c>__EFMigrationsHistory</c> table or they would clobber each other's
/// applied-migration records. This context records its history in
/// <c>__MaintenanceMigrationsHistory</c> so the financial store's history table
/// is untouched.
/// </para>
/// </remarks>
public sealed class NodeLocalMaintenanceDbContext : DbContext
{
    /// <summary>
    /// Dedicated migration-history table name for this context — keeps its
    /// applied-migration records out of the financial store's default
    /// <c>__EFMigrationsHistory</c> table (the two contexts share one file).
    /// </summary>
    public const string MigrationsHistoryTableName = "__MaintenanceMigrationsHistory";

    /// <summary>Initialises a new instance of <see cref="NodeLocalMaintenanceDbContext"/>.</summary>
    public NodeLocalMaintenanceDbContext(DbContextOptions<NodeLocalMaintenanceDbContext> options)
        : base(options)
    {
    }

    /// <summary>The node-local maintenance tickets.</summary>
    public DbSet<MaintenanceTicketRecord> MaintenanceTickets => Set<MaintenanceTicketRecord>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<MaintenanceTicketRecord>(e =>
        {
            e.ToTable("maintenance_tickets");
            e.HasKey(t => t.Name);
            e.Property(t => t.Name).HasColumnName("name");
            e.Property(t => t.Subject).HasColumnName("subject");
            e.Property(t => t.Property).HasColumnName("property");
            e.Property(t => t.Status).HasColumnName("status");
            e.Property(t => t.Priority).HasColumnName("priority");
            e.Property(t => t.AssignedTo).HasColumnName("assigned_to");
            e.Property(t => t.Description).HasColumnName("description");
            e.Property(t => t.Resolution).HasColumnName("resolution");
            // Stored as TEXT — SQLite has no decimal affinity; EF round-trips
            // via the invariant decimal<->string converter. The flat wire
            // contract surfaces cost as a JSON number at the route boundary.
            e.Property(t => t.Cost).HasColumnName("cost");
            e.Property(t => t.CreatedAt).HasColumnName("created_at");
            e.Property(t => t.ModifiedAt).HasColumnName("modified_at");
            e.HasIndex(t => t.Status);
        });
    }
}
