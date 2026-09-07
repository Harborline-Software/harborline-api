using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Properties;

/// <summary>
/// Node-exclusive EF Core <see cref="DbContext"/> for the local-authoritative
/// <c>properties</c> table (ADR 0115 D8 Stage 2 Cohort C). Maps the flat
/// <see cref="PropertyRecord"/> doctype the Harborline frontend consumes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate context, same encrypted file.</b> This context is deliberately
/// NOT <see cref="LocalNodeDbContext"/> and its single entity is NOT a shared
/// <c>IHarborlineEntityModule</c>: properties are node-local-authoritative and have
/// no Bridge EF persistence, so the entity must stay out of the council C2
/// both-provider parity check (which inspects only <see cref="LocalNodeDbContext"/>'s
/// injected module set). It opens the SAME SQLCipher-encrypted database file as
/// the financial store, keyed through the same
/// <see cref="SqlCipherConnectionInterceptor"/> on the connection — there is no
/// plaintext path (SC-1). Mirrors <c>NodeLocalMaintenanceDbContext</c>.
/// </para>
/// <para>
/// <b>Distinct migration-history table.</b> Several EF contexts each call
/// <c>MigrateAsync</c> against one SQLite file; they must not share the default
/// <c>__EFMigrationsHistory</c> table or they would clobber each other's
/// applied-migration records. This context records its history in
/// <c>__PropertyMigrationsHistory</c>.
/// </para>
/// </remarks>
public sealed class NodeLocalPropertyDbContext : DbContext
{
    /// <summary>
    /// Dedicated migration-history table name for this context — keeps its
    /// applied-migration records out of the financial store's default
    /// <c>__EFMigrationsHistory</c> table (the contexts share one file).
    /// </summary>
    public const string MigrationsHistoryTableName = "__PropertyMigrationsHistory";

    /// <summary>Initialises a new instance of <see cref="NodeLocalPropertyDbContext"/>.</summary>
    public NodeLocalPropertyDbContext(DbContextOptions<NodeLocalPropertyDbContext> options)
        : base(options)
    {
    }

    /// <summary>The node-local properties.</summary>
    public DbSet<PropertyRecord> Properties => Set<PropertyRecord>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<PropertyRecord>(e =>
        {
            e.ToTable("properties");
            e.HasKey(p => p.Name);
            e.Property(p => p.Name).HasColumnName("name");
            e.Property(p => p.PropertyName).HasColumnName("property_name");
            e.Property(p => p.AddressLine1).HasColumnName("address_line_1");
            e.Property(p => p.City).HasColumnName("city");
            e.Property(p => p.State).HasColumnName("state");
            e.Property(p => p.PostalCode).HasColumnName("postal_code");
            e.Property(p => p.Units).HasColumnName("units");
            e.Property(p => p.Status).HasColumnName("status");
            e.Property(p => p.Company).HasColumnName("company");
            e.Property(p => p.CreatedAt).HasColumnName("created_at");
            e.Property(p => p.ModifiedAt).HasColumnName("modified_at");
            e.HasIndex(p => p.Status);
            e.HasIndex(p => p.Company);
        });
    }
}
