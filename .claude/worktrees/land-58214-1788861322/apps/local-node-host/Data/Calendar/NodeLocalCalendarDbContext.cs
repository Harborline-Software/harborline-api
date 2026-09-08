using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Calendar;

/// <summary>
/// Node-exclusive EF Core <see cref="DbContext"/> for the local-authoritative calendar store —
/// the durable home of <c>CalendarEvent</c> series masters (<c>calendar_events</c>) and
/// <c>ResourceAvailability</c> records (<c>resource_availability</c>), the thin-slice read substrate
/// (ONR app-calendar survey 2026-06-24, inc-0). Mirrors <c>NodeLocalRosterDbContext</c> /
/// <c>NodeLocalMaintenanceDbContext</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate context, same encrypted file.</b> This context is deliberately NOT
/// <c>LocalNodeDbContext</c> and its rows are NOT shared <c>IHarborlineEntityModule</c>s: the calendar
/// store is node-local with no Bridge EF persistence, so it stays out of the council C2 both-provider
/// parity check. It opens the SAME SQLCipher-encrypted <c>local-node.db</c> file as the financial
/// store, keyed through the same connection interceptor — encrypted at rest (SC-1), no plaintext path.
/// </para>
/// <para>
/// <b>Distinct migration-history table.</b> Several EF contexts each call <c>MigrateAsync</c> against
/// one SQLite file; they must not share the default <c>__EFMigrationsHistory</c> table. This context
/// records its history in <c>__CalendarMigrationsHistory</c>.
/// </para>
/// </remarks>
public sealed class NodeLocalCalendarDbContext : DbContext
{
    /// <summary>
    /// Dedicated migration-history table name for this context — keeps its applied-migration records
    /// out of the financial store's default <c>__EFMigrationsHistory</c> table (the contexts share one
    /// file).
    /// </summary>
    public const string MigrationsHistoryTableName = "__CalendarMigrationsHistory";

    /// <summary>Initialises a new instance of <see cref="NodeLocalCalendarDbContext"/>.</summary>
    public NodeLocalCalendarDbContext(DbContextOptions<NodeLocalCalendarDbContext> options)
        : base(options)
    {
    }

    /// <summary>The node-local calendar-event series masters (JSON-on-master).</summary>
    public DbSet<NodeCalendarEventRow> CalendarEvents => Set<NodeCalendarEventRow>();

    /// <summary>The node-local resource-availability records (the bookable supply; JSON-on-master).</summary>
    public DbSet<NodeResourceAvailabilityRow> ResourceAvailability => Set<NodeResourceAvailabilityRow>();

    /// <summary>The node-local owned-calendar collections (calendar productization #149, C1; JSON-on-master).</summary>
    public DbSet<NodeCalendarRow> Calendars => Set<NodeCalendarRow>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<NodeCalendarEventRow>(e =>
        {
            e.ToTable("calendar_events");
            // Composite (tenant, id) primary key — the fleet financial-master keying. A foreign-tenant
            // get returns null because the tenant column is part of the key.
            e.HasKey(r => new { r.TenantId, r.Id });
            e.Property(r => r.TenantId).HasColumnName("tenant_id").HasMaxLength(128);
            e.Property(r => r.Id).HasColumnName("id").HasMaxLength(64);
            e.Property(r => r.SnapshotJson).HasColumnName("snapshot_json");
            // The list/agenda read scans by tenant.
            e.HasIndex(r => r.TenantId);
        });

        modelBuilder.Entity<NodeCalendarRow>(e =>
        {
            e.ToTable("calendars");
            // Composite (tenant, id) primary key — the same financial-master keying as the events table.
            // A foreign-tenant get returns null because the tenant column is part of the key.
            e.HasKey(r => new { r.TenantId, r.Id });
            e.Property(r => r.TenantId).HasColumnName("tenant_id").HasMaxLength(128);
            e.Property(r => r.Id).HasColumnName("id").HasMaxLength(64);
            e.Property(r => r.SnapshotJson).HasColumnName("snapshot_json");
            // The list read scans by tenant.
            e.HasIndex(r => r.TenantId);
        });

        modelBuilder.Entity<NodeResourceAvailabilityRow>(e =>
        {
            e.ToTable("resource_availability");
            // Composite (tenant, resource-kind, resource-value) key — one record per resource per
            // tenant, mirroring the in-memory store's keying.
            e.HasKey(r => new { r.TenantId, r.ResourceKind, r.ResourceValue });
            e.Property(r => r.TenantId).HasColumnName("tenant_id").HasMaxLength(128);
            e.Property(r => r.ResourceKind).HasColumnName("resource_kind");
            e.Property(r => r.ResourceValue).HasColumnName("resource_value").HasMaxLength(256);
            e.Property(r => r.SnapshotJson).HasColumnName("snapshot_json");
            e.HasIndex(r => r.TenantId);
        });
    }
}
